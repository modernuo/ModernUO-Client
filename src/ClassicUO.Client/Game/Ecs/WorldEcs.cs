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
            Extract = new DynamicSpriteExtract(Mirror.Store);
            Animation = new MobileAnimationSystem(Mirror);
            Clock = new MobileClockSystem(Mirror.Store);
        }

        public WorldMirror Mirror { get; }

        public DynamicSpriteExtract Extract { get; }

        public MobileAnimationSystem Animation { get; }

        /// <summary>E4: picks the mobiles whose Update has work this frame from their <see cref="MobileClock"/>.</summary>
        public MobileClockSystem Clock { get; }

        // created on first use: needs the loaded animation tables
        public MobileSpriteExtract MobileExtract => _mobileExtract ??= new MobileSpriteExtract(Client.Game.UO.FileManager.Animations);

        private MobileSpriteExtract _mobileExtract;

#if CUO_DEVTOOLS
        public EcsSessionDriver Driver { get; private set; }
#endif

        public int MismatchesTotal => _mismatchesTotal;

        public int Checks => _checks;

        public void Sync(Entity entity)
        {
            if (entity == null || entity.IsDestroyed)
            {
                return;
            }

            if (entity is Item item)
            {
                // T1: a WorldWriter operation commits this item itself, once, after its last write
                if (_folding && item.Serial == _foldSerial)
                {
                    return;
                }

                SyncItem(item);
            }
            else if (entity is Mobile mobile)
            {
                if (!ReferenceEquals(_world.Mobiles.Get(mobile.Serial), mobile))
                {
                    return;
                }

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
                    ReferenceEquals(mobile, _world.Player)
                );

                // W5: graphic, flags and direction reach the mirror only here, so the pushed inputs follow them here
                WriteInputs(mobile, e);
            }
        }

        // T1: the item whose hook Syncs fold into its WorldWriter operation's commit (one operation at a time; they don't nest)
        private bool _folding;
        private uint _foldSerial;

        /// <summary>T1, <see cref="WorldWriter"/>: from here until <see cref="EndFold"/>, Syncs of the item with this serial are
        /// skipped; the operation calls <see cref="Commit"/> once at the point the last of them would have run.</summary>
        internal void BeginFold(uint serial)
        {
            _foldSerial = serial;
            _folding = true;
        }

        internal void EndFold()
        {
            _folding = false;
        }

        /// <summary>T1: the funnel's ECS write for an item: what <see cref="Sync"/> does, with no fold check.</summary>
        internal void Commit(Item item)
        {
            if (item != null && !item.IsDestroyed)
            {
                SyncItem(item);
            }
        }

        private void SyncItem(Item item)
        {
            // Only the instance World currently holds for this serial; stale or detached objects are ignored.
            if (!ReferenceEquals(_world.Items.Get(item.Serial), item))
            {
                return;
            }

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
                item.Container,
                (byte)item.Layer,
                item.IsMulti
            );

            // W5: corpses animate in World.Update; every item change reaches the mirror here
            if (item.IsCorpse)
            {
                _world.QueueItemUpdate(item);
            }
        }

        /// <summary>
        /// W5 (mobile_push_inputs): recomputes the mobile's <see cref="MobileInputs"/> after a change that doesn't go
        /// through <see cref="Sync"/> (sex, the dead flag, notoriety, SA poison, the player's war mode, running, the step
        /// queue and the last step time). Ignores mobiles World doesn't hold, as Sync does.
        /// </summary>
        public void PushInputs(Mobile mobile)
        {
            if (mobile == null || mobile.IsDestroyed || !ReferenceEquals(_world.Mobiles.Get(mobile.Serial), mobile))
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

            // every step-queue change and every new entity passes here, so the clock's Stepping flag follows
            WriteClock(mobile, e);
        }

        /// <summary>
        /// E4: copies the Mobile's due times and update flags into its <see cref="MobileClock"/>. Called from every write
        /// of those values that happens outside <c>Mobile.Update</c> (SetAnimation, the idle timer, the step queue, the
        /// handles opening, entity creation) and after each Update the system runs. Ignores mobiles World doesn't hold.
        /// </summary>
        public void PushClock(Mobile mobile)
        {
            if (!ClockEnabled || mobile == null || mobile.IsDestroyed || !ReferenceEquals(_world.Mobiles.Get(mobile.Serial), mobile))
            {
                return;
            }

            if (Mirror.TryGetEntity(mobile.Serial, out EcsEntity e))
            {
                WriteClock(mobile, e);
            }
        }

        public static long ClockPushes;

        /// <summary>
        /// E4: the clock is kept only while the mobile system runs (World.Update sets this each frame from
        /// <c>world_update_mobile_system</c> and <c>world_update_active_items</c>). With it off there is no component, no write,
        /// no lookup and no destroyed queue; turning it on builds every clock from the Mobiles (<see cref="ResyncClocks"/>).
        /// </summary>
        public bool ClockEnabled;

        private void WriteClock(Mobile mobile, EcsEntity e)
        {
            if (!ClockEnabled)
            {
                return;
            }

            if (!e.HasComponent<MobileClock>())
            {
                // not at creation (CreateEntity takes 11 arguments at most) and only while the system runs
                e.AddComponent(new MobileClock());
            }

            ClockPushes++;
            ref MobileClock c = ref e.GetComponent<MobileClock>();
            c.AnimDue = mobile.LastAnimationChangeTime;
            c.IdleDue = mobile.IdleDue;
            c.Flags = ClockFlagsOf(mobile);
        }

        internal static MobileClockFlags ClockFlagsOf(Mobile mobile)
        {
            MobileClockFlags f = MobileClockFlags.None;

            if (mobile.Steps.Count != 0) f |= MobileClockFlags.Stepping;
            if (mobile.ObjectHandlesStatus == ObjectHandlesStatus.OPEN) f |= MobileClockFlags.HandlesOpening;

            return f;
        }

        // E4 checker (CUO_MOBILE_UPDATE_CHECK=1; "stats" only logs the counts)
        public static readonly bool MobileUpdateCheck = Environment.GetEnvironmentVariable("CUO_MOBILE_UPDATE_CHECK") == "1";
        public static readonly bool MobileUpdateStats = MobileUpdateCheck || Environment.GetEnvironmentVariable("CUO_MOBILE_UPDATE_CHECK") == "stats";
        public static long ClockFrames, ClockDue, ClockScanned, ClockCollectTicks, ClockUpdateTicks;
        public static long ClockChecks, ClockDiffs, SkippedChecks, SkippedDiffs;
        private int _clockLogged;

        /// <summary>
        /// E4 checker, start of the frame: every live Mobile has a clock entity and its clock equals what the Mobile holds.
        /// A mismatch is a missed push (a write of the due times or flags that didn't reach the ECS).
        /// </summary>
        public void CheckClocks()
        {
            foreach (Mobile m in _world.Mobiles.Values)
            {
                if (m.IsDestroyed)
                {
                    continue;
                }

                ClockChecks++;

                if (!Mirror.TryGetEntity(m.Serial, out EcsEntity e) || !e.HasComponent<MobileClock>())
                {
                    ClockDiffs++;
                    LogClockDiff($"mobile 0x{m.Serial:X8} has no clock entity");

                    continue;
                }

                MobileClock c = e.GetComponent<MobileClock>();

                // HandlesOpening may be left set after the scene or the name gump cancelled the OPEN: the next Update clears it
                // (an extra update, never a missed one), so only the other flags and a missing HandlesOpening count
                MobileClockFlags want = ClockFlagsOf(m);
                MobileClockFlags have = c.Flags;

                if ((have & MobileClockFlags.HandlesOpening) != 0 && (want & MobileClockFlags.HandlesOpening) == 0)
                {
                    have &= ~MobileClockFlags.HandlesOpening;
                }

                if (c.AnimDue != m.LastAnimationChangeTime || c.IdleDue != m.IdleDue || have != want)
                {
                    ClockDiffs++;
                    LogClockDiff($"clock of 0x{m.Serial:X8}: ecs anim {c.AnimDue} idle {c.IdleDue} {c.Flags} | mobile anim {m.LastAnimationChangeTime} idle {m.IdleDue} {ClockFlagsOf(m)} (tick {Time.Ticks})");
                    WriteClock(m, e);
                }
            }
        }

        public static long TableChecks, TableDiffs, OrderChecks, OrderDiffs;

        /// <summary>
        /// E4 checker: the due mobiles are about to run in <paramref name="due"/>'s order; it must be a subsequence of a fresh
        /// enumeration of <paramref name="mobiles"/> (the order the legacy loop ran them in).
        /// </summary>
        public void CheckOrder(IEnumerable<Mobile> mobiles, List<Mobile> due)
        {
            var position = new Dictionary<Mobile, int>(due.Count, ReferenceEqualityComparer.Instance as IEqualityComparer<Mobile>);
            int i = 0;

            foreach (Mobile m in mobiles)
            {
                position[m] = i++;
            }

            int last = -1;

            foreach (Mobile m in due)
            {
                OrderChecks++;

                if (!position.TryGetValue(m, out int p) || p <= last)
                {
                    OrderDiffs++;
                    LogClockDiff($"due update order: 0x{m.Serial:X8} at enumeration position {(position.TryGetValue(m, out int q) ? q : -1)} runs after position {last}");
                }
                else
                {
                    last = p;
                }
            }
        }

        /// <summary>
        /// E4 checker, after the system's updates: every mobile it skipped is updated the legacy way and must not change.
        /// (Mobiles the system updated carry this frame's stamp; they ran the legacy Update, so they are equal by construction.)
        /// </summary>
        public void CheckSkipped(IEnumerable<Mobile> mobiles, int stamp)
        {
            foreach (Mobile m in mobiles)
            {
                if (m.IsDestroyed || m.UpdateStamp == stamp)
                {
                    continue;
                }

                SkippedChecks++;
                int before = m.UpdateStateHash();
                m.Update();

                if (m.UpdateStateHash() != before)
                {
                    SkippedDiffs++;
                    LogClockDiff($"skipped mobile 0x{m.Serial:X8} g=0x{m.Graphic:X4} changed in a legacy Update (anim {m.LastAnimationChangeTime}, idle {m.IdleDue}, steps {m.Steps.Count}, tick {Time.Ticks})");
                    PushClock(m);
                }
            }
        }

        private void LogClockDiff(string message)
        {
            if (_clockLogged++ < MAX_LOGGED_MISMATCHES)
            {
                Log.Warn($"[mobile-update-check] {message}");
            }
        }

        /// <summary>E4: rewrites every mobile's clock from the Mobile (the setting was just turned on: Update advanced the timers unpushed).</summary>
        public void ResyncClocks()
        {
            foreach (Mobile m in _world.Mobiles.Values)
            {
                PushClock(m);
            }
        }

        // Client.Game.UO.Version >= CV_7000 (the rules of IsFlying, IsPoisoned, IsGargoyle); false without a client (tests)
        private static bool Cv7000 => Client.Game?.UO != null && Client.Game.UO.Version >= Utility.ClientVersion.CV_7000;

        public void OnDestroyed(Entity entity)
        {
            Entity current = entity is Item ? _world.Items.Get(entity.Serial) : _world.Mobiles.Get(entity.Serial);

            // a newer live object already owns this serial
            if (current != null && !ReferenceEquals(current, entity) && !current.IsDestroyed)
            {
                return;
            }

            Mirror.Remove(entity.Serial);
        }

        // Death re-keys a mobile to serial | 0x80000000 and repoints its items.
        public void OnSerialChanged(uint oldSerial, Entity entity)
        {
            // the Mobile keeps IsFlipped across the re-key; so must the persistent mirror bit
            AnimStateFlags keep = 0;

            if (Mirror.TryGetEntity(oldSerial, out EcsEntity old) && old.HasComponent<AnimationState>())
            {
                keep = old.GetComponent<AnimationState>().Flags & AnimStateFlags.Mirror;
            }

            Mirror.Remove(oldSerial);
            Sync(entity);

            if (keep != 0 && Mirror.TryGetEntity(entity.Serial, out EcsEntity rekeyed) && rekeyed.HasComponent<AnimationState>())
            {
                rekeyed.GetComponent<AnimationState>().Flags |= keep;
            }

            for (LinkedObject i = entity.Items; i != null; i = i.Next)
            {
                Sync(i as Entity);
            }
        }

        public void OnWorldCleared()
        {
            if (Mirror.Count != 0)
            {
                Log.Warn($"[ECS] {Mirror.Count} entities left after World.Clear; resetting mirror");
                Mirror.Clear();
            }
        }

        private uint _cacheLogAt, _inputLogAt, _clockLogAt;

        public void Update()
        {
            FunnelCheck.LogSummary();

            if (MobileAnimationSystem.CacheCheck && Time.Ticks >= _cacheLogAt)
            {
                _cacheLogAt = Time.Ticks + 5000;
                Log.Info($"[mobile-cache-check] selections reused {MobileFrameCache.SelectHits}, run {MobileFrameCache.SelectMisses}; checked {MobileAnimationSystem.CheckSelects}, {MobileAnimationSystem.CheckSelectDiffs} different | instances replayed {MobileFrameCache.EmitHits}, emitted {MobileFrameCache.EmitMisses}; checked {MobileSpriteExtract.CheckReplays}, {MobileSpriteExtract.CheckReplayDiffs} different");
            }

            if (MobileUpdateStats && Time.Ticks >= _clockLogAt)
            {
                _clockLogAt = Time.Ticks + 5000;
                long f = Math.Max(1, ClockFrames);
                Log.Info($"[mobile-update-check] frames {ClockFrames}: due {ClockDue / (double)f:F1} of {ClockScanned / (double)f:F0} mobiles per frame; clocks checked {ClockChecks}, {ClockDiffs} different; skipped updates checked {SkippedChecks}, {SkippedDiffs} changed state; due-update order checked {OrderChecks}, {OrderDiffs} out of order, table checks {TableChecks}, table-vs-dictionary differences {TableDiffs}; clock pushes {ClockPushes}; per frame: scan {ClockCollectTicks * 1000.0 / Stopwatch.Frequency / f:F4} ms, due updates {ClockUpdateTicks * 1000.0 / Stopwatch.Frequency / f:F4} ms");
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

            int interval = CUOEnviroment.EcsCheckFrames;

            if (interval <= 0)
            {
                return;
            }

#if CUO_DEVTOOLS
            if (Driver == null && CUOEnviroment.EcsDriverScript != null)
            {
                Driver = EcsSessionDriver.Load(CUOEnviroment.EcsDriverScript);
                CUOEnviroment.EcsDriverScript = null;
            }

            Driver?.EnsureAttached(_world);
#endif

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

            if (r.IsMobile || r.Graphic != item.Graphic || r.Hue != item.Hue || r.X != item.X || r.Y != item.Y || r.Z != item.Z ||
                r.Facing != (byte)item.Direction || r.Flags != (byte)item.Flags || r.Amount != item.Amount || r.IsMulti != item.IsMulti ||
                r.OnGround != onGround || r.ParentKind != kind || (!onGround && r.Parent != item.Container) ||
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

        private void CompareMobile(Mobile mobile, ref int mismatches)
        {
            if (!Mirror.TryGet(mobile.Serial, out MirrorRecord r))
            {
                Report(ref mismatches, $"mobile 0x{mobile.Serial:X8} g=0x{mobile.Graphic:X4} missing in ECS");

                return;
            }

            bool isPlayer = ReferenceEquals(mobile, _world.Player);

            if (!r.IsMobile || r.IsPlayer != isPlayer || r.Graphic != mobile.Graphic || r.Hue != mobile.Hue || r.X != mobile.X ||
                r.Y != mobile.Y || r.Z != mobile.Z || r.Facing != (byte)mobile.Direction || r.Flags != (byte)mobile.Flags)
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
        private static void CompareInputs(Mobile mobile, EcsEntity e, ref int mismatches)
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
                pushed.HueBits != fresh.HueBits || pushed.LastStepTime != fresh.LastStepTime || pushed.WalkingDelay != fresh.WalkingDelay)
            {
                Report
                (
                    ref mismatches,
                    $"inputs of 0x{mobile.Serial:X8} g=0x{mobile.Graphic:X4}: pushed {pushed.Flags} body 0x{pushed.Body:X4} facing {pushed.Facing} noto {pushed.Notoriety} " +
                    $"hue {pushed.HueBits} step {pushed.LastStepTime} | fresh {fresh.Flags} body 0x{fresh.Body:X4} facing {fresh.Facing} noto {fresh.Notoriety} " +
                    $"hue {fresh.HueBits} step {fresh.LastStepTime}"
                );
            }
        }

        private void CompareAnimation(Mobile mobile, EcsEntity e, ref int mismatches)
        {

            ref readonly EquipmentSlots eq = ref e.GetComponent<EquipmentSlots>();
            ref readonly AnimationState s = ref e.GetComponent<AnimationState>();
            bool gargoyle = AnimationRules.IsGargoyleDrawBody(mobile.Graphic, Client.Game.UO.Version >= Utility.ClientVersion.CV_7000);

            Span<Layer> layers = stackalloc Layer[PaperdollOrder.N];
            Span<uint> expected = stackalloc uint[PaperdollOrder.N];
            int count = PaperdollOrder.BuildInWorld(mobile, mobile.IsFemale || gargoyle, 1, layers);
            int n = 0;

            if (mobile.IsHuman)
            {
                for (int i = 0; i < count; i++)
                {
                    Item item = mobile.FindItemByLayer(layers[i]);

                    if (item == null || mobile.IsDead && (layers[i] == Layer.Hair || layers[i] == Layer.Beard) ||
                        Mobile.IsCovered(mobile, layers[i]) || item.ItemData.AnimID == 0)
                    {
                        continue;
                    }

                    expected[n++] = item.Serial;
                }
            }

            Span<byte> order = stackalloc byte[EquipmentSlots.MAX];
            Span<byte> layerOf = stackalloc byte[EquipmentSlots.MAX];

            for (int i = 0; i < eq.Count; i++)
            {
                order[i] = (byte)i;
                layerOf[i] = eq.Slots[i].Layer;
            }

            int got = AnimationRules.ApplyDirectionCloak(order, eq.Count, layerOf, 1, eq.CloakAnchor);
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

        private static void Report(ref int mismatches, string message)
        {
            if (mismatches++ < MAX_LOGGED_MISMATCHES)
            {
                Log.Warn($"[ECS] mismatch: {message}");
            }
        }
    }
}
