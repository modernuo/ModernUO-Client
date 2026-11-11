// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Diagnostics;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    // Mirrors World's items and mobiles into Friflo (ClassicUO.Ecs), keyed by serial. World stays the source of truth:
    // its choke points call Sync/OnDestroyed and nothing reads the mirror yet except the consistency check and the extract.
    internal sealed class WorldEcs
    {
        private const int MAX_LOGGED_MISMATCHES = 25;

        private readonly World _world;
        private readonly HashSet<uint> _live = new HashSet<uint>();
        private readonly List<uint> _extra = new List<uint>();
        private int _frame;
        private int _checks, _mismatchesTotal, _checksWithMismatches;
        private long _extractTicks, _extractMaxTicks, _extractFrames;
        private int _extractLastCount, _extractMaxCount;

        public WorldEcs(World world)
        {
            _world = world;
            Mirror = new WorldMirror();
            Extract = new MirrorSpriteExtract(Mirror.Store);
            Animation = new MobileAnimationSystem(Mirror);
            Clock = new MobileClockSystem(Mirror.Store);
            Sweep = new RangeSweepSystem(Mirror.Store);
        }

        public WorldMirror Mirror { get; }

        public MirrorSpriteExtract Extract { get; }

        public MobileAnimationSystem Animation { get; }

        /// <summary>E4: picks the mobiles whose Update has work this frame from their <see cref="MobileClock"/>.</summary>
        public MobileClockSystem Clock { get; }

        /// <summary>T5c: the range sweep's selection and the world-map allies, from Position and MobileInputs.</summary>
        public RangeSweepSystem Sweep { get; }

        // created on first use: needs the loaded animation tables
        public MobileSpriteExtract MobileExtract => _mobileExtract ??= new MobileSpriteExtract(Client.Game.UO.FileManager.Animations);

        private MobileSpriteExtract _mobileExtract;

#if CUO_DEVTOOLS
        public EcsSessionDriver Driver { get; private set; }
#endif

        public int MismatchesTotal => _mismatchesTotal;

        public int Checks => _checks;

        public void Sync(Entity entity) => Sync(entity, false);

        /// <summary>
        /// <see cref="Sync(Entity)"/>; <paramref name="detach"/> (T9 S5, <c>World.RemoveItemFromContainer</c>): an item's
        /// record is written without a container (the Container's only copy is the record).
        /// </summary>
        public void Sync(Entity entity, bool detach)
        {
            if (entity == null || entity.IsDestroyed)
            {
                return;
            }

            if (entity is Item item)
            {
                // T1: a WorldWriter operation commits this item itself, once, from its own values
                if (ReferenceEquals(item, _fold))
                {
                    return;
                }

                SyncItem(item, detach ? WorldMirror.NO_CONTAINER : item.Container);
            }
            else if (entity is Mobile mobile)
            {
                // T1 (funnel part 4): a mobile operation commits its mobile itself, likewise
                if (ReferenceEquals(mobile, _fold) || !_world.Holds(mobile))
                {
                    return;
                }

                MobileMirrorWrites++;
                uint epochBefore = Mirror.Epoch;
                int countBefore = Mirror.Count;
                EcsEntity e = Mirror.UpsertMobile
                (
                    mobile.Serial,
                    mobile.Graphic,
                    mobile.Hue,
                    mobile.X,
                    mobile.Y,
                    mobile.Z,
                    (byte)mobile.Direction,
                    (byte)mobile.Flags,
                    ReferenceEquals(mobile, _world.Player),
                    mobile
                );

                if (Mirror.LastMobileCreated)
                {
                    InitCreatedMobile(mobile, e);
                }

                RestoreScalars(mobile, epochBefore, countBefore);

                // W5: graphic, flags and direction reach the mirror only here, so the pushed inputs follow them here
                WriteInputs(mobile, e);
            }
        }

        // T1: the instance (an item, or since funnel part 4 a mobile) whose hook Syncs fold into its WorldWriter operation's
        // commit. One operation at a time: a second BeginFold before EndFold is a programming error (an operation that calls
        // another), and throws.
        private Entity _fold;

        /// <summary>
        /// T1, <see cref="WorldWriter"/>: from here until <see cref="EndFold"/>, the hook Syncs of this instance (not of its
        /// serial: a replacement instance with the same serial still syncs) are skipped, and for a mobile also its
        /// <see cref="PushInputs"/> and <see cref="PushClock"/>; the operation writes the ECS itself with
        /// <see cref="CommitItem"/> or <see cref="CommitMobile"/> and <see cref="CommitMobileInputs"/>. Not reentrant.
        /// </summary>
        internal void BeginFold(Entity entity)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (_fold != null)
            {
                throw new InvalidOperationException($"WorldEcs fold of 0x{entity.Serial:X8} inside the fold of 0x{_fold.Serial:X8}: funnel operations do not nest");
            }

            _fold = entity;
        }

        /// <summary>Ends the fold of <paramref name="entity"/>; a call for another instance (a stale scope) does nothing.</summary>
        internal void EndFold(Entity entity)
        {
            if (ReferenceEquals(_fold, entity))
            {
                _fold = null;
            }
        }

        internal bool IsFolding => _fold != null;

        /// <summary>
        /// T1, tests: sees each funnel commit before it is written, with the item's cache still as it was. The getters
        /// still return the previous values there, as they will once T4 moves them to the ECS, so a test can check that an
        /// operation computed every value instead of reading one back after its own unlink or write.
        /// </summary>
        internal Action<Item, ItemState> CommitObserver;

        /// <summary>T1, tests: every write of an item's mirror record (the hook Syncs and the funnel commits).</summary>
        internal long ItemMirrorWrites;

        /// <summary>
        /// T1: the funnel's ECS write for an item, from the operation's values (never from the GameObject's getters: at T4 those
        /// read the ECS). Ignores an item World does not hold under <see cref="ItemState.Serial"/>, as Sync does.
        /// <paramref name="attach"/> (T1 part 9): the operation puts the item at the back of its new parent's child list
        /// (<c>PushToBack</c>), so the record takes the next child-order stamp in the same write; without it a change of parent
        /// detaches (an orphan: the parent does not exist, so ClassicUO links it nowhere) and the same parent keeps its place.
        /// </summary>
        internal void CommitItem(Item item, in ItemState v, bool attach = false)
        {
            if (item == null || item.IsDestroyed || !_world.Holds(item, v.Serial))
            {
                return;
            }

            CommitObserver?.Invoke(item, v);
            ItemMirrorWrites++;
            uint epochBefore = Mirror.Epoch;
            int countBefore = Mirror.Count;
            Mirror.UpsertItem(v.Serial, v.Graphic, v.Hue, v.X, v.Y, v.Z, (byte)v.Direction, (byte)v.Flags, v.Amount, v.Container, (byte)v.Layer, v.IsMulti, attach, item);
            RestoreScalars(item, epochBefore, countBefore);

            // W5: corpses animate in World.Update (SyncItem does the same)
            if (v.Graphic == 0x2006)
            {
                _world.QueueItemUpdate(item);
            }
        }

        /// <summary>
        /// T1, tests: sees each mobile commit before it is written, with the mobile's cache still as it was (see
        /// <see cref="CommitObserver"/>).
        /// </summary>
        internal Action<Mobile, MobileState> MobileCommitObserver;

        /// <summary>T1, tests: every write of a mobile's mirror record (the hook Syncs and the funnel commits).</summary>
        internal long MobileMirrorWrites;

        /// <summary>
        /// T1 (funnel part 4): the funnel's ECS write of a mobile's record, from the operation's values (the same record
        /// <see cref="Sync"/>'s mobile branch writes from the getters). Its <see cref="MobileInputs"/> and clock are derived
        /// from the mobile's getters, so the operation pushes them after its cache writes with <see cref="CommitMobileInputs"/>.
        /// Ignores a mobile World does not hold under <see cref="MobileState.Serial"/>, as Sync does.
        /// </summary>
        /// <returns>The mobile's entity, for <see cref="CommitMobileInputs(Mobile, EcsEntity)"/> (a null entity when ignored).</returns>
        internal EcsEntity CommitMobile(Mobile mobile, in MobileState v)
        {
            if (mobile == null || mobile.IsDestroyed || !_world.Holds(mobile, v.Serial))
            {
                return default;
            }

            MobileCommitObserver?.Invoke(mobile, v);
            MobileMirrorWrites++;
            uint epochBefore = Mirror.Epoch;
            int countBefore = Mirror.Count;
            EcsEntity e = Mirror.UpsertMobile(v.Serial, v.Graphic, v.Hue, v.X, v.Y, v.Z, (byte)v.Direction, (byte)v.Flags, v.IsPlayer, mobile);

            if (Mirror.LastMobileCreated)
            {
                InitCreatedMobile(mobile, e);
            }

            RestoreScalars(mobile, epochBefore, countBefore);

            return e;
        }

        /// <summary>
        /// <see cref="CommitMobileInputs(Mobile)"/> with the entity <see cref="CommitMobile"/> returned in the same operation
        /// (no lookup; a null entity, from an ignored commit, pushes nothing).
        /// </summary>
        internal void CommitMobileInputs(Mobile mobile, EcsEntity e)
        {
            if (!e.IsNull)
            {
                WriteInputs(mobile, e);
            }
        }

        /// <summary>
        /// T1 (funnel part 4), the derived-state step of a mobile operation: recomputes the mobile's <see cref="MobileInputs"/>
        /// (and its clock) from the getters, after the operation's cache writes. Unlike <see cref="PushInputs"/> it is not
        /// folded (it is the operation's own push). Ignores mobiles World does not hold.
        /// </summary>
        internal void CommitMobileInputs(Mobile mobile)
        {
            if (mobile == null || mobile.IsDestroyed || !_world.Holds(mobile))
            {
                return;
            }

            if (Mirror.TryGetEntity(mobile.Serial, out EcsEntity e))
            {
                WriteInputs(mobile, e);
            }
        }

        // ---- -ecscheck v2 (T1 funnel part 9): the cold record fields, committed by the operations that write them, from
        // their values, before their cache writes. Each ignores an entity World does not hold (the detached player, a stale
        // instance) or that has no record.

        // T4: World holds this instance AND the record under its serial is of its kind (a death-rekeyed mobile and a corpse
        // item can share a serial; the mirror keeps one record per serial, so a commit must not write the other kind's record)
        // (T9 slice 3: one lookup, the record's handle is the instance)
        private bool HoldsEntity(Entity entity) => entity != null && _world.TryGetLiveRecord(entity, entity.Serial, out _);

        /// <summary>
        /// After every record upsert: the corpse timer push and the ItemActive tag follow the record; when the upsert replaced a
        /// record of the other kind under this serial (the epoch moved), the hot notoriety the Mobile keeps is written to it.
        /// </summary>
        private void RestoreScalars(Entity entity, uint epochBefore, int countBefore)
        {
            // T9 S5: a record the upsert created (or that replaced the other kind's) starts without traits: the record is
            // their only copy (the small-field caches and their seed are gone). A live instance's record is re-created only
            // after a misconfigured server's shared serial or 0x25 under the player's serial (the detached player); the
            // values come back with the server's next packet for them, as the cold fields do
            if (ItemAnimTimerEnabled && entity is Item corpse)
            {
                PushItemTimer(corpse);
            }

            // T5d: a listed item's tag follows its list membership whenever its record is written (a replaced record starts
            // without the tag, and ActiveItemStore.Add returns early for an item already listed)
            if (entity is Item listed && _world.IsItemListed(listed)
                && Mirror.TryGetEntity(listed.Serial, false, out EcsEntity tagged) && !tagged.Tags.Has<ItemActive>())
            {
                tagged.AddTag<ItemActive>();
            }

            if (Mirror.Epoch == epochBefore)
            {
                return;
            }

            // T9 (cold-caches; owner decision 2026-11-07): the replaced record's cold values (name, hits, pools, price) are
            // lost, as the record is their only copy. Only a misconfigured server sends an item and a mobile under one serial
            // (a death re-key moves the mobile to serial | 0x80000000, outside the item range); the values come back with the
            // server's next status or name packet for the new kind.
            if (entity is Mobile m)
            {
                Mirror.SetNotoriety(entity.Serial, (byte)m.NotorietyFlag);
            }
        }

        /// <summary>T1, tests: every cold-field commit (name, vitals, notoriety, price, child stamp) that reached a record.</summary>
        internal long ExtrasWrites;

        internal void CommitName(Entity entity, string name)
        {
            if (HoldsEntity(entity) && Mirror.SetName(entity.Serial, name))
            {
                ExtrasWrites++;
            }
        }

        internal void CommitVitals(Entity entity, ushort hits, ushort hitsMax)
        {
            if (HoldsEntity(entity) && Mirror.SetVitals(entity.Serial, hits, hitsMax))
            {
                ExtrasWrites++;
            }
        }

        /// <summary>T4: a mobile's stamina pool (StaminaMax, Stamina), in the record's <c>Vitals</c>.</summary>
        internal void CommitStamina(Mobile mobile, ushort staminaMax, ushort stamina)
        {
            if (HoldsEntity(mobile) && Mirror.SetStamina(mobile.Serial, staminaMax, stamina))
            {
                ExtrasWrites++;
            }
        }

        /// <summary>T4: a mobile's mana pool (ManaMax, Mana), in the record's <c>Vitals</c>.</summary>
        internal void CommitMana(Mobile mobile, ushort manaMax, ushort mana)
        {
            if (HoldsEntity(mobile) && Mirror.SetMana(mobile.Serial, manaMax, mana))
            {
                ExtrasWrites++;
            }
        }

        internal void CommitNotoriety(Mobile mobile, NotorietyFlag notoriety)
        {
            if (HoldsEntity(mobile) && Mirror.SetNotoriety(mobile.Serial, (byte)notoriety))
            {
                ExtrasWrites++;
            }
        }

        internal void CommitPrice(Item item, uint price)
        {
            if (HoldsEntity(item) && Mirror.SetPrice(item.Serial, price))
            {
                ExtrasWrites++;
            }
        }

        // ---- ecs_small_fields (wave 1C): the small cold fields, committed by their operations from their values before the
        // cache writes, as the fields above. Ignore an entity World does not hold (the detached player, a preview).

        internal void CommitLightId(Item item, byte lightId)
        {
            if (HoldsEntity(item) && Mirror.SetLightId(item.Serial, lightId))
            {
                ExtrasWrites++;
            }
        }

        internal void CommitItemBit(Item item, ItemTraitBits bit, bool on)
        {
            if (HoldsEntity(item) && Mirror.SetItemBit(item.Serial, bit, on))
            {
                ExtrasWrites++;
            }
        }

        internal void CommitRace(Mobile mobile, RaceType race)
        {
            if (HoldsEntity(mobile) && Mirror.SetRace(mobile.Serial, (byte)race))
            {
                ExtrasWrites++;
            }
        }

        internal void CommitSpeedMode(Mobile mobile, CharacterSpeedType mode)
        {
            if (HoldsEntity(mobile) && Mirror.SetSpeedMode(mobile.Serial, (byte)mode))
            {
                ExtrasWrites++;
            }
        }

        internal void CommitMobileBit(Mobile mobile, MobileTraitBits bit, bool on)
        {
            if (HoldsEntity(mobile) && Mirror.SetMobileBit(mobile.Serial, bit, on))
            {
                ExtrasWrites++;
            }
        }

        /// <summary>
        /// T1 part 9: the item went to the back of its parent's child list in a re-order that writes no record (the vendor
        /// sort's re-stamp; an operation that also commits the item stamps with <see cref="CommitItem"/>'s attach).
        /// </summary>
        internal void CommitChildOrder(Item item)
        {
            if (HoldsEntity(item) && Mirror.StampChild(item.Serial))
            {
                ExtrasWrites++;
            }
        }

        /// <summary>
        /// T2: a spellbook's 0xBF.1B spell set (bit i = spell i + 1), the detached entries at the front of its child list;
        /// 0 when a clear drops them (ClearContainer).
        /// </summary>
        internal void CommitSpellbook(Item book, ulong spells)
        {
            if (!HoldsEntity(book))
            {
                return;
            }

            // Codex audit P2 (#3): the entries stand for the old set (FillSpellbook hands the new ones over after this)
            _world.State.DropSpellEntries(book.Serial);

            if (Mirror.SetSpellbook(book.Serial, spells))
            {
                ExtrasWrites++;
            }
        }

        /// <summary>T1 part 9: the item left its parent's child list but keeps its Container (the spellbook's clear).</summary>
        internal void CommitDetach(Item item)
        {
            if (HoldsEntity(item) && Mirror.DetachChild(item.Serial))
            {
                ExtrasWrites++;
            }
        }

        private void SyncItem(Item item, uint container)
        {
            // Only the instance World currently holds for this serial; stale or detached objects are ignored.
            if (!_world.Holds(item))
            {
                return;
            }

            ItemMirrorWrites++;
            uint epochBefore = Mirror.Epoch;
            int countBefore = Mirror.Count;
            Mirror.UpsertItem
            (
                item.Serial,
                item.Graphic,
                item.Hue,
                item.X,
                item.Y,
                item.Z,
                (byte)item.Direction,
                (byte)item.Flags,
                item.Amount,
                container,
                (byte)item.Layer,
                item.IsMulti,
                handle: item
            );
            RestoreScalars(item, epochBefore, countBefore);

            // W5: corpses animate in World.Update; every item change reaches the mirror here
            if (item.IsCorpse)
            {
                _world.QueueItemUpdate(item);
            }
        }

        /// <summary>
        /// W5: recomputes the mobile's <see cref="MobileInputs"/> after a change that doesn't go
        /// through <see cref="Sync"/> (sex, the dead flag, notoriety, SA poison, the player's war mode, running, the step
        /// queue and the last step time). Ignores mobiles World doesn't hold, as Sync does.
        /// </summary>
        public void PushInputs(Mobile mobile)
        {
            if (mobile == null || mobile.IsDestroyed || ReferenceEquals(mobile, _fold) || !_world.Holds(mobile))
            {
                return;
            }

            if (Mirror.TryGetEntity(mobile.Serial, out EcsEntity e))
            {
                WriteInputs(mobile, e);
            }
        }

        public static long InputPushes;

        private void WriteInputs(Mobile mobile, EcsEntity e)
        {
            if (!e.HasComponent<MobileInputs>())
            {
                return;
            }

            InputPushes++;
            MobileInputSource src = MobileInputRules.From(mobile, Cv7000);
            MobileInputRules.Compute(in src, ref e.GetComponent<MobileInputs>());

            // the inputs read the step queue; the clock follows the record's timer and queue (T9 S8c: kept by their writes;
            // this also covers a new entity and the handles flag)
            WriteClock(mobile, e);
        }

        /// <summary>
        /// E4: writes the mobile's <see cref="MobileClock"/> after a change of its handles status (the scene's or the update's
        /// handles opening; the update loop after each update). T9 S8c: the due times and the Stepping flag come from the
        /// record's <see cref="AnimationTimer"/> and <see cref="Steps"/>, whose writes already keep them. Ignores mobiles World
        /// doesn't hold.
        /// </summary>
        public void PushClock(Mobile mobile)
        {
            if (mobile == null || mobile.IsDestroyed || ReferenceEquals(mobile, _fold) || !_world.Holds(mobile))
            {
                return;
            }

            if (Mirror.TryGetEntity(mobile.Serial, out EcsEntity e))
            {
                WriteClock(mobile, e);
            }
        }

        /// <summary>
        /// T1 (funnel part 6), the derived-state step of an animation operation: writes the mobile's <see cref="MobileClock"/>
        /// after the operation's writes. Unlike <see cref="PushClock"/> it is not folded (it is the operation's own push).
        /// Ignores mobiles World does not hold.
        /// </summary>
        internal void CommitMobileClock(Mobile mobile)
        {
            if (mobile == null || mobile.IsDestroyed || !_world.Holds(mobile))
            {
                return;
            }

            if (Mirror.TryGetEntity(mobile.Serial, out EcsEntity e))
            {
                WriteClock(mobile, e);
            }
        }

        public static long ClockPushes;

        /// <summary>Tests: this World's clock writes (<see cref="ClockPushes"/> counts every World's, also other tests').</summary>
        internal long ClockWrites;

        /// <summary>
        /// T9 S8c: <paramref name="e"/>'s <see cref="MobileClock"/> from its <see cref="AnimationTimer"/> (the frame and idle
        /// due times), its <see cref="Steps"/> (Stepping) and <paramref name="mobile"/>'s handles status (HandlesOpening). Every
        /// mobile record has the three components from its creation; a record without them (another kind's) is left alone.
        /// </summary>
        internal static void WriteClock(Mobile mobile, EcsEntity e)
        {
            EcsRow row = EcsRead.Row(e);

            if (row.IsNull || !row.Has<MobileClock>() || !row.Has<AnimationTimer>() || !row.Has<Steps>())
            {
                return;
            }

            ClockPushes++;

            if (mobile.World?.Ecs != null)
            {
                mobile.World.Ecs.ClockWrites++;
            }

            ref readonly AnimationTimer t = ref row.Get<AnimationTimer>();
            ref MobileClock c = ref row.Get<MobileClock>();
            c.AnimDue = t.AnimDue;
            c.IdleDue = t.IdleDue;
            c.Flags = ClockFlagsOf(row.Get<Steps>().Count, mobile);
        }

        internal static MobileClockFlags ClockFlagsOf(int steps, Mobile mobile)
        {
            MobileClockFlags f = MobileClockFlags.None;

            if (steps != 0) f |= MobileClockFlags.Stepping;
            if (mobile.ObjectHandlesStatus == ObjectHandlesStatus.OPEN) f |= MobileClockFlags.HandlesOpening;

            return f;
        }

        /// <summary>
        /// T9 S8c: a mobile record <see cref="Mirror"/> just created for <paramref name="mobile"/> starts with a new mobile's
        /// timer, queue and clock (WorldMirror); the player's frame timer starts now and its idle delay is drawn (the old
        /// PlayerMobile constructor's writes: the player's record is created right after it, World.CreatePlayer). A record
        /// re-created for a live instance (the detached player, a shared serial) starts over as well.
        /// </summary>
        private void InitCreatedMobile(Mobile mobile, EcsEntity e)
        {
            if (mobile is PlayerMobile && !e.IsNull && e.HasComponent<AnimationTimer>())
            {
                ref AnimationTimer t = ref e.GetComponent<AnimationTimer>();
                t.AnimDue = Time.Ticks;
                t.IdleDue = Mobile.NextIdleDue();
            }

            WriteClock(mobile, e);
        }

        /// <summary>
        /// T5b-items: a corpse's <see cref="AnimationTimer"/> is kept with its item record (World.UpdateActiveItems sets this
        /// each frame from <c>world_item_anim_system</c>; turning it on pushes every corpse). Only corpses carry it.
        /// </summary>
        public bool ItemAnimTimerEnabled;

        /// <summary>T5b-items: writes the corpse's timer fields into its entity's <see cref="AnimationTimer"/> (added on the
        /// first push). Nothing for another item, a destroyed or an unmirrored one.</summary>
        internal void PushItemTimer(Item item)
        {
            if (!ItemAnimTimerEnabled || item == null || item.IsDestroyed || !item.IsCorpse || !Mirror.TryGetEntity(item.Serial, false, out EcsEntity e)
                || !_world.Holds(item))
            {
                return;
            }

            if (!e.HasComponent<AnimationTimer>())
            {
                e.AddComponent(item.CorpseTimer);
            }
            else
            {
                e.GetComponent<AnimationTimer>() = item.CorpseTimer;
            }
        }

        /// <summary>T9 S8c: a mobile's step and animation state, carried from its old record to its new one (the death re-key).</summary>
        private struct MobileMotion
        {
            public AnimationTimer Timer;
            public Steps Steps;
        }

        private static bool TryTakeMotion(EcsEntity old, out MobileMotion motion)
        {
            motion = default;
            EcsRow row = EcsRead.Row(old);

            if (row.IsNull || !row.Has<AnimationTimer>() || !row.Has<Steps>())
            {
                return false;
            }

            motion.Timer = row.Get<AnimationTimer>();
            motion.Steps = row.Get<Steps>();

            return true;
        }

#if CUO_DEVTOOLS
        // CLEANUP(release): E4 stats (CUO_MOBILE_UPDATE_CHECK=1 or "stats" logs the counts; T9 S8c deleted its clock
        // comparison: the clock is written with the timer and the queue it is derived from)
        public static readonly bool MobileUpdateStats = Environment.GetEnvironmentVariable("CUO_MOBILE_UPDATE_CHECK") is "1" or "stats";
#else
        public const bool MobileUpdateStats = false;
#endif
        public static long ClockFrames, ClockDue, ClockScanned, ClockCollectTicks, ClockUpdateTicks;
        private uint _stepLogAt;

        // Client.Game.UO.Version >= CV_7000 (the rules of IsFlying, IsPoisoned, IsGargoyle); false without a client (tests)
        private static bool Cv7000 => Client.Game?.UO != null && Client.Game.UO.Version >= Utility.ClientVersion.CV_7000;

        public void OnDestroyed(Entity entity)
        {
            // a newer live object already owns this serial (T9 slice 3: World.NewerOwner, the index's handle)
            if (_world.NewerOwner(entity) != null)
            {
                return;
            }

            // T8 ownership: effects whose source or target is this record keep its last position
            if (_world.Effects?.Items != null && Mirror.TryGetEntity(entity.Serial, out EcsEntity record))
            {
                EffectSystem.OnRecordRemoving(_world, record);
            }

            // a boat's pending steps outlive its record until the next frame (BoatStepSystem.Park)
            if (entity is Item && Mirror.TryGetEntity(entity.Serial, out EcsEntity boat) && boat.TryGetComponent(out BoatSteps steps))
            {
                _world.BoatMovingManager.Steps.Park(entity.Serial, in steps);
            }

            Mirror.Remove(entity.Serial);

            // Codex audit P2 (#3): a book's spell entries go with its record
            if (entity is Item)
            {
                _world.State.DropSpellEntries(entity.Serial);
            }
        }

        /// <summary>
        /// T1 (funnel part 9), the death re-key's ECS commit from the operation's values, before its cache writes: the record
        /// under <paramref name="oldSerial"/> removed, the mobile's record written under <see cref="MobileState.Serial"/>, the
        /// persistent mirror bit carried over (the Mobile keeps IsFlipped). The children follow with <see cref="CommitItem"/>,
        /// the inputs and clock with <see cref="CommitMobileInputs(Mobile, EcsEntity)"/> after the cache writes. Writes what
        /// the old <c>OnSerialChanged</c> wrote without reading the serial or the table back: the mobile must be the one World
        /// holds under <paramref name="oldSerial"/>; a destroyed one only loses its old record, as Sync skipped it.
        /// </summary>
        /// <returns>The new entity (a null entity when no record was written).</returns>
        internal EcsEntity CommitRekey(Mobile mobile, uint oldSerial, in MobileState v)
        {
            if (mobile == null || !_world.Holds(mobile, oldSerial))
            {
                return default;
            }

            // Codex audit (t9-world-index-5): a live object other than this mobile that holds the old serial's record (a
            // shared serial: an item committed under it last) keeps it; nothing of it goes with the mobile (its mirror bit,
            // cold fields and effects are the item's), as World.NewerOwner keeps it on a destroy
            bool foreign = Mirror.GetHandle(oldSerial) is Entity owner && !ReferenceEquals(owner, mobile) && !owner.IsDestroyed;
            AnimStateFlags keep = 0;
            EcsEntity old = default;

            if (!foreign && Mirror.TryGetEntity(oldSerial, out old) && old.HasComponent<AnimationState>())
            {
                keep = old.GetComponent<AnimationState>().Flags & AnimStateFlags.Mirror;
            }

            // the cold fields (part 9) go with the mobile, as its mirror bit does
            MirrorExtras extras = default;

            if (!foreign)
            {
                Mirror.TryGetExtras(oldSerial, out extras);
            }

            // Codex audit (ecs-wave1c) P2: with no mobile record under the old serial (none, or a shared-serial item's) no
            // item record's values go with the mobile. T9 S5: the record was the traits' only copy, so they are lost there,
            // as the cold fields are (only a misconfigured server shares a serial between an item and a mobile)
            if (foreign || !Mirror.TryGetEntity(oldSerial, true, out _))
            {
                extras.MobileTraits = default;
            }

            // T9 S8c: the timer and the walk queue go with the mobile too (the record is their only copy); without its mobile
            // record under the old serial the mobile had none (its getters read a new mobile's), and its new record gets none
            MobileMotion motion = default;
            bool carry = !foreign && Mirror.TryGetEntity(oldSerial, true, out EcsEntity oldMobile) && TryTakeMotion(oldMobile, out motion);

            // T8 ownership: effects on the old record are gone with it, then follow the mobile to the new record
            bool effects = !foreign && _world.Effects?.Items != null && Mirror.TryGetEntity(oldSerial, out old);

            if (effects)
            {
                EffectSystem.OnRecordRemoving(_world, old, rekey: true);
            }

            if (!foreign)
            {
                Mirror.Remove(oldSerial);
            }

            if (mobile.IsDestroyed)
            {
                if (effects)
                {
                    EffectSystem.OnRecordRekeyed(_world, old, default, 0);
                }

                return default;
            }

            // Codex audit (ecs-effects-own) P2: an earlier death's mobile still under the new serial is displaced, and the
            // upsert below writes this mobile into its record (as OnSerialChanged did). Effects on the displaced mobile end at
            // its last position and let go of the record, so they do not follow this mobile (legacy kept following the
            // orphaned object, which stays where it was)
            if (_world.Effects?.Items != null && Mirror.TryGetEntity(v.Serial, out EcsEntity displaced))
            {
                EffectSystem.OnRecordRemoving(_world, displaced);
            }

            MobileCommitObserver?.Invoke(mobile, v);
            MobileMirrorWrites++;
            EcsEntity e = Mirror.UpsertMobile(v.Serial, v.Graphic, v.Hue, v.X, v.Y, v.Z, (byte)v.Direction, (byte)v.Flags, v.IsPlayer, mobile);
            Mirror.CarryMobileExtras(in extras, v.Serial);

            if (effects)
            {
                EffectSystem.OnRecordRekeyed(_world, old, e, v.Serial);
            }

            if (keep != 0 && e.HasComponent<AnimationState>())
            {
                e.GetComponent<AnimationState>().Flags |= keep;
            }

            // the upsert may have written into a displaced earlier death's record: its timer and queue are not this mobile's
            if (!carry)
            {
                motion.Timer = AnimationTimer.MobileDefault;
            }

            if (e.HasComponent<AnimationTimer>() && e.HasComponent<Steps>())
            {
                e.GetComponent<AnimationTimer>() = motion.Timer;
                e.GetComponent<Steps>() = motion.Steps;
                WriteClock(mobile, e);
            }

            return e;
        }

        // T9 S4: the detached preview objects' records (see Previews); created at the first preview write
        private WorldMirror _previews;

        /// <summary>
        /// T9 S4: the records of the detached preview objects (the character-creation and race-change gumps' Mobile and the
        /// Items they wear), written only by <see cref="WorldWriter"/>'s preview operations. A mirror of its own, not
        /// <see cref="Mirror"/>: no system, extract, sweep or index walks it, so a preview never draws, updates or answers a
        /// world lookup, and its serials (1 or 0, 0x4000_0000 + layer) cannot clash with a world record. Keyed by serial
        /// with the instance as the handle (<see cref="TryGetPreviewRecord"/> checks it). Null until the first preview write.
        /// </summary>
        public WorldMirror Previews => _previews;

        /// <summary>
        /// T9 S4: the preview record of <paramref name="entity"/> (a detached instance last committed by a preview operation).
        /// False for every other instance, and for a preview whose record was released or taken by another instance under
        /// the same serial (two race-change gumps at once: the newer one's objects own the serials).
        /// </summary>
        internal bool TryGetPreviewRecord(Entity entity, out EcsEntity e)
        {
            e = default;

            return _previews != null && entity != null && _previews.TryGetEntityOf(entity.Serial, entity, out e);
        }

        /// <summary>T9 S4: a preview mobile's record from the operation's values (body, hue, flags, and the traits).</summary>
        internal void CommitPreviewMobile(object owner, Mobile mobile, in MobileState v, in MobileTraits traits)
        {
            WorldMirror previews = PreviewsFor(owner, mobile);
            previews.UpsertMobile(v.Serial, v.Graphic, v.Hue, v.X, v.Y, v.Z, (byte)v.Direction, (byte)v.Flags, v.IsPlayer, mobile);
            previews.SetMobileTraits(v.Serial, in traits);
        }

        // a preview gump holds one mobile and a few dozen items at most
        private const int PREVIEW_CAPACITY = 32;

        // T9 S4 (Codex audit P2): the instances each owner (a preview gump) committed, so closing one gump releases only its
        // own records; another open gump's, or a newer gump's under the same serials, stay. By reference: Entity's Equals
        // compares serials, and a gump's replaced item has its predecessor's serial
        private Dictionary<object, HashSet<Entity>> _previewOwners;

        private WorldMirror PreviewsFor(object owner, Entity entity)
        {
            _previews ??= new WorldMirror(PREVIEW_CAPACITY);
            _previewOwners ??= new Dictionary<object, HashSet<Entity>>(ReferenceEqualityComparer.Instance);

            if (!_previewOwners.TryGetValue(owner, out HashSet<Entity> owned))
            {
                owned = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
                _previewOwners.Add(owner, owned);
            }

            owned.Add(entity);

            return _previews;
        }

        /// <summary>
        /// T9 S4: a preview item's record from the operation's values. Never attached (no child-order stamp): the gumps keep
        /// the worn table themselves (<see cref="Mobile.Preview"/>, the race-change gump's hair and beard fields).
        /// </summary>
        internal void CommitPreviewItem(object owner, Item item, in ItemState v)
        {
            PreviewsFor(owner, item).UpsertItem(v.Serial, v.Graphic, v.Hue, v.X, v.Y, v.Z, (byte)v.Direction, (byte)v.Flags, v.Amount, v.Container, (byte)v.Layer, v.IsMulti, attach: false, handle: item);
        }

        /// <summary>
        /// T9 S4: drops the records of the instances <paramref name="owner"/> (a preview gump that closed) committed, so its
        /// objects stay reachable no longer. A record a newer instance took over (the same serial, another gump) stays, and
        /// a second call for the same owner finds nothing.
        /// </summary>
        internal void ReleasePreviews(object owner)
        {
            if (_previews == null || owner == null || !_previewOwners.Remove(owner, out HashSet<Entity> owned))
            {
                return;
            }

            foreach (Entity entity in owned)
            {
                if (_previews.TryGetEntityOf(entity.Serial, entity, out _))
                {
                    _previews.Remove(entity.Serial);
                }
            }
        }

        public void OnWorldCleared()
        {
            _world.State.OnWorldCleared();

            if (Mirror.Count != 0)
            {
                Log.Warn($"[ECS] {Mirror.Count} entities left after World.Clear; resetting mirror");
            }

            // (also with no record left: the contents cache can hold slots of parents that had none; Codex audit)
            Mirror.Clear();
        }

        private uint _cacheLogAt, _inputLogAt, _clockLogAt, _animAdvanceLogAt;

#if CUO_DEVTOOLS
        internal void AttachDriver()
        {
            if (Driver == null && CUOEnviroment.EcsDriverScript != null)
            {
                Driver = EcsSessionDriver.Load(CUOEnviroment.EcsDriverScript);
                CUOEnviroment.EcsDriverScript = null;
            }

            Driver?.EnsureAttached(_world);
        }
#endif

        public void Update()
        {
            FunnelCheck.LogSummary();
#if CUO_DEVTOOLS
            // CUO_LAYER_CACHE_CHECK's 5 s summary
            Data.LayerCacheCheck.LogSummary();
            // CUO_ANIM_FRAME_COUNT_CHECK's 5 s summary
            AnimFrameCount.LogSummary();
#endif

#if CUO_DEVTOOLS
            if (MobileAnimationSystem.CacheCheck && Time.Ticks >= _cacheLogAt)
            {
                _cacheLogAt = Time.Ticks + 5000;
                Log.Info($"[mobile-cache-check] selections reused {MobileFrameCache.SelectHits}, run {MobileFrameCache.SelectMisses}; checked {MobileAnimationSystem.CheckSelects}, {MobileAnimationSystem.CheckSelectDiffs} different | instances replayed {MobileFrameCache.EmitHits}, emitted {MobileFrameCache.EmitMisses}; checked {MobileSpriteExtract.CheckReplays}, {MobileSpriteExtract.CheckReplayDiffs} different | legacy calls replayed {MobileFrameCache.LegacyHits}, selected {MobileFrameCache.LegacyMisses}; checked {MobileSpriteExtract.CheckLegacyReplays}, {MobileSpriteExtract.CheckLegacyReplayDiffs} different");
            }
#endif

            if (MobileUpdateStats && Time.Ticks >= _clockLogAt)
            {
                _clockLogAt = Time.Ticks + 5000;
                long f = Math.Max(1, ClockFrames);
                Log.Info($"[mobile-update-check] frames {ClockFrames}: due {ClockDue / (double)f:F1} of {ClockScanned / (double)f:F0} mobiles per frame; clock writes {ClockPushes}; per frame: scan {ClockCollectTicks * 1000.0 / Stopwatch.Frequency / f:F4} ms, due updates {ClockUpdateTicks * 1000.0 / Stopwatch.Frequency / f:F4} ms");
            }

            if (ItemAnimationAdvanceSystem.AdvanceCheck && Time.Ticks >= _animAdvanceLogAt)
            {
                _animAdvanceLogAt = Time.Ticks + 5000;
                Log.Info(MobileAnimationAdvanceSystem.Summary());
                Log.Info(ItemAnimationAdvanceSystem.Summary());
            }

            // T9 S8c: the step counters with the clock stats (CUO_STEP_CHECK and CUO_STEP_MOUNT_CHECK are gone)
            if (MobileUpdateStats && Time.Ticks >= _stepLogAt)
            {
                _stepLogAt = Time.Ticks + 5000;
                Log.Info(StepSystem.Summary());
            }

            if ((MobileAnimationSystem.InputCheck || MobileAnimationSystem.InputBench) && Time.Ticks >= _inputLogAt)
            {
                _inputLogAt = Time.Ticks + 5000;

                if (MobileAnimationSystem.InputBench && MobileAnimationSystem.BenchSamples > 0)
                {
                    double ns = 1e9 / Stopwatch.Frequency;
                    long seats = Math.Max(1, MobileAnimationSystem.BenchSeats);
                    Log.Info($"[mobile-input-bench] samples {MobileAnimationSystem.BenchSamples}: legacy {MobileAnimationSystem.BenchLegacyTicks * ns / MobileAnimationSystem.BenchSamples:F1} ns, pushed {MobileAnimationSystem.BenchPushedTicks * ns / MobileAnimationSystem.BenchSamples:F1} ns | seat tests {MobileAnimationSystem.BenchSeats}: legacy {MobileAnimationSystem.BenchSeatLegacyTicks * ns / seats:F1} ns, pushed {MobileAnimationSystem.BenchSeatPushedTicks * ns / seats:F1} ns");
                }

                if (MobileAnimationSystem.InputBench && MobileAnimationSystem.BenchClockCount > 0)
                {
                    double cns = 1e9 / Stopwatch.Frequency;
                    long cn = MobileAnimationSystem.BenchClockCount;
                    Log.Info($"[mobile-clock-bench] reads {cn}: from the Mobile {MobileAnimationSystem.BenchClockMobileTicks * cns / cn:F2} ns, from the fetched inputs {MobileAnimationSystem.BenchClockInputsTicks * cns / cn:F2} ns, with a fresh lookup {MobileAnimationSystem.BenchClockLookupTicks * cns / cn:F2} ns");
                }

                Log.Info($"[mobile-input-check] pushed samples checked {MobileAnimationSystem.InputChecks}, {MobileAnimationSystem.InputDiffs} different; seat tests checked {MobileAnimationSystem.SeatChecks}, {MobileAnimationSystem.SeatDiffs} different; pushes {InputPushes}");
            }

#if CUO_DEVTOOLS
            // before the -ecscheck guard: a driver run with the checks off (-ecscheck 0, e.g. FrameStats counter jobs) must still play
            AttachDriver();
#endif

            int interval = CUOEnviroment.EcsCheckFrames;

            if (interval <= 0)
            {
                return;
            }

            if (_world.Player != null)
            {
                long t0 = Stopwatch.GetTimestamp();
                Extract.Run(_world.Player.X, _world.Player.Y, _world.ClientViewRange);
                long dt = Stopwatch.GetTimestamp() - t0;
                _extractTicks += dt;
                _extractFrames++;
                _extractLastCount = Extract.Sprites.Length;

                // the first frames include JIT and buffer growth
                if (dt > _extractMaxTicks && _extractFrames > 120)
                {
                    _extractMaxTicks = dt;
                }

                if (_extractLastCount > _extractMaxCount)
                {
                    _extractMaxCount = _extractLastCount;
                }
            }

            if (++_frame >= interval)
            {
                _frame = 0;
                RunCheck(false);
            }
        }

        public int RunCheck(bool forceSummary)
        {
            int mismatches = 0;
            int items = 0, mobiles = 0;
            _live.Clear();

            foreach (Item item in _world.Items.Values)
            {
                if (item.IsDestroyed)
                {
                    continue;
                }

                items++;
                _live.Add(item.Serial);
                CompareItem(item, ref mismatches);
                CompareItemExtras(item, ref mismatches);
            }

            foreach (Mobile mobile in _world.Mobiles.Values)
            {
                if (mobile.IsDestroyed)
                {
                    continue;
                }

                mobiles++;
                _live.Add(mobile.Serial);
                CompareMobile(mobile, ref mismatches);
                CompareMobileExtras(mobile, ref mismatches);
                CompareAnimation(mobile, ref mismatches);
            }

            _extra.Clear();

            foreach (uint serial in Mirror.Serials)
            {
                if (!_live.Contains(serial))
                {
                    _extra.Add(serial);
                }
            }

            foreach (uint serial in _extra)
            {
                Mirror.TryGet(serial, out MirrorRecord r);
                Report(ref mismatches, $"0x{serial:X8} in ECS but not in World (mobile={r.IsMobile} g=0x{r.Graphic:X4} parent=0x{r.Parent:X8})");
            }

            if (Mirror.ItemCount != items || Mirror.MobileCount != mobiles)
            {
                Report(ref mismatches, $"counts: World items={items} mobiles={mobiles}, ECS items={Mirror.ItemCount} mobiles={Mirror.MobileCount}");
            }

            _checks++;
            _mismatchesTotal += mismatches;

            if (mismatches != 0)
            {
                _checksWithMismatches++;
            }

            if (mismatches != 0 || forceSummary || _checks % 30 == 1)
            {
                double us = _extractFrames == 0 ? 0 : _extractTicks * 1_000_000.0 / Stopwatch.Frequency / _extractFrames;
                double maxUs = _extractMaxTicks * 1_000_000.0 / Stopwatch.Frequency;

                Log.Info
                (
                    $"[ECS] check #{_checks}: map={_world.MapIndex} pos=({_world.Player?.X},{_world.Player?.Y}) items={items} mobiles={mobiles} ecs={Mirror.Count} mismatches={mismatches} " +
                    $"(total {_mismatchesTotal} in {_checksWithMismatches} checks) | extract avg={us:F2}us max={maxUs:F1}us " +
                    $"sprites={_extractLastCount} (items {Extract.ItemCount}, mobiles {Extract.MobileCount}) max={_extractMaxCount} frames={_extractFrames}"
                );
            }

            return mismatches;
        }

        private void CompareItem(Item item, ref int mismatches)
        {
            if (!Mirror.TryGet(item.Serial, out MirrorRecord r))
            {
                Report(ref mismatches, $"item 0x{item.Serial:X8} g=0x{item.Graphic:X4} container=0x{item.Container:X8} missing in ECS");

                return;
            }

            bool onGround = item.Container == WorldMirror.NO_CONTAINER || item.Container == 0;
            ParentKind kind = onGround ? ParentKind.None : WorldMirror.IsWearerSerial(item.Container) ? ParentKind.Equipped : ParentKind.Contained;

            // T9 S5/S6/S9: Amount, Container, Direction, Flags, Graphic and Hue read the record (nothing to compare). T9 S11: left
            // are the position cache and IsMulti against the record, and the record's own consistency: the OnGround tag and the
            // parent kind against its ContainedIn/EquippedOn parent, EquippedOn's layer against ItemData's
            if (r.IsMobile || r.X != item.X || r.Y != item.Y || r.Z != item.Z || r.IsMulti != item.IsMulti ||
                r.OnGround != onGround || r.ParentKind != kind ||
                (kind == ParentKind.Equipped && r.Layer != (byte)item.Layer))
            {
                Report
                (
                    ref mismatches,
                    $"item 0x{item.Serial:X8}: World g=0x{item.Graphic:X4} h=0x{item.Hue:X4} ({item.X},{item.Y},{item.Z}) dir={(byte)item.Direction} " +
                    $"f=0x{(byte)item.Flags:X2} amt={item.Amount} multi={item.IsMulti} cont=0x{item.Container:X8} layer={(byte)item.Layer} | " +
                    $"ECS g=0x{r.Graphic:X4} h=0x{r.Hue:X4} ({r.X},{r.Y},{r.Z}) dir={r.Facing} f=0x{r.Flags:X2} amt={r.Amount} multi={r.IsMulti} " +
                    $"{r.ParentKind} parent=0x{r.Parent:X8} layer={r.Layer} mobile={r.IsMobile}"
                );
            }
        }

        // -ecscheck v2 (T1 funnel part 9): an item's health-bar percentage. The name, hits and price are record-only since T9,
        // the layer since T9 S5 (no cache to compare).
        private void CompareItemExtras(Item item, ref int mismatches)
        {
            if (!Mirror.TryGetExtras(item.Serial, out MirrorExtras x))
            {
                return; // CompareItem reported it missing
            }

            if (StalePercentage(item, in x))
            {
                Report
                (
                    ref mismatches,
                    $"item 0x{item.Serial:X8} v2: World layer={(byte)item.Layer} hits%={item.HitsPercentage} | " +
                    $"ECS layer={x.ItemLayer} hits={x.Hits}/{x.HitsMax}"
                );
            }
        }

        // -ecscheck v2: a mobile's notoriety (the hot cache) and, T9, its health-bar percentage
        private void CompareMobileExtras(Mobile mobile, ref int mismatches)
        {
            if (!Mirror.TryGetExtras(mobile.Serial, out MirrorExtras x))
            {
                return;
            }

            if (x.Notoriety != (byte)mobile.NotorietyFlag || StalePercentage(mobile, in x))
            {
                Report
                (
                    ref mismatches,
                    $"mobile 0x{mobile.Serial:X8} v2: World noto={(byte)mobile.NotorietyFlag} hits%={mobile.HitsPercentage} | " +
                    $"ECS noto={x.Notoriety} hits={x.Hits}/{x.HitsMax}"
                );
            }
        }

        // T9: the funnel's hits operations set HitsPercentage from the values they commit; with HitsMax 0 it keeps the old one
        private static bool StalePercentage(Entity entity, in MirrorExtras x) =>
            x.HitsMax > 0 && entity.HitsPercentage != Entity.HitsPercent(x.Hits, x.HitsMax);

        private static string Quote(string s) => s == null ? "null" : $"\"{s}\"";

        private void CompareMobile(Mobile mobile, ref int mismatches)
        {
            if (!Mirror.TryGet(mobile.Serial, out MirrorRecord r))
            {
                Report(ref mismatches, $"mobile 0x{mobile.Serial:X8} g=0x{mobile.Graphic:X4} missing in ECS");

                return;
            }

            bool isPlayer = ReferenceEquals(mobile, _world.Player);

            // T9 S6/S9: Direction, Flags, Graphic and Hue read the record (nothing to compare)
            if (!r.IsMobile || r.IsPlayer != isPlayer || r.X != mobile.X || r.Y != mobile.Y || r.Z != mobile.Z)

            {
                Report
                (
                    ref mismatches,
                    $"mobile 0x{mobile.Serial:X8}: World g=0x{mobile.Graphic:X4} h=0x{mobile.Hue:X4} ({mobile.X},{mobile.Y},{mobile.Z}) " +
                    $"dir={(byte)mobile.Direction} f=0x{(byte)mobile.Flags:X2} player={isPlayer} | ECS g=0x{r.Graphic:X4} h=0x{r.Hue:X4} " +
                    $"({r.X},{r.Y},{r.Z}) dir={r.Facing} f=0x{r.Flags:X2} player={r.IsPlayer} mobile={r.IsMobile}"
                );
            }
        }

        // EquipmentSlots vs MobileView's layer walk, AnimationState's action/frame vs the Mobile's
        private void CompareAnimation(Mobile mobile, ref int mismatches)
        {
            if (_world.Player == null || !Mirror.TryGetEntity(mobile.Serial, out EcsEntity e) || !e.HasComponent<AnimationState>())
            {
                return;
            }

            CompareInputs(mobile, e, ref mismatches);

            // The mirror bit is history (GetAnimDirection keeps it for directions 3 and 7) and MobileView advances it only
            // on frames the mobile is drawn; the check must not advance it on frames the view skips.
            AnimStateFlags mirrorBefore = e.GetComponent<AnimationState>().Flags & AnimStateFlags.Mirror;

            if (!Animation.Update(_world, mobile))
            {
                return;
            }

            try
            {
                CompareAnimation(mobile, e, ref mismatches);
            }
            finally
            {
                ref AnimationState restore = ref e.GetComponent<AnimationState>();
                restore.Flags = restore.Flags & ~AnimStateFlags.Mirror | mirrorBefore;
            }
        }

        // W5: the pushed MobileInputs vs a fresh computation from the Mobile (a missed push leaves them stale)
        private void CompareInputs(Mobile mobile, EcsEntity e, ref int mismatches)
        {
            if (!e.HasComponent<MobileInputs>())
            {
                Report(ref mismatches, $"mobile 0x{mobile.Serial:X8} has no MobileInputs");

                return;
            }

            MobileInputs pushed = e.GetComponent<MobileInputs>();
            MobileInputs fresh = default;
            MobileInputSource src = MobileInputRules.From(mobile, Cv7000);
            MobileInputRules.Compute(in src, ref fresh);

            if (pushed.Flags != fresh.Flags || pushed.Body != fresh.Body || pushed.Facing != fresh.Facing || pushed.Notoriety != fresh.Notoriety ||
                pushed.HueBits != fresh.HueBits || pushed.LastStepTime != fresh.LastStepTime || pushed.WalkingDelay != fresh.WalkingDelay ||
                pushed.SpeedMode != fresh.SpeedMode)
            {
                Report
                (
                    ref mismatches,
                    $"inputs of 0x{mobile.Serial:X8} g=0x{mobile.Graphic:X4}: pushed {pushed.Flags} body 0x{pushed.Body:X4} facing {pushed.Facing} noto {pushed.Notoriety} " +
                    $"hue {pushed.HueBits} step {pushed.LastStepTime} | fresh {fresh.Flags} body 0x{fresh.Body:X4} facing {fresh.Facing} noto {fresh.Notoriety} " +
                    $"hue {fresh.HueBits} step {fresh.LastStepTime}; speed mode {pushed.SpeedMode} | {fresh.SpeedMode}"
                );
            }
        }

        private void CompareAnimation(Mobile mobile, EcsEntity e, ref int mismatches)
        {

            ref readonly EquipmentSlots eq = ref e.GetComponent<EquipmentSlots>();
            ref readonly AnimationState s = ref e.GetComponent<AnimationState>();
            bool gargoyle = AnimationRules.IsGargoyleDrawBody(mobile.Graphic, Client.Game.UO.Version >= Utility.ClientVersion.CV_7000);

            // MobileView's answer from the GameObject side: the layering rules on the wearer's items (T3b)
            Span<Layer> layers = stackalloc Layer[PaperdollOrder.N];
            Span<uint> expected = stackalloc uint[PaperdollOrder.N];
            LayerTables tables = default;
            tables.Gather(mobile);
            Uo.Pack.LayerSubject subject = LayerRuleEval.Subject(mobile.Graphic, mobile.IsFemale, gargoyle);
            int count = LayerRuleEval.BuildInWorld(tables.Anim, subject, 1, layers);
            int n = 0;

            if (mobile.IsHuman)
            {
                for (int i = 0; i < count; i++)
                {
                    Item item = mobile.FindItemByLayer(layers[i]);

                    if (item == null || mobile.IsDead && (layers[i] == Layer.Hair || layers[i] == Layer.Beard) ||
                        LayerRuleEval.IsCovered(Uo.Pack.LayerRuleSet.World, layers[i], ref tables, subject) || item.ItemData.AnimID == 0)
                    {
                        continue;
                    }

                    expected[n++] = item.Serial;
                }
            }

            Span<byte> order = stackalloc byte[EquipmentSlots.MAX];
            int got = AnimationRules.PaintOrder(in eq, 1, order);
            bool same = got == n;

            for (int i = 0; same && i < n; i++)
            {
                same = eq.Slots[order[i]].Item == expected[i];
            }

            uint mount = mobile.FindItemByLayer(Layer.Mount)?.Serial ?? 0;
            AnimGroupInput legacy = mobile.GetAnimGroupInput();

            if (!same || eq.MountItem != mount || eq.Mounted != mobile.IsMounted || !legacy.Hand1.Equals(eq.Hand1) || !legacy.Hand2.Equals(eq.Hand2))
            {
                var sb = new System.Text.StringBuilder();

                for (int i = 0; i < n; i++)
                {
                    sb.Append($" 0x{expected[i]:X8}");
                }

                sb.Append(" | ecs");

                for (int i = 0; i < got; i++)
                {
                    sb.Append($" 0x{eq.Slots[order[i]].Item:X8}");
                }

                Report(ref mismatches, $"equipment of 0x{mobile.Serial:X8} g=0x{mobile.Graphic:X4}: World{sb} mount 0x{mount:X8}/0x{eq.MountItem:X8}");
            }

            // a seated mobile draws the stand action's frame 0 whatever the Mobile's action and frame are
            bool seated = (s.Flags & AnimStateFlags.Sitting) != 0;

            if (s.Body != mobile.GetGraphicForAnimation() ||
                !seated && (s.Frame != mobile.AnimIndex || s.Group != Mobile.GetGroupForAnimation(mobile, s.Body, true)))
            {
                Report(ref mismatches, $"animation of 0x{mobile.Serial:X8}: World body=0x{mobile.GetGraphicForAnimation():X4} frame={mobile.AnimIndex} | ECS body=0x{s.Body:X4} group={s.Group} frame={s.Frame}");
            }
        }

        /// <summary>Tests: every mismatch the check reports (logged or not).</summary>
        internal Action<string> MismatchObserver;

        private void Report(ref int mismatches, string message)
        {
            MismatchObserver?.Invoke(message);

            if (mismatches++ < MAX_LOGGED_MISMATCHES)
            {
                Log.Warn($"[ECS] mismatch: {message}");
            }
        }
    }

    /// <summary>
    /// T1: the ECS-owned values of a world item as a funnel operation computed them. The operation writes the ECS
    /// (<see cref="WorldEcs.CommitItem"/>) and the GameObject cache from this, never by reading one back from the other.
    /// </summary>
    internal struct ItemState
    {
        public uint Serial;
        public ushort Graphic, Hue, X, Y, Amount;
        public sbyte Z;
        public Direction Direction;
        public ClassicUO.Game.Data.Flags Flags;
        public uint Container;
        public Layer Layer;
        public bool IsMulti;
    }

    /// <summary>
    /// T1 (funnel part 4): the mirror record of a mobile as a funnel operation computed it (<see cref="WorldEcs.CommitMobile"/>).
    /// </summary>
    internal struct MobileState
    {
        public uint Serial;
        public ushort Graphic, Hue, X, Y;
        public sbyte Z;
        public Direction Direction;
        public ClassicUO.Game.Data.Flags Flags;
        public bool IsPlayer;
    }
}
