// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    /// <summary>
    /// T1 (ecs-transition-plan.md, "T1: the mutation funnel"): the one writer of world-object state. Each operation matches a
    /// packet's meaning, not a field. Its shape (round 2, after the Codex audit of slice 1):
    /// <list type="number">
    /// <item><b>Admit, resolve and check.</b> The operation checks that no operation or fold is open, finds the object itself
    /// (or checks the one it was given belongs to its World and is the live instance), all before any side effect.</item>
    /// <item><b>Compute.</b> It computes the authoritative values (an <see cref="ItemState"/>) from the packet and the
    /// previous state, with the pure helpers (<see cref="Entity.FixedHue"/>, <see cref="Item.CorpseState"/>). Every read of
    /// the previous state happens here, before the first write or unlink; results such as the new Container are computed,
    /// never read back after a step that changes them.</item>
    /// <item><b>Write.</b> It writes the ECS (<see cref="WorldEcs.CommitItem"/>) and the GameObject cache from those values,
    /// never one read back from the other. The hook Syncs of the operation's own instance are folded (skipped) meanwhile.</item>
    /// <item><b>Derived state.</b> Only then the helpers that read world state run (<see cref="Item.RefreshGraphicState"/>:
    /// drawability, the multi load). They read values that the ECS and the cache both already hold, so the order stays right
    /// when T4 moves the getters to the ECS.</item>
    /// <item><b>Placement.</b> Tile placement is a separate operation when the handler has callbacks between the writes and
    /// the placement (<see cref="PlaceWorldItem"/>: 0x1A's new-corpse SingleClick sends a packet, and plugin send hooks run
    /// synchronously, so they must see the item written but not yet on its tile, as before T1). The upsert issues a
    /// <see cref="Placement"/> token that the placement consumes.</item>
    /// </list>
    /// <para>
    /// Operations do not nest, and only operations open a scope (one documented exception: the player's own walk from a
    /// position or direction callback joins the open unfolded operation on that player, see <see cref="AdmittedPlayerWalk"/>).
    /// A violation (nesting, a placement that is not outstanding,
    /// a removal of an item this World does not hold, an item upsert of a live mobile) is a bug in the caller: it throws in
    /// CUO_DEVTOOLS builds; in a release build it logs an error once per kind, so a funnel bug never disconnects a player
    /// (coordinator, T1 round 2). A nested operation is then rejected outright (nothing changes; T1 round 4: production code
    /// cannot re-enter the writer, see <see cref="Admitted"/>); the other violations happen with nothing open, and the packet
    /// takes the pre-T1 path (the old writes, the hook syncs). While one runs,
    /// writes to its own object (and, for a removal, to the removed subtree) are funnel writes for <see cref="FunnelCheck"/>;
    /// writes to any other object are still direct writes. Converted so far: world items (0x1A, 0xF3 and the item branch of
    /// 0x1D) and items into containers (0x25, 0x3C's items, 0x27's deny path: <see cref="PlaceInContainer"/>), the bulk clear
    /// of 0x3C and 0x24 (<see cref="ClearContainer"/>) 0x27's other deny branches (<see cref="ReturnHeldItem"/>) and equipment (0x2E: <see cref="Equip"/>; 0x78/0xD3's
    /// equipment list: <see cref="ClearWornItems"/>, <see cref="WearItem"/>) and mobiles (funnel part 4: UpdateGameObject's
    /// mobile branch, <see cref="UpsertMobile"/> and <see cref="PlaceMobile"/>; the notoriety of 0x77/0x78,
    /// <see cref="SetNotoriety"/>; the player's body of 0x20/0x77/0x78, <see cref="SetMobileBody"/>; 0x20's move,
    /// <see cref="MoveMobile"/>; 0x1D for a mobile, <see cref="RemoveMobile"/>), EnterWorld (funnel part 5: 0x1B,
    /// <see cref="SetMobileGraphic"/>, <see cref="MoveMobile"/>, <see cref="SetMobileDirection"/>) and the walker (0x21 and
    /// 0x20's deny, <see cref="StopMobile"/> and <see cref="MoveMobile"/>; 0x22's notoriety; <c>Mobile.ProcessSteps</c>' step
    /// completion, <see cref="CompleteStep"/>) and the range sweep's removals (<see cref="SweepMobile"/>, <see cref="SweepItem"/>), status and hits (funnel part 6: 0xA1, 0xA2, 0xA3, 0x2D, 0x11, 0x16/0x17) and the server's animations (0x6E, 0xE2, 0xAF, 0xBF.19, 0xBF.2B: <see cref="AnimateMobile"/>), the boat (<see cref="CompleteBoatStep"/>, <see cref="MoveBoatPassenger"/>, <see cref="SetBoatStepTime"/>, <see cref="ResetBoatOffset"/>) and the spellbook (0xBF.1B, <see cref="FillSpellbook"/>). Every other writer still relies on <see cref="WorldEcs.Sync"/> and <see cref="WorldEcs.PushInputs"/>, which
    /// stay as the fallback until the last packet group is converted.
    /// </para>
    /// <para>
    /// Detached objects (the character-creation mobiles, the house placement preview, the 0xBF spellbook entries) never pass
    /// here: the funnel creates no entity for an object its World does not hold. All of this runs on the one world-mutation
    /// thread (the game loop); nothing here is synchronised.
    /// </para>
    /// </summary>
    internal sealed class WorldWriter
    {
        /// <summary>The kinds of caller bug the operations reject (one error log per kind in a release build).</summary>
        internal enum Violation
        {
            /// <summary>An operation started while another one (or a fold) is open.</summary>
            Nesting,
            /// <summary><see cref="PlaceWorldItem"/> with a placement that is not outstanding.</summary>
            Placement,
            /// <summary>A removal of an item this World does not hold for its serial (foreign or stale).</summary>
            NotHeld,
            /// <summary>An item upsert for a serial that is a live mobile.</summary>
            LiveMobile
        }

        /// <summary>
        /// 0x1A between <see cref="UpsertWorldItem"/> and <see cref="PlaceWorldItem"/>: the item written but not yet placed, with
        /// the id of the upsert that wrote it. Each upsert issues its own, so a callback that upserts and places other items
        /// between the two halves leaves this one valid. A placement is consumed by its PlaceWorldItem. It is cancelled when a
        /// later upsert of the same item supersedes it, when the item is removed, or, abandoned (a callback threw, so the
        /// handler never placed), when <see cref="MaxOutstanding"/> newer ones were issued. Using a consumed or cancelled one is
        /// a violation. Id 0 is never issued (a default token is not outstanding).
        /// </summary>
        internal readonly struct Placement
        {
            /// <summary>The placed object: an item (<see cref="PlaceWorldItem"/>) or, since funnel part 4, a mobile.</summary>
            internal readonly Entity Subject;
            internal readonly int Id;

            internal Placement(Entity subject, int id)
            {
                Subject = subject;
                Id = id;
            }

            /// <summary>The item of an item placement (null for a mobile's).</summary>
            internal Item Item => Subject as Item;
        }

        /// <summary>A handler places within the same packet; more outstanding placements than this means the oldest were abandoned.</summary>
        internal const int MaxOutstanding = 8;

#if CUO_DEVTOOLS
        private const bool DefaultThrowOnViolation = true;
#else
        private const bool DefaultThrowOnViolation = false;
#endif

        private readonly World _world;

        // the open scope: its id (a copied or late Dispose of an older scope finds another id and does nothing), its subject
        // and, for a removal with the check on, the subtree whose writes it owns
        private int _scopeId;
        private bool _open;
        private Entity _subject;
        private HashSet<Entity> _subtree;

        // the outstanding placements, oldest first (normally none or one)
        private readonly List<Placement> _placements = new List<Placement>(MaxOutstanding);
        private int _placementId;

        private int _loggedViolations;

        // the boats whose steps a multi reload cleared inside an operation (Item.LoadMulti during an upsert or placement):
        // their resets are operations of their own, so they run when that scope closes (see ClearBoatSteps)
        private readonly List<uint> _deferredBoatClears = new List<uint>(1);
        private bool _runningDeferred;

        public WorldWriter(World world)
        {
            _world = world;
        }

        /// <summary>
        /// True: a violation throws (the CUO_DEVTOOLS default). False: it logs an error once per kind; a nested operation is
        /// rejected (changes nothing), any other violation takes the pre-T1 path (the release default; tests set it to check
        /// that path in either build).
        /// </summary>
        internal bool ThrowOnViolation = DefaultThrowOnViolation;

        /// <summary>Tests: the violations seen so far (thrown or logged).</summary>
        internal int Violations { get; private set; }

        /// <summary>Tests: the placements issued and not yet consumed or cancelled.</summary>
        internal int OutstandingPlacements => _placements.Count;

        /// <summary>True while a funnel operation runs.</summary>
        public bool InScope => _open;

        /// <summary>
        /// True when a write to <paramref name="e"/> now is a funnel write: an operation runs and <paramref name="e"/> is its
        /// subject (or in the subtree a removal owns).
        /// </summary>
        public bool Owns(Entity e) =>
            _open && (_clearingWorld || ReferenceEquals(e, _subject) || (_subtree != null && _subtree.Contains(e)));

        // ClearWorld's scope owns every object of this World (the logout's removals)
        private bool _clearingWorld;

        /// <summary>The scope of one operation. Only the operations of this class open one (no bare scopes).</summary>
        private ref struct Scope
        {
            private WorldWriter _writer;
            private readonly int _id;
            private readonly Entity _fold;

            internal Scope(WorldWriter writer, int id, Entity fold)
            {
                _writer = writer;
                _id = id;
                _fold = fold;
            }

            // idempotent: a second Dispose, or the Dispose of a copy after the original's, finds another (or no) open scope
            public void Dispose()
            {
                WorldWriter w = _writer;
                _writer = null;

                if (w == null || !w._open || w._scopeId != _id)
                {
                    return;
                }

                if (_fold != null)
                {
                    w._world.Ecs.EndFold(_fold);
                }

                w._open = false;
                w._subject = null;
                w._clearingWorld = false;
                w._subtree?.Clear();
                w.RunDeferredBoatClears();
            }
        }

        /// <summary>
        /// <c>Item.LoadMulti</c>'s <c>BoatMovingManager.ClearSteps</c> (a multi's (re)load ends its boat movement: the boat's
        /// and its passengers' offsets back to zero, the passengers and the queued steps dropped). With no operation open it
        /// runs now, as before T1. Inside one (<see cref="UpsertWorldItem"/>, <see cref="PlaceInContainer"/>,
        /// <see cref="ReturnHeldItem"/> and <see cref="WearItem"/> refresh a multi's load in their derived-state step) it runs
        /// as soon as that operation's scope closes, before the operation returns: the resets are <see cref="ResetBoatOffset"/>
        /// operations, which do not nest (T1 part 6, Codex audit). Nothing an operation does after its multi load reads an
        /// offset, a passenger or a boat step, so every effect the handler and the next frame see is in the pre-T1 order.
        /// </summary>
        internal void ClearBoatSteps(uint serial)
        {
            if (_open)
            {
                _deferredBoatClears.Add(serial);

                return;
            }

            _world.BoatMovingManager.ClearSteps(serial);
        }

        // after an operation's scope closed (its fold ended): the boat clears its multi load asked for, oldest first. The
        // resets open scopes of their own, whose closes must not start the next clear in the middle of this one
        private void RunDeferredBoatClears()
        {
            if (_runningDeferred || _deferredBoatClears.Count == 0)
            {
                return;
            }

            _runningDeferred = true;

            try
            {
                for (int i = 0; i < _deferredBoatClears.Count; i++)
                {
#if CUO_DEVTOOLS
                    // gate evidence (part 7, Codex audit P3 #2): a deferred clear that finds queued steps resets offsets
                    if (_world.BoatMovingManager.DescribeQueuedSteps(_deferredBoatClears[i]) is { } queued)
                    {
                        Log.Info($"[funnel] deferred boat clear 0x{_deferredBoatClears[i]:X8}: {queued}");
                    }
#endif
                    _world.BoatMovingManager.ClearSteps(_deferredBoatClears[i]);
                }
            }
            finally
            {
                _deferredBoatClears.Clear();
                _runningDeferred = false;
            }
        }

        // only after Admitted (nothing is open, so BeginFold cannot throw)
        private Scope Open(Entity subject, Entity fold)
        {
            if (fold != null)
            {
                _world.Ecs.BeginFold(fold);
            }

            _open = true;
            _subject = subject;

            return new Scope(this, ++_scopeId, fold);
        }

        // the admission check every operation makes before its first side effect: no operation and no fold open. Re-entry is
        // forbidden, not recovered from (T1 round 4, after the Codex round-4 audit): the operations are called only by the 0x1A/
        // 0xF3, 0x1D, 0x20, 0x25/0x3C, 0x27, 0x2E, 0x77 and 0x78/0xD3 handlers (and UpdateGameObject's other callers), which run only from ParsePackets in GameController's update; inside an
        // operation nothing parses packets (a plugin's injected packet, also from a send hook, is only queued by Append), so
        // a nested operation is a bug: it throws in CUO_DEVTOOLS builds; in a release build it logs once and is rejected (the
        // caller gets null / nothing), so no stale outer continuation can publish over a nested change.
        private bool Admitted(string operation, uint serial)
        {
            if (!_open && !_world.Ecs.IsFolding)
            {
                return true;
            }

            Reject(Violation.Nesting, $"WorldWriter: {operation} of 0x{serial:X8} started inside the operation on 0x{_subject?.Serial ?? 0:X8}; operations do not nest");

            return false;
        }

        // a caller bug: throws in a CUO_DEVTOOLS build; otherwise logs once per kind (the caller rejects a nesting, and takes
        // the pre-T1 path for the others, except where it passes rejected: true: WearItem's owner not held, which has none)
        private void Reject(Violation kind, string message, bool rejected = false)
        {
            Violations++;

            if (ThrowOnViolation)
            {
                throw kind == Violation.Nesting ? new InvalidOperationException(message) : new ArgumentException(message);
            }

            int bit = 1 << (int)kind;

            if ((_loggedViolations & bit) == 0)
            {
                _loggedViolations |= bit;
                Log.Error(message + (kind == Violation.Nesting || rejected ? " (logged once per kind; rejected)" : " (logged once per kind; the packet takes the pre-T1 path)"));
            }
        }

        /// <summary>
        /// 0x1A (UpdateItem), 0xF3 (UpdateItemSA) and the other users of <c>UpdateGameObject</c> for a serial that is not a
        /// mobile: an item on the ground, first half. Finds the item (<c>World.Get(serial)</c>) or creates it, takes it out of
        /// its container, writes the packet's values to the ECS and the GameObject, and refreshes its draw state and multi.
        /// It does not place the item on its tile: the handler runs its callbacks, then calls <see cref="PlaceWorldItem"/> with
        /// <paramref name="placement"/>. Returns null when no item could be had.
        /// </summary>
        /// <param name="created">True when the item is new (or replaced a destroyed one, or <c>World.Get</c> did not find a
        /// live item for this serial: a mobile-valued serial with type 3 is looked up among the mobiles, as before T1).</param>
        /// <param name="placement">The token <see cref="PlaceWorldItem"/> takes.</param>
        public Item UpsertWorldItem
        (
            uint serial,
            ushort graphic,
            byte graphicInc,
            ushort count,
            ushort x,
            ushort y,
            sbyte z,
            Direction direction,
            ushort hue,
            Flags flags,
            byte type,
            out bool created,
            out Placement placement
        )
        {
            placement = default;

            // 0. admission, before anything changes (no item is created and no outstanding placement touched on a rejection;
            // a nested upsert is rejected outright, also in a release build: see Admitted)
            if (!Admitted(nameof(UpsertWorldItem), serial))
            {
                created = false;

                return null;
            }

            // 1. resolve: what the handler's World.Get(serial) returned
            Entity existing = _world.Get(serial);
            Item item;

            if (existing == null || existing.IsDestroyed)
            {
                created = true;
                item = _world.GetOrCreateItem(serial, out _);

                if (item == null)
                {
                    return null;
                }
            }
            else if (existing is Item it)
            {
                created = false;
                item = it;
            }
            else
            {
                created = false;
                Reject(Violation.LiveMobile, $"WorldWriter.UpsertWorldItem: 0x{serial:X8} is a live mobile");

                return null;
            }

            // 2. compute: every read of the previous state is here, before the unlink and the writes. Only an item World.Get
            // found leaves its container; a "created" one that GetOrCreateItem found alive stays where it is, as before T1.
            uint oldContainer = item.Container;
            bool unlink = !created && SerialHelper.IsValid(oldContainer);

            if (graphic != 0x2006)
            {
                graphic += graphicInc;
            }

            bool multi = type == 2;
            ItemState v;
            v.Serial = serial;
            v.IsMulti = multi;
            v.Graphic = multi ? (ushort)(graphic & 0x3FFF) : graphic;
            v.X = x;
            v.Y = y;
            v.Z = z;
            v.Hue = Entity.FixedHue(hue);
            v.Amount = count == 0 ? (ushort)1 : count;
            v.Flags = flags;
            v.Container = unlink ? 0xFFFF_FFFF : oldContainer;
            v.Direction = direction;
            v.Layer = graphic == 0x2006 ? (Layer)direction : item.Layer;

            bool wantUpdateMulti = multi
                && (v.Graphic != item.Graphic || item.X != x || item.Y != y || item.Z != z || item.Hue != hue);

            // CheckGraphicChange's corpse branch (a corpse is a non-multi with the corpse graphic)
            bool corpse = !multi && v.Graphic == 0x2006;
            bool usedLayer = false;

            if (corpse)
            {
                Item.CorpseState(ref v.Direction, out usedLayer, out v.Layer);
            }

            // an earlier placement of this item is superseded: this upsert issues its own
            CancelPlacements(item);

            using (Open(item, item))
            {
                // the links only (the parent's gump and child list, the tile); the Container is written from v below
                if (unlink)
                {
                    _world.UnlinkFromContainer(item, oldContainer);
                }

                // 3. write: the ECS, then the cache, from v (the old path's order of the cache writes)
                _world.Ecs.CommitItem(item, in v);

                if (unlink)
                {
                    item.Container = v.Container;
                }

                if (multi)
                {
                    item.IsMulti = true;
                    item.WantUpdateMulti = wantUpdateMulti;
                    item.Graphic = v.Graphic;
                }
                else
                {
                    item.IsDamageable = type == 3;
                    item.IsMulti = false;
                    item.Graphic = v.Graphic;
                }

                item.X = v.X;
                item.Y = v.Y;
                item.Z = v.Z;
                item.LightID = (byte)direction;

                if (graphic == 0x2006 || corpse)
                {
                    item.Layer = v.Layer;
                }

                item.Hue = v.Hue;
                item.Amount = v.Amount;
                item.Flags = v.Flags;
                item.Direction = v.Direction;

                if (corpse)
                {
                    item.AnimIndex = 99;
                    item.UsedLayer = usedLayer;
                    // T5b-items (Codex P2): the commit pushed the timer before these writes
                    _world.Ecs.PushItemTimer(item);
                }

                // 4. derived state: drawability and the multi (reads Graphic, IsMulti, WantUpdateMulti, X, Y written above)
                item.RefreshGraphicState();
            }

            placement = Issue(item);

            return item;
        }

        /// <summary>
        /// 0x1A/0xF3, second half: places the item of <paramref name="placement"/> (what <see cref="UpsertWorldItem"/> issued)
        /// on its tile when it is on the ground (tile insertion order breaks equal-priority ties, so this is where the old
        /// handler placed it: after its callbacks). The ECS already holds the item's values (the placement changes none), so the
        /// placement's own Sync is folded. Consumes the placement.
        /// </summary>
        public void PlaceWorldItem(in Placement placement)
        {
            Item item = placement.Item;

            // a nested placement is rejected; its token stays outstanding (the handler that nested is a bug)
            if (!Admitted(nameof(PlaceWorldItem), item?.Serial ?? 0))
            {
                return;
            }

            // a mobile's placement is not an item placement (it stays outstanding for its PlaceMobile)
            if (item == null || !Consume(placement))
            {
                Reject(Violation.Placement, $"WorldWriter.PlaceWorldItem: 0x{item?.Serial ?? 0:X8} is not an outstanding item placement (used twice, superseded, abandoned, a mobile's or never issued)");

                if (item != null)
                {
                    PlacePreT1(item);
                }

                return;
            }

            if (item.IsDestroyed || !ReferenceEquals(_world.Items.Get(item.Serial), item) || !item.OnGround)
            {
                return;
            }

            using (Open(item, item))
            {
                item.SetInWorldTile(item.X, item.Y, item.Z);
            }
        }

        /// <summary>
        /// 0x1D (DeleteObject) for an item: removes a house's components, takes the item out of its parent's child list, then
        /// removes it and its contents from World (each child first, OPL, Destroy, which deletes the ECS entities through
        /// <see cref="WorldEcs.OnDestroyed"/>). The hooks are not folded here: the removal's own Sync of a corpse queues it in
        /// the active-items list as before, so the list order is unchanged. Cancels the item's outstanding placement.
        /// </summary>
        /// <param name="item">The item World holds for its serial; anything else (a foreign or stale instance) is a violation,
        /// rejected before any side effect.</param>
        /// <param name="parent">What <c>World.Get(item.Container)</c> returned before the removal (the handler's gumps use it).</param>
        public void RemoveWorldItem(Item item, out Entity parent)
        {
            parent = null;

            if (item == null)
            {
                Reject(Violation.NotHeld, "WorldWriter.RemoveWorldItem: no item");

                return;
            }

            if (!Admitted(nameof(RemoveWorldItem), item.Serial))
            {
                return;
            }

            // the instance this World holds (a destroyed one World still holds goes through, as DeleteObject's did before T1:
            // the house and parent cleanup run, World.RemoveItem then finds it destroyed and does nothing)
            if (item.World != _world || !ReferenceEquals(_world.Items.Get(item.Serial), item))
            {
                Reject(Violation.NotHeld, $"WorldWriter.RemoveWorldItem: 0x{item.Serial:X8} is not the item this World holds for its serial");

                // the pre-T1 handler removed by serial, from this World
                RemovePreT1(item.Serial, out parent);

                return;
            }

            parent = _world.Get(item.Container);
            uint serial = item.Serial;
            CancelSubtreePlacements(item);

            using (Open(item, null))
            {
#if CUO_DEVTOOLS
                if (FunnelCheck.Enabled)
                {
                    CollectSubtree(item);
                }
#endif

                if (item.IsMulti)
                {
                    _world.HouseManager.Remove(serial);
                }

                parent?.Remove(item);

                // by instance: if something replaced the item under its serial meanwhile, the replacement stays
                if (ReferenceEquals(_world.Items.Get(serial), item))
                {
                    _world.RemoveItem(serial, true);
                }
            }
        }

        /// <summary>
        /// 0x25 (UpdateContainedItem), each item of 0x3C (UpdateContainedItems, after its bulk clear, which runs outside this
        /// operation) and 0x27's deny path (<c>AddItemToContainer</c>): puts an item into a container. Finds the container
        /// (<c>World.Get(containerSerial)</c>; null: nothing happens, null is returned). A mobile with the item's serial is
        /// removed (as before T1), through the removal path (its subtree's placements are cancelled). An item whose Container differs is destroyed with its contents and recreated, except an
        /// equipped item (Layer set) going into a corpse, which is moved; then the packet's values are written, and the item is
        /// linked at the end of the container's child list (a re-push when it already was there).
        /// </summary>
        /// <param name="container">The container the item went into, for the handler's gumps.</param>
        public Item PlaceInContainer(uint serial, ushort graphic, ushort amount, ushort x, ushort y, ushort hue, uint containerSerial, out Entity container)
        {
            container = null;

            if (!Admitted(nameof(PlaceInContainer), serial))
            {
                return null;
            }

            // 1. resolve, in the old order: the container, the item, a mobile holding the serial
            container = _world.Get(containerSerial);

            if (container == null)
            {
                return null;
            }

            Item item = _world.Items.Get(serial);

            if (SerialHelper.IsMobile(serial))
            {
                // through the removal path (Codex audit of part 4, #2): the placements of the mobile and of everything it
                // wears or holds are cancelled first, and the removal owns its subtree. World.RemoveMobile did nothing for a
                // missing or destroyed mobile, so neither does this.
                Mobile asItem = _world.Mobiles.Get(serial);

                if (asItem != null && !asItem.IsDestroyed)
                {
                    RemoveMobileSubtree(asItem);
                }

                Log.Warn("AddItemToContainer function adds mobile as Item");
            }

            // destroy-vs-reuse: another container's item is destroyed (contents too) and recreated below, unless it is an
            // equipped item going into a corpse (the corpse's equipment)
            if (item != null && item.Container != containerSerial && (container.Graphic != 0x2006 || item.Layer == Layer.Invalid))
            {
                RemoveForReplacement(item);
            }

            item = _world.GetOrCreateItem(serial, out _);

            // 2. compute: every read of the previous state is here. CheckGraphicChange ran right after the Graphic write and
            // read the old Direction (the corpse branch) and the old IsMulti.
            uint oldContainer = item.Container;
            bool relink = oldContainer != containerSerial;
            bool multi = item.IsMulti;
            bool corpse = !multi && graphic == 0x2006;
            bool usedLayer = false;

            ItemState v;
            v.Serial = serial;
            v.IsMulti = multi;
            v.Graphic = graphic;
            v.X = x;
            v.Y = y;
            v.Z = 0;
            v.Hue = Entity.FixedHue(hue);
            v.Amount = amount;
            v.Flags = item.Flags;
            v.Container = containerSerial;
            v.Direction = item.Direction;
            v.Layer = item.Layer;

            if (corpse)
            {
                Item.CorpseState(ref v.Direction, out usedLayer, out v.Layer);
            }

            CancelPlacements(item);

            using (Open(item, item))
            {
                // 3. write: the ECS (attached: the push at the end, T1 part 9), then the cache from v in the old order
                _world.Ecs.CommitItem(item, in v, attach: true);

                item.Graphic = v.Graphic;

                if (corpse)
                {
                    item.AnimIndex = 99;
                    item.UsedLayer = usedLayer;
                    // T5b-items (Codex P2): the commit pushed the timer before these writes
                    _world.Ecs.PushItemTimer(item);
                    item.Direction = v.Direction;
                    item.Layer = v.Layer;
                }

                // 4. derived state where the old CheckGraphicChange ran it: before the amount, hue and position writes (a
                // multi's load sees the old position, as before T1; for every other item it reads Graphic only)
                item.RefreshGraphicState();

                item.Amount = v.Amount;
                item.Hue = v.Hue;
                item.X = v.X;
                item.Y = v.Y;
                item.Z = v.Z;

                if (relink)
                {
                    // the links only (the old parent's gump and child list, the tile); the Container is written from v
                    _world.UnlinkFromContainer(item, oldContainer);
                    item.Container = v.Container;
                }

                // at the end of the container's child list, also when it already was there (the re-push; the commit above
                // took its child-order stamp)
                container.PushToBack(item);
            }

            // art_container_prefetch: an open container's new item starts its art decode on the workers (no world state)
            global::ClassicUO.Game.Map.ContentsArt.RequestContained(item, container);

            return item;
        }

        /// <summary>
        /// 0x3C's bulk clear before its first item and 0x24 (OpenContainer) of a container that is not a corpse
        /// (<c>ClearContainerAndRemoveItems</c> before T1): removes every item in the container with its contents, or, with
        /// <paramref name="keepEquipped"/> (0x3C into a corpse), every item without a Layer, keeping the corpse's equipment in
        /// its child list in order. A removal: it owns the removed subtrees and writes no value of its own (each removal's Sync
        /// and Destroy run as in <see cref="RemoveWorldItem"/>, not folded: a corpse's removal queues it in the active-items
        /// list as before). The container itself is not written (its child list only). Cancels the outstanding placements of
        /// every removed item and its contents at any depth (the kept equipment and its contents keep theirs).
        /// </summary>
        /// <param name="itemOnly">How the container is found, as each handler found it before T1: 0x24 by
        /// <c>World.Items.Get</c> (true: the item, also when a mobile has the same serial), 0x3C by <c>World.Get</c> (false:
        /// a mobile first).</param>
        public void ClearContainer(uint containerSerial, bool keepEquipped, bool itemOnly)
        {
            if (!Admitted(nameof(ClearContainer), containerSerial))
            {
                return;
            }

            Entity container = itemOnly ? _world.Items.Get(containerSerial) : _world.Get(containerSerial);

            // T9 child-list slice A: the children are the ECS's (WorldState.Children: the spell entries, then the contents, in
            // child order)
            if (container == null || !_world.State.HasChildren(container))
            {
                return;
            }

            // 1. resolve: the removed children, in child order, before anything changes
            foreach (Item it in _world.State.Children(container))
            {
                if (!(keepEquipped && it.Layer != 0))
                {
                    CancelSubtreePlacements(it);
                }
            }

            using (Open(container, null))
            {
#if CUO_DEVTOOLS
                if (FunnelCheck.Enabled)
                {
                    foreach (Item it in _world.State.Children(container))
                    {
                        if (!(keepEquipped && it.Layer != 0))
                        {
                            CollectSubtree(it);
                        }
                    }
                }
#endif

                // T2: the clear also drops a spellbook's detached spell entries (not world objects: RemoveItem skips them,
                // the head below drops them); committed first
                if (!keepEquipped && container is Item book && _world.Ecs.Mirror.Spellbook(book.Serial) != 0)
                {
                    _world.Ecs.CommitSpellbook(book, 0);
                }

                // the old loop: each removal by serial in child order (World.RemoveItem: out of the child list, the
                // contents, OPL, Destroy). The spell entries are no world items (RemoveItem skipped them): the copy of the
                // ECS contents leaves them out
                using (ContentsSnapshot children = _world.State.SnapshotContents(container.Serial))
                {
                    foreach (uint serial in children.Serials)
                    {
                        Item it = _world.Items.Get(serial);

                        if (it != null && !(keepEquipped && it.Layer != 0))
                        {
                            _world.RemoveItem(serial, true);
                        }
                    }
                }

                // the list's head (list upkeep, goes in T9 slice B): the kept equipment's links are untouched and the head
                // is the first kept one, the first child left with a Layer
                LinkedObject newFirst = null;

                for (LinkedObject i = keepEquipped ? container.Items : null; i != null; i = i.Next)
                {
                    if (((Item)i).Layer != 0)
                    {
                        newFirst = i;

                        break;
                    }
                }

                container.Items = newFirst;
            }
        }

        /// <summary>What <see cref="ReturnHeldItem"/> did with a denied item.</summary>
        internal enum HeldItemReturn
        {
            /// <summary>No operation ran (a nesting was rejected).</summary>
            None,
            /// <summary>Re-equipped on the mobile it came from (the handler refreshes its paperdoll).</summary>
            Paperdoll,
            /// <summary>Its container is an item: the old code removed it ("something wrong"; the server resends it).</summary>
            Removed,
            /// <summary>Its container is gone (or it lay on the ground): put back on its tile.</summary>
            Terrain
        }

        /// <summary>
        /// 0x27 (DenyMoveItem) for a held item that does not go back into a container through <see cref="PlaceInContainer"/>
        /// (it has a Layer, or no valid container): restores the held item's values (graphic, hue as given, amount, flags,
        /// layer, position; a corpse's state from its direction as <c>CheckGraphicChange</c> computed it), then, by its
        /// container (<c>World.Get(containerSerial)</c>): a mobile re-equips it at the end of its child list; another item
        /// removes it with its contents; none puts it on its tile. The item is found or created (<c>GetOrCreateItem</c>).
        /// <para>
        /// The old path published the item out of its container before re-adding it (a Sync after
        /// <c>RemoveItemFromContainer</c>, then one after the re-equip), which moved it to the end of the mirror's equipment
        /// index. T3a: the order of a wearer's equipment is its child-order stamps (WornLayers, EquipmentSlots and -ecscheck
        /// read the stamps, never the index's order), so the paperdoll branch commits its final state once, attached (the
        /// next stamp: the back of the wearer's list); the other branches commit the detached state. Its hook Syncs are folded.
        /// </para>
        /// </summary>
        public Item ReturnHeldItem
        (
            uint serial,
            ushort graphic,
            ushort hue,
            ushort amount,
            Flags flags,
            Layer layer,
            ushort x,
            ushort y,
            sbyte z,
            uint containerSerial,
            out Entity container,
            out HeldItemReturn result
        )
        {
            container = null;
            result = HeldItemReturn.None;

            if (!Admitted(nameof(ReturnHeldItem), serial))
            {
                return null;
            }

            // 1. resolve, in the old order: the item (found or created), then its container
            Item item = _world.GetOrCreateItem(serial, out _);
            container = _world.Get(containerSerial);

            result = container == null ? HeldItemReturn.Terrain
                : SerialHelper.IsMobile(container.Serial) ? HeldItemReturn.Paperdoll
                : HeldItemReturn.Removed;

            // 2. compute: every read of the previous state is here (CheckGraphicChange ran after the writes below and read
            // the new Graphic with the old Direction and IsMulti)
            uint oldContainer = item.Container;
            bool multi = item.IsMulti;
            bool corpse = !multi && graphic == 0x2006;
            bool usedLayer = false;

            ItemState v;
            v.Serial = serial;
            v.IsMulti = multi;
            v.Graphic = graphic;
            v.X = x;
            v.Y = y;
            v.Z = z;
            v.Hue = hue;
            v.Amount = amount;
            v.Flags = flags;
            v.Container = 0xFFFF_FFFF; // detached: RemoveItemFromContainer (or the removal) ran in every branch
            v.Direction = item.Direction;
            v.Layer = layer;

            if (corpse)
            {
                Item.CorpseState(ref v.Direction, out usedLayer, out v.Layer);
            }

            // a removal cancels its contents' placements too; a returned item keeps its contents (only its own is superseded)
            if (result == HeldItemReturn.Removed)
            {
                CancelSubtreePlacements(item);
            }
            else
            {
                CancelPlacements(item);
            }

            using (Open(item, item))
            {
#if CUO_DEVTOOLS
                if (result == HeldItemReturn.Removed && FunnelCheck.Enabled)
                {
                    CollectSubtree(item);
                }
#endif

                // 3. write: the ECS (paperdoll: worn by the container at the back of its list, the equipment-order stamp
                // taken in this one commit; otherwise the detached state), then the cache from v in the old order
                if (result == HeldItemReturn.Paperdoll)
                {
                    v.Container = container.Serial;
                }

                _world.Ecs.CommitItem(item, in v, attach: result == HeldItemReturn.Paperdoll);

                item.Graphic = v.Graphic;
                item.Hue = v.Hue;
                item.Amount = v.Amount;
                item.Flags = v.Flags;
                item.Layer = layer;
                item.X = v.X;
                item.Y = v.Y;
                item.Z = v.Z;

                if (corpse)
                {
                    item.AnimIndex = 99;
                    item.UsedLayer = usedLayer;
                    // T5b-items (Codex P2): the commit pushed the timer before these writes
                    _world.Ecs.PushItemTimer(item);
                    item.Direction = v.Direction;
                    item.Layer = v.Layer;
                }

                // 4. derived state where CheckGraphicChange ran it
                item.RefreshGraphicState();

                // 5. links, by the container
                switch (result)
                {
                    case HeldItemReturn.Paperdoll:
                        _world.UnlinkFromContainer(item, oldContainer);
                        container.PushToBack(item);
                        item.Container = v.Container;

                        break;

                    case HeldItemReturn.Removed:
                        // World.RemoveItem from the computed state, never reading item.Container back (T4's ECS-backed getter
                        // returns the committed detached value there): the captured parent's unlink, the Container from v, then
                        // the rest of the removal (the contents, OPL, Destroy). The item is the live one World holds for the
                        // serial (GetOrCreateItem above), as World.RemoveItem(serial) required
                        _world.UnlinkFromContainer(item, oldContainer);

                        if (oldContainer != v.Container)
                        {
                            item.Container = v.Container;
                        }

                        _world.RemoveDetachedItem(item, true);

                        break;

                    default:
                        _world.UnlinkFromContainer(item, oldContainer);

                        if (oldContainer != v.Container)
                        {
                            item.Container = v.Container;
                        }

                        item.SetInWorldTile(v.X, v.Y, v.Z);

                        break;
                }
            }

            return item;
        }

        /// <summary>
        /// 0x2E (EquipItem): puts an item on a wearer. The item is found or created among the items
        /// (<c>World.GetOrCreateItem</c>); the wearer is <c>World.Get(containerSerial)</c> (a mobile first), which may be null
        /// (the item then gets the Container and Layer but no parent link, as before T1). An item that already had a graphic
        /// and is not a backpack leaves its old container first (the old path published that detached state, so this commits
        /// it too); then the packet's graphic, layer, container, hue (FixHue) and amount 1 are written, and the item goes to
        /// the end of the wearer's child list. No <c>CheckGraphicChange</c> ran here before T1, so none runs now.
        /// </summary>
        /// <param name="wearer">What the handler's <c>World.Get(item.Container)</c> returned.</param>
        /// <param name="shownContainer">The container whose gumps the handler refreshes before the writes: the old Container when
        /// the item did not leave it, otherwise 0xFFFFFFFF (none).</param>
        public Item Equip(uint serial, ushort graphic, Layer layer, uint containerSerial, ushort hue, out Entity wearer, out uint shownContainer)
        {
            wearer = null;
            shownContainer = 0xFFFF_FFFF;

            if (!Admitted(nameof(Equip), serial))
            {
                return null;
            }

            // 1. resolve, as the old handler: the item among the items (found or created), the wearer by World.Get (the writes
            // below change neither dictionary, so resolving it first finds what the old lookup after the writes found)
            Item item = _world.GetOrCreateItem(serial, out _);
            wearer = _world.Get(containerSerial);

            // 2. compute: every read of the previous state is here
            uint oldContainer = item.Container;
            bool unlink = item.Graphic != 0 && item.Layer != Layer.Backpack;
            shownContainer = unlink ? 0xFFFF_FFFF : oldContainer;

            ItemState detached;
            detached.Serial = serial;
            detached.IsMulti = item.IsMulti;
            detached.Graphic = item.Graphic;
            detached.X = item.X;
            detached.Y = item.Y;
            detached.Z = item.Z;
            detached.Hue = item.Hue;
            detached.Amount = item.Amount;
            detached.Flags = item.Flags;
            detached.Container = 0xFFFF_FFFF;
            detached.Direction = item.Direction;
            detached.Layer = item.Layer;

            ItemState v = detached;
            v.Graphic = graphic;
            v.Layer = layer;
            v.Container = containerSerial;
            v.Hue = Entity.FixedHue(hue);
            v.Amount = 1;

            CancelPlacements(item);

            using (Open(item, item))
            {
                if (unlink)
                {
                    // the old RemoveItemFromContainer: the links, then its Sync of the detached state
                    _world.UnlinkFromContainer(item, oldContainer);
                    _world.Ecs.CommitItem(item, in detached);
                }
                else if (oldContainer != containerSerial)
                {
                    // Behaviour fix (Codex audit P2 #2; legacy ClassicUO and the base have the same quirk): a backpack (or an
                    // item without a graphic) keeps its link, and the new wearer's PushToBack below unlinks it only through its
                    // neighbours, so as its old parent's first child it stayed that parent's list head (the backpack and the
                    // old parent's other children lost). It leaves its old parent's list here; the gumps of the old container
                    // are refreshed by the handler (shownContainer), and the mirror already moves its attachment.
                    _world.Get(oldContainer)?.Remove(item);
                }

                // 3. write: the ECS (attached when there is a wearer to link it to; without one an orphan, T1 part 9), then
                // the cache from v in the old order, then the link
                _world.Ecs.CommitItem(item, in v, attach: wearer != null);

                item.Graphic = v.Graphic;
                item.Layer = v.Layer;
                item.Container = v.Container;
                item.Hue = v.Hue;
                item.Amount = v.Amount;

                wearer?.PushToBack(item);
            }

            return item;
        }

        /// <summary>
        /// 0x78 / 0xD3 (UpdateObject), before its equipment list: removes every child of the object
        /// (<c>World.Get(ownerSerial)</c>) that is not an opened container and not the backpack, with its contents, each by
        /// serial as before T1. A removal: it owns the removed subtrees, writes no value of its own (each removal's Sync and
        /// Destroy run unfolded, as in <see cref="ClearContainer"/>), and cancels the removed items' placements at any depth.
        /// </summary>
        public void ClearWornItems(uint ownerSerial)
        {
            if (!Admitted(nameof(ClearWornItems), ownerSerial))
            {
                return;
            }

            Entity owner = _world.Get(ownerSerial);

            // T9 child-list slice A: the children are the ECS's, in child order
            if (owner == null || !_world.State.HasChildren(owner))
            {
                return;
            }

            // 1. resolve: the removed children, before anything changes
            foreach (Item it in _world.State.Children(owner))
            {
                if (!it.Opened && it.Layer != Layer.Backpack)
                {
                    CancelSubtreePlacements(it);
                }
            }

            using (Open(owner, null))
            {
#if CUO_DEVTOOLS
                if (FunnelCheck.Enabled)
                {
                    foreach (Item it in _world.State.Children(owner))
                    {
                        if (!it.Opened && it.Layer != Layer.Backpack)
                        {
                            CollectSubtree(it);
                        }
                    }
                }
#endif

                // the old loop, over a copy of the contents (each removal changes them)
                using (ContentsSnapshot children = _world.State.SnapshotContents(owner.Serial))
                {
                    foreach (uint serial in children.Serials)
                    {
                        Item it = _world.Items.Get(serial);

                        if (it != null && !it.Opened && it.Layer != Layer.Backpack)
                        {
                            _world.RemoveItem(serial, true);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 0x78 / 0xD3 (UpdateObject), one entry of its equipment list: the item (found or created among the items) gets the
        /// entry's graphic, hue (FixHue) and amount 1, leaves its old container (the old path published that state: the new
        /// graphic, hue and amount with the old layer), then gets the owner as its Container and the entry's layer;
        /// <c>CheckGraphicChange</c> ran next (a corpse graphic: the corpse's state from the old direction), then the item goes to the end of the owner's child list.
        /// </summary>
        /// <param name="owner">The handler's <c>obj</c>: what <c>World.Get(serial)</c> returned once, before the list. The
        /// handler passes that instance to every entry, as the old loop used it, and the operation does not look the owner up
        /// again: an entry can create an item under the owner's serial (a death-rekeyed mobile's 0x8000_0000 serial), and
        /// <c>World.Get</c> would then return that item for the following entries. Null: nothing happens and null is returned.
        /// An instance this World does not hold in its table (the mobiles for a mobile, the items for an item) is a caller bug:
        /// a violation, rejected (null) before any side effect.</param>
        public Item WearItem(Entity owner, uint serial, ushort graphic, Layer layer, ushort hue)
        {
            if (!Admitted(nameof(WearItem), serial))
            {
                return null;
            }

            // 1. resolve: the owner as captured by the handler, validated against its own table; the item among the items
            // (found or created)
            if (owner == null)
            {
                return null;
            }

            if (!Holds(owner))
            {
                Reject(Violation.NotHeld, $"WorldWriter.WearItem: owner 0x{owner.Serial:X8} is not the instance this World holds for its serial", rejected: true);

                return null;
            }

            uint ownerSerial = owner.Serial;
            Item item = _world.GetOrCreateItem(serial, out _);

            // 2. compute: every read of the previous state is here
            uint oldContainer = item.Container;
            bool multi = item.IsMulti;

            ItemState detached;
            detached.Serial = serial;
            detached.IsMulti = multi;
            detached.Graphic = graphic;
            detached.X = item.X;
            detached.Y = item.Y;
            detached.Z = item.Z;
            detached.Hue = Entity.FixedHue(hue);
            detached.Amount = 1;
            detached.Flags = item.Flags;
            detached.Container = 0xFFFF_FFFF;
            detached.Direction = item.Direction;
            detached.Layer = item.Layer;

            ItemState v = detached;
            v.Container = ownerSerial;
            v.Layer = layer;

            // CheckGraphicChange's corpse branch. Its running-bit Direction write also synced (with the entry's layer, before
            // the corpse layer): the item was already re-equipped at the end of the owner's equipment by then and the final
            // state overwrites every value of that one, so the operation commits the final state only
            bool corpse = !multi && graphic == 0x2006;
            bool usedLayer = false;

            if (corpse)
            {
                Item.CorpseState(ref v.Direction, out usedLayer, out Layer corpseLayer);
                v.Layer = corpseLayer;
            }

            CancelPlacements(item);

            using (Open(item, item))
            {
                // 3. write: the old order (the graphic before the unlink: a placed item's tile notes the change)
                _world.Ecs.CommitItem(item, in detached);

                item.Graphic = v.Graphic;
                item.Hue = v.Hue;
                item.Amount = v.Amount;

                _world.UnlinkFromContainer(item, oldContainer);
                _world.Ecs.CommitItem(item, in v, attach: true);

                item.Container = v.Container;
                item.Layer = layer;

                if (corpse)
                {
                    item.AnimIndex = 99;
                    item.UsedLayer = usedLayer;
                    // T5b-items (Codex P2): the commit pushed the timer before these writes
                    _world.Ecs.PushItemTimer(item);

                    if (usedLayer)
                    {
                        item.Direction = v.Direction;
                    }

                    item.Layer = v.Layer;
                }

                // 4. derived state where CheckGraphicChange ran it, then the link
                item.RefreshGraphicState();
                owner.PushToBack(item);
            }

            return item;
        }

        /// <summary>One entry of a 0x78/0xD3 equipment list, as the packet gives it (the hue before FixHue).</summary>
        internal readonly struct WornEntry
        {
            internal readonly uint Serial;
            internal readonly ushort Graphic;
            internal readonly Layer Layer;
            internal readonly ushort Hue;

            internal WornEntry(uint serial, ushort graphic, Layer layer, ushort hue)
            {
                Serial = serial;
                Graphic = graphic;
                Layer = layer;
                Hue = hue;
            }
        }

        /// <summary>
        /// t3a-fold: 0x78 / 0xD3 (UpdateObject)'s whole equipment part as one operation: the handler resolves the packet's
        /// list first (<paramref name="entries"/>, read before any change), then this runs <see cref="ClearWornItems"/> on the
        /// owner's serial and one <see cref="WearItem"/> per entry, in the packet's order, exactly as the handler did before,
        /// with one difference: the wearer's WornLayers are kept in the mirror's batch for the length of the packet (the
        /// mirror's <c>BeginWornBatch</c>/<c>EndWornBatch</c>), so the departures and arrivals touch no component of the wearer,
        /// and the batch writes the result back once, with one version bump, only when a serial or a per-layer count changed.
        /// <para>
        /// Kept as before, on purpose (owner, t3a-fold: real destruction semantics): every item the clear takes is removed and
        /// destroyed with its contents, and every listed item is found or created after the clear, also an item the list
        /// re-sends unchanged. The old path's result for such an item is a new Item object (the old one destroyed: IsDestroyed,
        /// its overhead texts cleared, its OPL entry dropped, its fields back to defaults, its contents destroyed, the
        /// destroyed-items callback, a new mirror record with a new child-order stamp and an epoch bump), and each of those is
        /// observable (held references, tooltips, texts, the mirror's epoch that readers' caches key on), so the unchanged
        /// item takes the same removal and creation. Only WornLayers is diffed (the batch's end state against its start).
        /// </para>
        /// </summary>
        /// <param name="owner">The handler's <c>obj</c> (<c>World.Get(serial)</c>, captured once before the list); null: nothing.</param>
        public void ReplaceWornItems(Entity owner, ReadOnlySpan<WornEntry> entries)
        {
            if (owner == null || !Admitted(nameof(ReplaceWornItems), owner.Serial))
            {
                return;
            }

            ClassicUO.Ecs.WorldMirror mirror = _world.Ecs.Mirror;
            mirror.BeginWornBatch(owner.Serial);

            try
            {
                // the old sequence, each its own operation (nothing is open between them)
                ClearWornItems(owner.Serial);

                for (int i = 0; i < entries.Length; i++)
                {
                    ref readonly WornEntry e = ref entries[i];
                    WearItem(owner, e.Serial, e.Graphic, e.Layer, e.Hue);
                }
            }
            finally
            {
                mirror.EndWornBatch();
            }
        }

        /// <summary>
        /// The mobile branch of <c>UpdateGameObject</c> (0x77 and 0x78/0xD3 for another mobile, and the item packets for a
        /// mobile's serial), first half. <paramref name="obj"/> is the handler's <c>World.Get(serial)</c>, captured once: null or
        /// destroyed creates the mobile (<c>World.GetOrCreateMobile</c>, whose creation Sync runs as before); a live mobile is
        /// used as it is, validated against the mobiles table. Computes, before any write: the creation's writes (graphic plus
        /// increment, <c>CheckGraphicChange</c>'s sex and race, direction, position), for a mobile other than the player the
        /// step (<see cref="Mobile.PlanStep"/>) or the reset to the packet's position (the mobile was unplaced, or its walk
        /// queue is full), then the masked graphic, the hue and the flags. Writes the ECS record, then the cache in the old
        /// order, then the derived inputs. It does not place the mobile: the handler runs its callbacks (the new-mobile
        /// SingleClick), then calls <see cref="PlaceMobile"/> with <paramref name="placement"/>.
        /// </summary>
        /// <returns>The mobile; null when none could be had, or (a violation) <paramref name="obj"/> is not a mobile this
        /// World holds: rejected before any side effect, no pre-T1 path (the handler only passes what it just looked up).</returns>
        public Mobile UpsertMobile
        (
            Entity obj,
            uint serial,
            ushort graphic,
            byte graphicInc,
            ushort x,
            ushort y,
            sbyte z,
            Direction direction,
            ushort hue,
            Flags flags,
            out bool created,
            out Placement placement
        )
        {
            created = false;
            placement = default;

            if (!Admitted(nameof(UpsertMobile), serial))
            {
                return null;
            }

            // 1. resolve: the handler's obj, or the created mobile
            Mobile mobile;

            if (obj == null || obj.IsDestroyed)
            {
                mobile = _world.GetOrCreateMobile(serial);

                if (mobile == null)
                {
                    return null;
                }

                created = true;
            }
            else if (obj is Mobile m && obj.Serial == serial && Holds(m))
            {
                mobile = m;
            }
            else
            {
                Reject(Violation.NotHeld, $"WorldWriter.UpsertMobile: 0x{serial:X8} is not a mobile this World holds", rejected: true);

                return null;
            }

            // 2. compute: every read of the previous state is here
            ushort bodyGraphic = (ushort)(graphic + graphicInc);
            bool body = Mobile.BodyState(bodyGraphic, out bool isFemale, out RaceType race) && created;

            // the position and direction after the creation's writes (the old created branch wrote the packet's)
            ushort x0 = created ? x : mobile.X;
            ushort y0 = created ? y : mobile.Y;
            sbyte z0 = created ? z : mobile.Z;
            Direction dir0 = created ? direction & Direction.Up : mobile.Direction;

            bool player = _world.Player != null && serial == _world.Player.Serial;
            Direction cleanedDir = direction & Direction.Up;
            bool isRun = (direction & Direction.Running) != 0;
            bool reset = false;
            Mobile.StepPlan step = default;

            if (!player)
            {
                // the old handler's World.Get(mobile) == null or the unplaced marker; then EnqueueStep, whose false (a full
                // queue) took the same reset. After a reset EnqueueStep found the queue ending at the packet's position and
                // did nothing. World.Get(serial) is never null here: the mobile is held under serial (validated above, or
                // just created by GetOrCreateMobile), and World.Get looks among the mobiles whenever the items have none.
                reset = x0 == 0xFFFF && y0 == 0xFFFF;

                if (!reset)
                {
                    int endX = x0, endY = y0;
                    sbyte endZ = z0;
                    Direction endDir = dir0;

                    if (mobile.Steps.Count != 0)
                    {
                        mobile.GetEndPosition(out endX, out endY, out endZ, out endDir);
                    }

                    mobile.PlanStep(x, y, z, cleanedDir, isRun, endX, endY, endZ, endDir, out step);
                    reset = !step.Accepted;
                }
            }

            MobileState v;
            v.Serial = serial;
            v.Graphic = (ushort)(bodyGraphic & 0x3FFF);
            v.Hue = Entity.FixedHue(hue);
            v.X = reset ? x : x0;
            v.Y = reset ? y : y0;
            v.Z = reset ? z : z0;
            v.Direction = reset ? cleanedDir : dir0;
            v.Flags = flags;
            v.IsPlayer = ReferenceEquals(mobile, _world.Player);

            // an earlier placement of this mobile is superseded: this upsert issues its own
            CancelPlacements(mobile);

            using (Open(mobile, mobile))
            {
                // 3. write: the ECS, then the cache from v in the old order
                EcsEntity e = _world.Ecs.CommitMobile(mobile, in v);

                if (created)
                {
                    mobile.Graphic = bodyGraphic;

                    if (body)
                    {
                        mobile.IsFemale = isFemale;
                        mobile.Race = race;
                    }
                }

                if (created || reset)
                {
                    mobile.Direction = v.Direction;
                    mobile.X = v.X;
                    mobile.Y = v.Y;
                    mobile.Z = v.Z;
                }

                if (reset)
                {
                    mobile.IsRunning = isRun;
                    mobile.ClearSteps();
                }
                else
                {
                    mobile.ApplyStep(in step);
                }

                mobile.Graphic = v.Graphic;
                mobile.Hue = v.Hue;
                mobile.Flags = v.Flags;

                // 4. derived state: the inputs and the clock, from the values written above
                _world.Ecs.CommitMobileInputs(mobile, e);
            }

            placement = Issue(mobile);

            return mobile;
        }

        /// <summary>
        /// The mobile branch of <c>UpdateGameObject</c>, second half: puts the mobile of <paramref name="placement"/> (what
        /// <see cref="UpsertMobile"/> issued) on its tile at its position, after the handler's callbacks, as before T1. The ECS
        /// already holds its values; the player's placement is not folded (its position callbacks may change it, and what they
        /// change publishes as before T1); any other mobile's is (no callback runs, the tile's Sync would rewrite the same
        /// record and inputs). Consumes the placement.
        /// A placement that is not outstanding is a violation: in a release build the mobile is placed as before T1.
        /// </summary>
        public void PlaceMobile(in Placement placement)
        {
            Mobile mobile = placement.Subject as Mobile;

            // a nested placement is rejected; its token stays outstanding
            if (!Admitted(nameof(PlaceMobile), placement.Subject?.Serial ?? 0))
            {
                return;
            }

            if (mobile == null || !Consume(placement))
            {
                Reject(Violation.Placement, $"WorldWriter.PlaceMobile: 0x{placement.Subject?.Serial ?? 0:X8} is not an outstanding mobile placement (used twice, superseded, abandoned, an item's or never issued)");

                if (mobile != null)
                {
                    PlacePreT1(mobile);
                }

                return;
            }

            if (mobile.IsDestroyed || !ReferenceEquals(_world.Mobiles.Get(mobile.Serial), mobile))
            {
                return;
            }

            // the player is not folded (Codex audit of part 4, #1): SetInWorldTile runs its position callbacks
            // (PlayerMobile.OnPositionChanged), where a plugin's RequestMove walks the player (Player.Walk: the queue, the
            // timers, a turn). Their input and clock pushes and Syncs, and the tile's own Sync, publish as before T1.
            // Any other mobile is folded (funnel part 6): GameObject.OnPositionChanged is empty for it, so nothing between
            // UpsertMobile's commit and here changes its record or inputs, and the tile's Sync and input push only rewrote
            // the values UpsertMobile committed (+0.1 us per 0x77/0x78 in the E2 bench).
            bool player = mobile is PlayerMobile || ReferenceEquals(mobile, _world.Player);

            using (Open(mobile, player ? null : mobile))
            {
                mobile.SetInWorldTile(mobile.X, mobile.Y, mobile.Z);
            }
        }

        /// <summary>
        /// 0x77 (UpdateCharacter) and 0x78/0xD3 (UpdateObject): the mobile's notoriety. <paramref name="mobile"/> is the
        /// handler's instance (0x77: <c>World.Mobiles.Get</c>; 0x78: the mobile <c>World.Get</c> returned). Notoriety is an
        /// input only (no record field): it is written, then the inputs are pushed. A mobile this World does not hold is a
        /// violation, rejected (nothing changes; no pre-T1 path).
        /// </summary>
        public void SetNotoriety(Mobile mobile, NotorietyFlag notoriety)
        {
            if (mobile == null || !Admitted(nameof(SetNotoriety), mobile.Serial))
            {
                return;
            }

            bool detached = !Holds(mobile);

            // funnel part 5: 0x22 (ConfirmWalk) passes World.Player, which may be the detached player (see IsDetachedPlayer)
            if (detached && !IsDetachedPlayer(mobile))
            {
                Reject(Violation.NotHeld, $"WorldWriter.SetNotoriety: 0x{mobile.Serial:X8} is not the mobile this World holds for its serial", rejected: true);

                return;
            }

            using (Open(mobile, detached ? null : mobile))
            {
                _world.Ecs.CommitNotoriety(mobile, notoriety);
                mobile.NotorietyFlag = notoriety;
                _world.Ecs.CommitMobileInputs(mobile);
            }
        }

        /// <summary>
        /// The player's body from 0x77 and 0x78/0xD3 (<paramref name="checkBody"/>: <c>CheckGraphicChange</c>'s sex and race)
        /// and from 0x20 (UpdatePlayer, with its direction, <paramref name="setDirection"/>; no CheckGraphicChange): the
        /// graphic, the direction, the hue (FixHue) and the flags in that order (0x20's; 0x77 wrote the flags first, which
        /// nothing in between could see), the ECS record first, then the cache, then the derived inputs. The position stays
        /// (0x77/0x78 ignore the packet's; 0x20 moves the player with <see cref="MoveMobile"/> after its other work). The
        /// player's direction change runs its door check inside the operation, as the setter did before T1. A mobile this
        /// World does not hold is a violation, rejected (no pre-T1 path), except the detached <c>World.Player</c> (removed
        /// under its serial while World.Player stays), which is written as before T1 (nothing of it reaches the ECS).
        /// </summary>
        public void SetMobileBody(Mobile mobile, ushort graphic, ushort hue, Flags flags, bool checkBody, bool setDirection, Direction direction)
        {
            if (mobile == null || !Admitted(nameof(SetMobileBody), mobile.Serial))
            {
                return;
            }

            bool detached = !Holds(mobile);

            if (detached && !IsDetachedPlayer(mobile))
            {
                Reject(Violation.NotHeld, $"WorldWriter.SetMobileBody: 0x{mobile.Serial:X8} is not the mobile this World holds for its serial", rejected: true);

                return;
            }

            // 2. compute
            bool body = Mobile.BodyState(graphic, out bool isFemale, out RaceType race) && checkBody;

            MobileState v;
            v.Serial = mobile.Serial;
            v.Graphic = graphic;
            v.Hue = Entity.FixedHue(hue);
            v.X = mobile.X;
            v.Y = mobile.Y;
            v.Z = mobile.Z;
            v.Direction = setDirection ? direction : mobile.Direction;
            v.Flags = flags;
            v.IsPlayer = ReferenceEquals(mobile, _world.Player);

            // the detached player is not folded: nothing of it reaches the ECS (every commit, Sync and push ignores an
            // instance World does not hold), so its writes are the pre-T1 path's
            using (Open(mobile, detached ? null : mobile))
            {
                EcsEntity e = _world.Ecs.CommitMobile(mobile, in v);

                mobile.Graphic = v.Graphic;

                if (body)
                {
                    mobile.IsFemale = isFemale;
                    mobile.Race = race;
                }

                if (setDirection)
                {
                    mobile.Direction = v.Direction;
                }

                mobile.Hue = v.Hue;
                mobile.Flags = v.Flags;

                _world.Ecs.CommitMobileInputs(mobile, e);
            }
        }

        /// <summary>
        /// 0x20 (UpdatePlayer), last: the mobile's tile at the packet's position (<c>SetInWorldTile</c>, with the player's
        /// position callbacks, as before T1). Commits the record with the new position and the mobile's other values as they
        /// are when this operation resolves (the handler's work since <see cref="SetMobileBody"/>), then the cache and the
        /// tile (not folded: the position callbacks' changes and the tile's Sync publish as before T1), then the derived
        /// inputs. It neither issues nor consumes a placement. A mobile this World does not hold is a violation, rejected (no
        /// pre-T1 path), except the detached <c>World.Player</c> (removed under its serial), which is written as before T1.
        /// </summary>
        public void MoveMobile(Mobile mobile, ushort x, ushort y, sbyte z)
        {
            if (mobile == null || !Admitted(nameof(MoveMobile), mobile.Serial))
            {
                return;
            }

            if (!Holds(mobile) && !IsDetachedPlayer(mobile))
            {
                Reject(Violation.NotHeld, $"WorldWriter.MoveMobile: 0x{mobile.Serial:X8} is not the mobile this World holds for its serial", rejected: true);

                return;
            }

            MobileState v;
            v.Serial = mobile.Serial;
            v.Graphic = mobile.Graphic;
            v.Hue = mobile.Hue;
            v.X = x;
            v.Y = y;
            v.Z = z;
            v.Direction = mobile.Direction;
            v.Flags = mobile.Flags;
            v.IsPlayer = ReferenceEquals(mobile, _world.Player);

            // not folded (Codex audit of part 4, #1): the player's position callbacks may walk it (a plugin's RequestMove);
            // their pushes and Syncs, and the tile's Sync, publish as before T1
            using (Open(mobile, null))
            {
                EcsEntity e = _world.Ecs.CommitMobile(mobile, in v);
                mobile.SetInWorldTile(v.X, v.Y, v.Z);
                _world.Ecs.CommitMobileInputs(mobile, e);
            }
        }

        /// <summary>
        /// 0x1B (EnterWorld): the player's body graphic, then <c>CheckGraphicChange</c>'s sex and race
        /// (<paramref name="checkBody"/>), nothing else (hue, flags, direction and position stay; 0x1B moves the player with
        /// <see cref="MoveMobile"/> and turns it with <see cref="SetMobileDirection"/>). No callback runs in these setters, so the
        /// mobile is folded: one record commit, then the cache, then the derived inputs. A mobile this World does not hold is
        /// a violation, rejected (no pre-T1 path), except the detached <c>World.Player</c>, written as before T1.
        /// </summary>
        public void SetMobileGraphic(Mobile mobile, ushort graphic, bool checkBody)
        {
            if (mobile == null || !Admitted(nameof(SetMobileGraphic), mobile.Serial))
            {
                return;
            }

            bool detached = !Holds(mobile);

            if (detached && !IsDetachedPlayer(mobile))
            {
                Reject(Violation.NotHeld, $"WorldWriter.SetMobileGraphic: 0x{mobile.Serial:X8} is not the mobile this World holds for its serial", rejected: true);

                return;
            }

            bool body = Mobile.BodyState(graphic, out bool isFemale, out RaceType race) && checkBody;

            MobileState v;
            v.Serial = mobile.Serial;
            v.Graphic = graphic;
            v.Hue = mobile.Hue;
            v.X = mobile.X;
            v.Y = mobile.Y;
            v.Z = mobile.Z;
            v.Direction = mobile.Direction;
            v.Flags = mobile.Flags;
            v.IsPlayer = ReferenceEquals(mobile, _world.Player);

            using (Open(mobile, detached ? null : mobile))
            {
                EcsEntity e = _world.Ecs.CommitMobile(mobile, in v);

                mobile.Graphic = v.Graphic;

                if (body)
                {
                    mobile.IsFemale = isFemale;
                    mobile.Race = race;
                }

                _world.Ecs.CommitMobileInputs(mobile, e);
            }
        }

        /// <summary>
        /// 0x1B (EnterWorld) and 0x21 (DenyWalk): the player's direction from the packet. The record is committed with the new
        /// direction first, then the setter runs <b>unfolded</b>: the player's direction change runs its door check, which can
        /// send a packet (plugin send hooks run synchronously), so the setter's Sync publishes as before T1. Then the derived
        /// inputs. A mobile this World does not hold is a violation, rejected (no pre-T1 path), except the detached
        /// <c>World.Player</c>, written as before T1.
        /// </summary>
        public void SetMobileDirection(Mobile mobile, Direction direction)
        {
            if (mobile == null || !Admitted(nameof(SetMobileDirection), mobile.Serial))
            {
                return;
            }

            if (!Holds(mobile) && !IsDetachedPlayer(mobile))
            {
                Reject(Violation.NotHeld, $"WorldWriter.SetMobileDirection: 0x{mobile.Serial:X8} is not the mobile this World holds for its serial", rejected: true);

                return;
            }

            MobileState v;
            v.Serial = mobile.Serial;
            v.Graphic = mobile.Graphic;
            v.Hue = mobile.Hue;
            v.X = mobile.X;
            v.Y = mobile.Y;
            v.Z = mobile.Z;
            v.Direction = direction;
            v.Flags = mobile.Flags;
            v.IsPlayer = ReferenceEquals(mobile, _world.Player);

            using (Open(mobile, null))
            {
                EcsEntity e = _world.Ecs.CommitMobile(mobile, in v);
                mobile.Direction = v.Direction;
                _world.Ecs.CommitMobileInputs(mobile, e);
            }
        }

        /// <summary>
        /// The walker's step clear (<c>WalkerManager.DenyWalk</c>: 0x21, and 0x20's deny before its move): <c>ClearSteps</c>,
        /// the walk queue emptied and the offset zeroed, then the derived inputs. No record field changes (the offset and the
        /// queue are inputs only). No callback runs, so the mobile is folded. A mobile this World does not hold is a violation,
        /// rejected (no pre-T1 path), except the detached <c>World.Player</c>, written as before T1.
        /// </summary>
        public void StopMobile(Mobile mobile)
        {
            if (mobile == null || !Admitted(nameof(StopMobile), mobile.Serial))
            {
                return;
            }

            bool detached = !Holds(mobile);

            if (detached && !IsDetachedPlayer(mobile))
            {
                Reject(Violation.NotHeld, $"WorldWriter.StopMobile: 0x{mobile.Serial:X8} is not the mobile this World holds for its serial", rejected: true);

                return;
            }

            using (Open(mobile, detached ? null : mobile))
            {
                mobile.ClearSteps();
                _world.Ecs.CommitMobileInputs(mobile);
            }
        }

        /// <summary>
        /// <c>Mobile.ProcessSteps</c>' step completion (funnel part 5): the front step <paramref name="step"/> is done, so the
        /// mobile takes its position, direction and run state, the offset is zeroed, the step leaves the queue and the idle
        /// timer restarts. Unless <paramref name="directionChange"/> (a turn in place: ProcessSteps goes on with the next step
        /// after this returns), it then refreshes its screen position, closes the player's out-of-range gumps, re-adds itself
        /// to its tile and stamps <c>LastStepTime</c>, in the old order. The derived inputs and clock last.
        /// <para>
        /// The record is committed once, first, from the step's values. A mobile other than the player is folded: none of its
        /// setters or helpers here run a callback, so its five hook Syncs and pushes become one commit and one push. The player is
        /// not folded: its direction change (door check), screen position (the plugins' position callback, the door and corpse
        /// checks) and <c>CloseRangedGumps</c> run code that can send packets and call plugins, so their pushes and Syncs publish
        /// as before T1, and the inputs are also committed between the two halves, where the old code pushed them.
        /// </para>
        /// <para>
        /// Called per step from the per-frame update; ProcessSteps passes its own instance, so a mobile this World does not
        /// hold for its serial (a stale instance still updating, or the detached player) is written as before T1, unfolded
        /// (every commit, Sync and push ignores it). Only an instance of another World is a violation, rejected (nothing
        /// changes; the step stays queued).
        /// </para>
        /// </summary>
        public void CompleteStep(Mobile mobile, in Mobile.Step step, bool directionChange)
        {
            if (mobile == null || !Admitted(nameof(CompleteStep), mobile.Serial))
            {
                return;
            }

            if (mobile.World != _world)
            {
                Reject(Violation.NotHeld, $"WorldWriter.CompleteStep: 0x{mobile.Serial:X8} belongs to another World", rejected: true);

                return;
            }

            bool fold = Holds(mobile) && !ReferenceEquals(mobile, _world.Player);

            // 2. compute (the step is the caller's front step: read here, before RemoveFirstStep)
            MobileState v;
            v.Serial = mobile.Serial;
            v.Graphic = mobile.Graphic;
            v.Hue = mobile.Hue;
            v.X = (ushort)step.X;
            v.Y = (ushort)step.Y;
            v.Z = step.Z;
            v.Direction = (Direction)step.Direction;
            v.Flags = mobile.Flags;
            v.IsPlayer = ReferenceEquals(mobile, _world.Player);
            bool run = step.Run;
            uint stepTime = Time.Ticks;

            using (Open(mobile, fold ? mobile : null))
            {
                EcsEntity e = _world.Ecs.CommitMobile(mobile, in v);

                mobile.X = v.X;
                mobile.Y = v.Y;
                mobile.Z = v.Z;
                mobile.Direction = v.Direction;
                mobile.IsRunning = run;
                mobile.OffsetX = 0;
                mobile.OffsetY = 0;
                mobile.OffsetZ = 0;
                mobile.RemoveFirstStep();
                mobile.CalculateRandomIdleTime();

                if (!directionChange)
                {
                    if (!fold)
                    {
                        // the old first push, before the callbacks below
                        _world.Ecs.CommitMobileInputs(mobile, e);
                    }

                    mobile.UpdateScreenPosition();

                    // looked up here, after the position callbacks, as the old code did
                    if (_world.InGame && mobile.Serial == _world.Player)
                    {
                        _world.Player.CloseRangedGumps();
                    }

                    if (mobile.IsPlaced)
                    {
                        mobile.AddToTile();
                    }

                    mobile.LastStepTime = stepTime;
                }

                _world.Ecs.CommitMobileInputs(mobile, e);
            }
        }

        /// <summary>
        /// 0x1D (DeleteObject) for a mobile: removes it and everything it wears or holds, at any depth (<c>World.RemoveMobile</c>
        /// before T1: each child's removal with its contents, OPL, Destroy, which deletes the ECS entities through
        /// <see cref="WorldEcs.OnDestroyed"/>, then out of the mobiles table). A removal: it owns the removed subtree, writes no
        /// value of its own, and does not fold (each removal's Sync runs as before). Cancels the outstanding placements of the
        /// mobile and of every removed item.
        /// </summary>
        /// <param name="mobile">The handler's entity (<c>World.Get(serial)</c> found this mobile, so <c>World.Mobiles.Get</c>,
        /// the old removal's lookup, returns the same instance). One this World does not hold for its serial is a violation,
        /// rejected before any side effect (nothing is removed; no pre-T1 path: the handler only passes what it just found).</param>
        public void RemoveMobile(Mobile mobile)
        {
            if (mobile == null || !Admitted(nameof(RemoveMobile), mobile.Serial))
            {
                return;
            }

            if (mobile.IsDestroyed || !Holds(mobile))
            {
                Reject(Violation.NotHeld, $"WorldWriter.RemoveMobile: 0x{mobile.Serial:X8} is not the live mobile this World holds for its serial", rejected: true);

                return;
            }

            RemoveMobileSubtree(mobile);
        }

        // the removal of RemoveMobile and of PlaceInContainer's mobile under an item serial, its own scope (owning the removed
        // subtree): first the placements of the mobile and of everything it wears or holds are cancelled
        private void RemoveMobileSubtree(Mobile mobile)
        {
            uint serial = mobile.Serial;
            CancelSubtreePlacements(mobile);

            using (Open(mobile, null))
            {
#if CUO_DEVTOOLS
                if (FunnelCheck.Enabled)
                {
                    CollectSubtree(mobile);
                }
#endif

                _world.RemoveMobile(serial, true);
            }
        }

        /// <summary>
        /// The range sweep (<c>World.Update</c>, every 50 ms: a mobile beyond the view range, in the
        /// E4 clock path): <c>World.RemoveMobile(mobile)</c> before T1, which removes it and everything it wears or
        /// holds and destroys it, but leaves it in the mobiles table (no force: the sweep iterates the table; the destroyed
        /// mobile leaves it with the frame's destroyed list). A removal like <see cref="RemoveMobile"/>: it owns the removed
        /// subtree, writes no value of its own, does not fold, and first cancels the outstanding placements of the mobile and
        /// of every removed item. A destroyed mobile is a no-op (the old removal returned false), with no violation.
        /// </summary>
        /// <param name="mobile">The sweep's instance (from <c>World.Mobiles</c>, so the old lookup by its serial returned it).
        /// One this World does not hold for its serial is a violation, rejected before any side effect (nothing is removed; no
        /// pre-T1 path: the sweep passes only what it is iterating).</param>
        public void SweepMobile(Mobile mobile)
        {
            if (mobile == null || !Admitted(nameof(SweepMobile), mobile.Serial))
            {
                return;
            }

            if (!Holds(mobile))
            {
                Reject(Violation.NotHeld, $"WorldWriter.SweepMobile: 0x{mobile.Serial:X8} is not the mobile this World holds for its serial", rejected: true);

                return;
            }

            if (mobile.IsDestroyed)
            {
                return;
            }

            CancelSubtreePlacements(mobile);

            using (Open(mobile, null))
            {
#if CUO_DEVTOOLS
                if (FunnelCheck.Enabled)
                {
                    CollectSubtree(mobile);
                }
#endif

                _world.RemoveMobile(mobile.Serial);
            }
        }

        /// <summary>
        /// The range sweep for a ground item beyond the view range (<c>World.Update</c>'s item loops, after the house check for
        /// a multi): <c>World.RemoveItem(item)</c> before T1, which removes it with its contents and destroys it, leaving it in
        /// the items table (no force). A removal: owns the removed subtree, no fold, cancels the placements of the item and of
        /// its contents first. A destroyed item is a no-op, with no violation; one this World does not hold for its serial is a
        /// violation, rejected before any side effect (no pre-T1 path).
        /// </summary>
        public void SweepItem(Item item)
        {
            if (item == null || !Admitted(nameof(SweepItem), item.Serial))
            {
                return;
            }

            if (!Holds(item))
            {
                Reject(Violation.NotHeld, $"WorldWriter.SweepItem: 0x{item.Serial:X8} is not the item this World holds for its serial", rejected: true);

                return;
            }

            if (item.IsDestroyed)
            {
                return;
            }

            CancelSubtreePlacements(item);

            using (Open(item, null))
            {
#if CUO_DEVTOOLS
                if (FunnelCheck.Enabled)
                {
                    CollectSubtree(item);
                }
#endif

                _world.RemoveItem(item.Serial);
            }
        }

        // ---- funnel part 6, group 4: status and hits. Hits, HitsMax, the mana, the stamina, the name, IsRenamable and the race
        // are cache-only (no mirror record field and no MobileInputs reads them; HitsPercentage and its text are the setters'
        // own derived state), so these operations are admission, the held check, the scope and the writes in the handler's
        // order. IsFemale and the SA poison are inputs, the flags a record field: those operations commit.

        /// <summary>
        /// 0xA1 (UpdateHitpoints) and 0x2D (MobileAttributes): the hits of an item or a mobile (<c>World.Get</c>'s), HitsMax then
        /// Hits as the handlers wrote them (each setter refreshes HitsPercentage, so the order is kept), then a pending hits
        /// request is marked received. Nothing reaches the ECS. An entity this World does not hold is a violation, rejected
        /// (no pre-T1 path), except the detached <c>World.Player</c>, written as before T1.
        /// </summary>
        public void SetHits(Entity entity, ushort hitsMax, ushort hits)
        {
            if (entity == null || !Admitted(nameof(SetHits), entity.Serial) || !Accepted(entity, nameof(SetHits)))
            {
                return;
            }

            using (Open(entity, null))
            {
                _world.Ecs.CommitVitals(entity, hits, hitsMax);
                entity.HitsMax = hitsMax;
                entity.Hits = hits;

                if (entity.HitsRequest == HitsRequestStatus.Pending)
                {
                    entity.HitsRequest = HitsRequestStatus.Received;
                }
            }
        }

        /// <summary>
        /// 0x11 (CharacterStatus): the name, then Hits then HitsMax (0x11's order), then a pending hits request is marked
        /// received. Nothing reaches the ECS. The handler reads the old name before this (the window title) and runs the
        /// rest of the packet after it. Rejections as <see cref="SetHits"/>.
        /// </summary>
        public void SetStatus(Entity entity, string name, ushort hits, ushort hitsMax)
        {
            if (entity == null || !Admitted(nameof(SetStatus), entity.Serial) || !Accepted(entity, nameof(SetStatus)))
            {
                return;
            }

            using (Open(entity, null))
            {
                _world.Ecs.CommitName(entity, name);
                _world.Ecs.CommitVitals(entity, hits, hitsMax);
                entity.Name = name;
                entity.Hits = hits;
                entity.HitsMax = hitsMax;

                if (entity.HitsRequest == HitsRequestStatus.Pending)
                {
                    entity.HitsRequest = HitsRequestStatus.Received;
                }
            }
        }

        /// <summary>
        /// 0x11 (CharacterStatus) for a mobile: IsRenamable, then (<paramref name="setFemale"/>: the packet has the extended
        /// part) IsFemale, an input: the mobile is folded and its inputs are committed once (the setter's push before T1). A
        /// mobile this World does not hold is a violation, rejected (no pre-T1 path), except the detached <c>World.Player</c>.
        /// </summary>
        public void SetMobileStatus(Mobile mobile, bool renamable, bool setFemale, bool female)
        {
            if (mobile == null || !Admitted(nameof(SetMobileStatus), mobile.Serial) || !Accepted(mobile, nameof(SetMobileStatus)))
            {
                return;
            }

            bool detached = !Holds(mobile);

            using (Open(mobile, detached ? null : mobile))
            {
                mobile.IsRenamable = renamable;

                if (setFemale)
                {
                    mobile.IsFemale = female;
                    _world.Ecs.CommitMobileInputs(mobile);
                }
            }
        }

        /// <summary>
        /// 0xA2 (UpdateMana), 0x2D (MobileAttributes) and the player's 0x11: ManaMax and Mana, committed to the record first
        /// (T4). Rejections as <see cref="SetMobileStatus"/>.
        /// </summary>
        public void SetMana(Mobile mobile, ushort manaMax, ushort mana)
        {
            if (mobile == null || !Admitted(nameof(SetMana), mobile.Serial) || !Accepted(mobile, nameof(SetMana)))
            {
                return;
            }

            using (Open(mobile, null))
            {
                _world.Ecs.CommitMana(mobile, manaMax, mana);
                mobile.ManaMax = manaMax;
                mobile.Mana = mana;
            }
        }

        /// <summary>
        /// 0xA3 (UpdateStamina), 0x2D (MobileAttributes) and the player's 0x11: StaminaMax and Stamina, committed to the record
        /// first (T4). Rejections as <see cref="SetMobileStatus"/>.
        /// </summary>
        public void SetStamina(Mobile mobile, ushort staminaMax, ushort stamina)
        {
            if (mobile == null || !Admitted(nameof(SetStamina), mobile.Serial) || !Accepted(mobile, nameof(SetStamina)))
            {
                return;
            }

            using (Open(mobile, null))
            {
                _world.Ecs.CommitStamina(mobile, staminaMax, stamina);
                mobile.StaminaMax = staminaMax;
                mobile.Stamina = stamina;
            }
        }

        /// <summary>
        /// 0x11 (CharacterStatus, ML and later) for the player: the race. Nothing reaches the ECS (the race is not an input).
        /// Rejections as <see cref="SetMobileStatus"/>.
        /// </summary>
        public void SetRace(Mobile mobile, RaceType race)
        {
            if (mobile == null || !Admitted(nameof(SetRace), mobile.Serial) || !Accepted(mobile, nameof(SetRace)))
            {
                return;
            }

            using (Open(mobile, null))
            {
                mobile.Race = race;
            }
        }

        /// <summary>
        /// 0x16/0x17 (NewHealthbarUpdate): the bar states. The handler folds the packet's list into the final values (the
        /// poisoned and yellow-bar flags, and from 7.0 on the SA poison instead of the poisoned flag); before T1 each entry
        /// was a write (SetSAPoison pushed the inputs) and the handler ended with a Sync. Now: one record commit with the
        /// final flags, then the cache, then one inputs commit (the mobile is folded). A mobile this World does not hold is a
        /// violation, rejected (no pre-T1 path).
        /// </summary>
        public void SetHealthbar(Mobile mobile, Flags flags, bool setSAPoison, bool saPoison)
        {
            if (mobile == null || !Admitted(nameof(SetHealthbar), mobile.Serial) || !Accepted(mobile, nameof(SetHealthbar)))
            {
                return;
            }

            bool detached = !Holds(mobile);

            MobileState v;
            v.Serial = mobile.Serial;
            v.Graphic = mobile.Graphic;
            v.Hue = mobile.Hue;
            v.X = mobile.X;
            v.Y = mobile.Y;
            v.Z = mobile.Z;
            v.Direction = mobile.Direction;
            v.Flags = flags;
            v.IsPlayer = ReferenceEquals(mobile, _world.Player);

            using (Open(mobile, detached ? null : mobile))
            {
                EcsEntity e = _world.Ecs.CommitMobile(mobile, in v);

                if (setSAPoison)
                {
                    mobile.SetSAPoison(saPoison);
                }

                mobile.Flags = v.Flags;
                _world.Ecs.CommitMobileInputs(mobile, e);
            }
        }

        /// <summary>
        /// The server's animations (funnel part 6, group 4.2): 0x6E (CharacterAnimation), 0xE2 (NewCharacterAnimation), the
        /// death animation of 0xAF (DisplayDeath, after its re-key) and the held frames of 0xBF.19 and 0xBF.2B:
        /// <c>Mobile.SetAnimation</c> (the animation state, the frame due time and the random idle timer), then
        /// (<paramref name="frame"/> &gt;= 0) the frame index and (<paramref name="hold"/>) <c>ExecuteAnimation</c> off. None of
        /// these is a record field or an input; the clock (the due times) is. The mobile is folded (SetAnimation's clock push
        /// is skipped) and the clock is committed once after the writes. A mobile this World does not hold is a violation,
        /// rejected (no pre-T1 path), except the detached <c>World.Player</c>, written as before T1.
        /// <para>Not converted: the per-frame animation (<c>Mobile.Update</c>: <c>SetIdleAnimation</c>, <c>ProcessAnimation</c>),
        /// which runs <c>ProcessSteps</c> (its step completion is an operation) and removes finished corpse mobiles inside
        /// it: see the poc-log, "T1 funnel, part 6".</para>
        /// </summary>
        public void AnimateMobile(Mobile mobile, byte id, byte interval = 0, byte frameCount = 0, ushort repeatCount = 0, bool repeat = false, bool forward = false, bool fromServer = false, int frame = -1, bool hold = false)
        {
            if (mobile == null || !Admitted(nameof(AnimateMobile), mobile.Serial) || !Accepted(mobile, nameof(AnimateMobile)))
            {
                return;
            }

            bool detached = !Holds(mobile);

            using (Open(mobile, detached ? null : mobile))
            {
                mobile.SetAnimation(id, interval, frameCount, repeatCount, repeat, forward, fromServer);

                if (frame >= 0)
                {
                    mobile.AnimIndex = (byte)frame;
                }

                if (hold)
                {
                    mobile.ExecuteAnimation = false;
                }

                _world.Ecs.CommitMobileClock(mobile);
            }
        }

        // ---- funnel part 6, group 4.3 (misc): the boat (BoatMovingManager, 0xF6's steps). These run the pre-T1 writes in their
        // old order inside a scope that owns the object, unfolded: UpdateScreenPosition and AddToTile Sync the record as
        // before, and a passenger may be the player, whose position callbacks (plugins, the door and corpse checks) must
        // publish as before T1. The per-frame offset interpolation between steps stays direct (like ProcessSteps').

        /// <summary>
        /// A boat's step is done (<c>BoatMovingManager.Update</c>): the boat takes the step's position, refreshes its screen
        /// position, its offset is zeroed and it is re-added to its tile. Not folded (see above). A boat this World does not
        /// hold is a violation, rejected (no pre-T1 path: the manager passes <c>World.Items.Get</c>'s).
        /// </summary>
        public void CompleteBoatStep(Item boat, ushort x, ushort y, sbyte z)
        {
            if (boat == null || !Admitted(nameof(CompleteBoatStep), boat.Serial) || !Accepted(boat, nameof(CompleteBoatStep)))
            {
                return;
            }

            using (Open(boat, null))
            {
                boat.X = x;
                boat.Y = y;
                boat.Z = z;
                boat.UpdateScreenPosition();

                boat.OffsetX = 0;
                boat.OffsetY = 0;
                boat.OffsetZ = 0;

                if (boat.IsPlaced)
                {
                    boat.AddToTile();
                }
            }
        }

        /// <summary>
        /// 0xF6 without smooth boat movement (the profile's <c>UseSmoothBoatMovement</c> off, the default): the boat jumps to
        /// the packet's position, <c>SetInWorldTile</c> (position, screen position flag, re-added to its tile), as the handler
        /// did before T1; the handler then regenerates its house and moves the passengers (UpdatePlayer, UpdateGameObject).
        /// Not folded (as <see cref="CompleteBoatStep"/>). Rejections as <see cref="CompleteBoatStep"/> (found by the part 7
        /// gate: the boat's first live moves showed <c>SetInWorldTile</c> writing from <c>PacketHandlers.BoatMoving</c>).
        /// </summary>
        public void JumpBoat(Item boat, ushort x, ushort y, sbyte z)
        {
            if (boat == null || !Admitted(nameof(JumpBoat), boat.Serial) || !Accepted(boat, nameof(JumpBoat)))
            {
                return;
            }

            using (Open(boat, null))
            {
                boat.SetInWorldTile(x, y, z);
            }
        }

        /// <summary>
        /// An object on a boat whose step is done (<c>BoatMovingManager.UpdateEntitiesInside</c>): the item or mobile takes
        /// its new position (the boat's minus its offset on deck), refreshes its screen position (the player's position
        /// callbacks run, unfolded), its offset is zeroed and it is re-added to its tile. Rejections as
        /// <see cref="SetHits"/>.
        /// </summary>
        public void MoveBoatPassenger(Entity entity, ushort x, ushort y, sbyte z)
        {
            if (entity == null || !Admitted(nameof(MoveBoatPassenger), entity.Serial) || !Accepted(entity, nameof(MoveBoatPassenger)))
            {
                return;
            }

            using (Open(entity, null))
            {
                entity.X = x;
                entity.Y = y;
                entity.Z = z;
                entity.UpdateScreenPosition();

                entity.OffsetX = 0;
                entity.OffsetY = 0;
                entity.OffsetZ = 0;

                if (entity.IsPlaced)
                {
                    entity.AddToTile();
                }
            }

            _world.Ecs.PushSteps(entity); // T6c: a carried mobile's position and offset reach its Steps component
        }

        /// <summary>
        /// The boat's step clock (<c>BoatMovingManager</c>: a first step queued, a step done): <c>LastStepTime</c>. Nothing
        /// reaches the ECS (an item's LastStepTime is not a record field). Rejections as <see cref="CompleteBoatStep"/>.
        /// </summary>
        public void SetBoatStepTime(Item boat, uint time)
        {
            if (boat == null || !Admitted(nameof(SetBoatStepTime), boat.Serial) || !Accepted(boat, nameof(SetBoatStepTime)))
            {
                return;
            }

            using (Open(boat, null))
            {
                boat.LastStepTime = time;
            }
        }

        /// <summary>
        /// <c>BoatMovingManager.ClearSteps</c> (0xD8's and 0xBF.1D's custom-house regeneration, a multi's (re)load through
        /// <see cref="ClearBoatSteps"/>): the offset of the boat or of one object on it
        /// back to zero. Nothing reaches the ECS (the offset is not a record field, and an item has no inputs).
        /// Rejections as <see cref="SetHits"/>.
        /// </summary>
        public void ResetBoatOffset(Entity entity)
        {
            if (entity == null || !Admitted(nameof(ResetBoatOffset), entity.Serial) || !Accepted(entity, nameof(ResetBoatOffset)))
            {
                return;
            }

            using (Open(entity, null))
            {
                entity.OffsetX = 0;
                entity.OffsetY = 0;
                entity.OffsetZ = 0;
            }

            _world.Ecs.PushSteps(entity); // T6c
        }

        // ---- funnel part 7, group 4: the per-frame offset interpolation between steps (Mobile.ProcessSteps,
        // BoatMovingManager.Update). Each runs the old writes in their old order inside a scope that owns the object,
        // unfolded and committing nothing: an offset is not a record field and no input (the ECS sprite extract reads the
        // object's offset each frame), so nothing is pushed, as before T1. The caller computes the values from its step, as
        // the old code did (the frame delay, the step's timing and MovementSpeed.GetPixelOffset stay with the step's owner).

        /// <summary>
        /// <c>Mobile.ProcessSteps</c> between two steps: the walk offset toward the front step, <c>OffsetZ</c>, then
        /// <c>OffsetX</c> and <c>OffsetY</c>, the casts' values. Called per frame from the update loops with the instance they
        /// iterate: admission as <see cref="CompleteStep"/> (an instance this World does not hold for its serial is written
        /// as before T1; only an instance of another World is a violation, rejected: nothing changes).
        /// </summary>
        public void InterpolateStep(Mobile mobile, sbyte x, sbyte y, sbyte z)
        {
            if (mobile == null || !Admitted(nameof(InterpolateStep), mobile.Serial))
            {
                return;
            }

            if (mobile.World != _world)
            {
                Reject(Violation.NotHeld, $"WorldWriter.InterpolateStep: 0x{mobile.Serial:X8} belongs to another World", rejected: true);

                return;
            }

            using (Open(mobile, null))
            {
                mobile.OffsetZ = z;
                mobile.OffsetX = x;
                mobile.OffsetY = y;
            }
        }

        /// <summary>
        /// <c>BoatMovingManager.Update</c> between two boat steps: the boat's offset toward the front step, <c>OffsetZ</c>,
        /// then <c>OffsetX</c> and <c>OffsetY</c>. Rejections as <see cref="CompleteBoatStep"/>.
        /// </summary>
        public void InterpolateBoat(Item boat, sbyte x, sbyte y, sbyte z)
        {
            if (boat == null || !Admitted(nameof(InterpolateBoat), boat.Serial) || !Accepted(boat, nameof(InterpolateBoat)))
            {
                return;
            }

            using (Open(boat, null))
            {
                boat.OffsetZ = z;
                boat.OffsetX = x;
                boat.OffsetY = y;
            }
        }

        /// <summary>
        /// <c>BoatMovingManager.Update</c> while a boat step is under way: each component of the boat's multi
        /// (<paramref name="house"/>, the boat's own; its components are not world objects of their own) takes the boat's
        /// offset, in the components' order. Rejections as <see cref="CompleteBoatStep"/>.
        /// </summary>
        public void FollowBoatComponents(Item boat, House house)
        {
            if (boat == null || house == null || !Admitted(nameof(FollowBoatComponents), boat.Serial) || !Accepted(boat, nameof(FollowBoatComponents)))
            {
                return;
            }

            using (Open(boat, null))
            {
                foreach (Multi c in house.Components)
                {
                    c.Offset = boat.Offset;
                }
            }
        }

        /// <summary>
        /// <c>BoatMovingManager.UpdateEntitiesInside</c> while a boat step is under way: an item or mobile on the boat takes
        /// the boat's offset <paramref name="offset"/>. Rejections as <see cref="MoveBoatPassenger"/>.
        /// </summary>
        public void FollowBoat(Entity passenger, Microsoft.Xna.Framework.Vector3 offset)
        {
            if (passenger == null || !Admitted(nameof(FollowBoat), passenger.Serial) || !Accepted(passenger, nameof(FollowBoat)))
            {
                return;
            }

            using (Open(passenger, null))
            {
                passenger.Offset = offset;
            }

            _world.Ecs.PushSteps(passenger); // T6c
        }

        /// <summary>
        /// 0xBF.1B (new spellbook content): finds or creates the spellbook, writes its graphic, empties its child list and
        /// syncs it (as before T1: unfolded, the old Sync), then links one detached entry per known spell (bit i of
        /// <paramref name="spells0"/>/<paramref name="spells1"/>: spell 1 + i / 33 + i; the entries are not world objects, so
        /// they never reach the ECS). Returns the spellbook (null when none could be had, or a nested call: rejected).
        /// </summary>
        public Item FillSpellbook(uint serial, ushort graphic, uint spells0, uint spells1)
        {
            if (!Admitted(nameof(FillSpellbook), serial))
            {
                return null;
            }

            Item spellbook = _world.GetOrCreateItem(serial);

            if (spellbook == null)
            {
                return null;
            }

            using (Open(spellbook, null))
            {
                // T1 part 9: the clear drops every child from the list without touching its Container; a child World holds
                // (an item the server put in the book) is no longer attached, committed first. T9 child-list slice A: over
                // a copy of the ECS contents (each detach changes them); the old spell entries are not world items (the
                // commit skipped them)
                using (ContentsSnapshot children = _world.State.SnapshotContents(spellbook.Serial))
                {
                    foreach (uint child in children.Serials)
                    {
                        if (_world.Items.Get(child) is Item held)
                        {
                            _world.Ecs.CommitDetach(held);
                        }
                    }
                }

                // T2: the spell set the detached entries below stand for (computed from the packet)
                _world.Ecs.CommitSpellbook(spellbook, spells0 | ((ulong)spells1 << 32));

                spellbook.Graphic = graphic;
                spellbook.Clear();
                _world.Ecs.Sync(spellbook);

                for (int j = 0; j < 2; j++)
                {
                    uint spells = j == 0 ? spells0 : spells1;

                    for (int i = 0; i < 32; i++)
                    {
                        if ((spells & (1u << i)) != 0)
                        {
                            ushort cc = (ushort)(j * 32 + i + 1);
                            Item spellItem = Item.Create(_world, cc);
                            spellItem.Serial = cc;
                            spellItem.Graphic = 0x1F2E;
                            spellItem.Amount = cc;
                            spellItem.Container = spellbook;
                            spellbook.PushToBack(spellItem);
                            _spellEntries.Add(spellItem);
                        }
                    }
                }

                // T2: the entries, for the readers of the ECS contents, with the set they stand for
                _world.State.SetSpellEntries(spellbook.Serial, spells0 | ((ulong)spells1 << 32), _spellEntries);
                _spellEntries.Clear();
            }

            return spellbook;
        }

        // a book holds at most 64 spells, so the list never grows past this (WorldState.SpellScratchBytes counts it)
        internal const int SpellScratchCapacity = 64;

        private readonly List<Item> _spellEntries = new List<Item>(SpellScratchCapacity);

        private readonly List<Item> _sorted = new List<Item>();

        /// <summary>
        /// 0x74 (BuyList) for a shop container with graphic 0x2AF8: the client sorts its child list by X (the original
        /// client's hard-coded order; <c>SortContents</c>, a stable merge sort). T1 part 9: the new order is computed first
        /// (stable, by X), each child World holds re-stamped in it (the mirror's child order follows), then the list sorted as
        /// before. Returns the list's new first child (null for an empty list, or a nested call: rejected, nothing sorted).
        /// </summary>
        public LinkedObject SortShopContents(Item container)
        {
            if (container == null || !Admitted(nameof(SortShopContents), container.Serial))
            {
                return null;
            }

            using (Open(container, null))
            {
                // 2. compute: the stable order by X (insertion sort: equal X keep their list order, as the merge sort)
                _sorted.Clear();

                // T9 child-list slice A: the children in the ECS's child order (nothing changes them during the walk)
                foreach (Item it in _world.State.Children(container))
                {
                    int at = _sorted.Count;

                    while (at > 0 && _sorted[at - 1].X > it.X)
                    {
                        at--;
                    }

                    _sorted.Insert(at, it);
                }

                // 3. the ECS, then the links
                foreach (Item it in _sorted)
                {
                    _world.Ecs.CommitChildOrder(it);
                }

                LinkedObject first = container.SortContents<Item>((x, y) => x.X - y.X);
                _sorted.Clear();

                return first;
            }
        }

        // ---- funnel part 7, group 1: the small writers. Name and Price are cache-only (no mirror record field and no
        // MobileInputs reads them); speed mode, war mode and IsDead are inputs (MobileInputRules), so those operations fold the mobile and
        // commit its inputs once (the setter's push before T1). The corpse equipment is an item placement into a container.

        /// <summary>
        /// The name writers: 0x98 (UpdateName), the speech handlers' name of an unnamed speaker (0x1C, 0xAE), 0xC1/0xCC
        /// (DisplayClilocString) for an unnamed speaker, 0xD6 (MegaCliloc) for an item, 0xBF.10's item name and 0x74's shop
        /// entries (BuyList). Cache-only. The handlers keep their reads (the empty-name checks) and run everything else as
        /// before. An entity this World does not hold is a violation, rejected (no pre-T1 path: the handlers pass what they
        /// just looked up), except the detached <c>World.Player</c>, written as before T1.
        /// </summary>
        public void SetName(Entity entity, string name)
        {
            if (entity == null || !Admitted(nameof(SetName), entity.Serial) || !Accepted(entity, nameof(SetName)))
            {
                return;
            }

            using (Open(entity, null))
            {
                _world.Ecs.CommitName(entity, name);
                entity.Name = name;
            }
        }

        /// <summary>
        /// 0x74 (BuyList): a shop entry's price. Cache-only. Rejections as <see cref="SetName"/>.
        /// </summary>
        public void SetPrice(Item item, uint price)
        {
            if (item == null || !Admitted(nameof(SetPrice), item.Serial) || !Accepted(item, nameof(SetPrice)))
            {
                return;
            }

            using (Open(item, null))
            {
                _world.Ecs.CommitPrice(item, price);
                item.Price = price;
            }
        }

        /// <summary>
        /// 0xBF.26 (the player's movement speed mode): an input since the T6c follow-up (<c>MobileInputs.SpeedMode</c>, the
        /// step system's mounted pace with <c>ecs_step_mount_inputs</c>). Folded, the inputs committed once, as
        /// <see cref="SetWarMode"/>; rejections as <see cref="SetName"/>.
        /// </summary>
        public void SetSpeedMode(Mobile mobile, CharacterSpeedType mode)
        {
            if (mobile == null || !Admitted(nameof(SetSpeedMode), mobile.Serial) || !Accepted(mobile, nameof(SetSpeedMode)))
            {
                return;
            }

            bool detached = !Holds(mobile);

            using (Open(mobile, detached ? null : mobile))
            {
                mobile.SpeedMode = mode;
                _world.Ecs.CommitMobileInputs(mobile);
            }
        }

        /// <summary>
        /// 0x72 (Warmode): the player's war mode, an input (<c>PlayerMobile.InWarMode</c>; for any other mobile the setter is
        /// empty and war mode is a flag). The mobile is folded and its inputs are committed once (the setter's push before
        /// T1). Rejections as <see cref="SetName"/>; the detached <c>World.Player</c> is written unfolded (nothing of it
        /// reaches the ECS).
        /// </summary>
        public void SetWarMode(Mobile mobile, bool warMode)
        {
            if (mobile == null || !Admitted(nameof(SetWarMode), mobile.Serial) || !Accepted(mobile, nameof(SetWarMode)))
            {
                return;
            }

            bool detached = !Holds(mobile);

            using (Open(mobile, detached ? null : mobile))
            {
                mobile.InWarMode = warMode;
                _world.Ecs.CommitMobileInputs(mobile);
            }
        }

        /// <summary>
        /// 0xBF.19 version 0 (a bonded pet's dead state): <c>Mobile.IsDead</c>, an input. Folded, the inputs committed once.
        /// Rejections as <see cref="SetWarMode"/>.
        /// </summary>
        public void SetDead(Mobile mobile, bool dead)
        {
            if (mobile == null || !Admitted(nameof(SetDead), mobile.Serial) || !Accepted(mobile, nameof(SetDead)))
            {
                return;
            }

            bool detached = !Holds(mobile);

            using (Open(mobile, detached ? null : mobile))
            {
                mobile.IsDead = dead;
                _world.Ecs.CommitMobileInputs(mobile);
            }
        }

        /// <summary>
        /// 0x89 (CorpseEquipment), one entry that is not the backpack: the item (found or created among the items) leaves its
        /// old container (the old path published that detached state: Container 0xFFFFFFFF, its other values unchanged), then
        /// gets the corpse as its Container and the entry's layer, goes to the end of the corpse's child list and is published
        /// (the old final Sync). No graphic change and no <c>CheckGraphicChange</c> (the old handler ran none). Cancels the
        /// item's outstanding placements (it leaves its tile).
        /// </summary>
        /// <param name="corpse">The handler's <c>World.Get(serial)</c>, captured once before the list and passed to every
        /// entry, as the old loop used it. Null: nothing happens and null is returned. An instance this World does not hold in
        /// its table is a violation, rejected (null) before any side effect (no pre-T1 path).</param>
        public Item PlaceCorpseEquipment(Entity corpse, uint serial, Layer layer)
        {
            if (!Admitted(nameof(PlaceCorpseEquipment), serial) || corpse == null)
            {
                return null;
            }

            if (!Holds(corpse))
            {
                Reject(Violation.NotHeld, $"WorldWriter.PlaceCorpseEquipment: corpse 0x{corpse.Serial:X8} is not the instance this World holds for its serial", rejected: true);

                return null;
            }

            uint corpseSerial = corpse.Serial;
            Item item = _world.GetOrCreateItem(serial);

            // compute: every read of the previous state is here
            uint oldContainer = item.Container;

            ItemState detached;
            detached.Serial = item.Serial;
            detached.IsMulti = item.IsMulti;
            detached.Graphic = item.Graphic;
            detached.X = item.X;
            detached.Y = item.Y;
            detached.Z = item.Z;
            detached.Hue = item.Hue;
            detached.Amount = item.Amount;
            detached.Flags = item.Flags;
            detached.Container = 0xFFFF_FFFF;
            detached.Direction = item.Direction;
            detached.Layer = item.Layer;

            ItemState v = detached;
            v.Container = corpseSerial;
            v.Layer = layer;

            CancelPlacements(item);

            // not folded: no write here has a hook Sync (the Container and Layer setters publish nothing, nor does the unlink),
            // so the two commits are the only ECS writes, as the old two Syncs were
            using (Open(item, null))
            {
                // the old order: the unlink (gump refresh, list links, tile), the detached state's Sync, then the final one
                _world.UnlinkFromContainer(item, oldContainer);
                _world.Ecs.CommitItem(item, in detached);
                _world.Ecs.CommitItem(item, in v, attach: true);

                item.Container = v.Container;
                item.Layer = v.Layer;
                corpse.PushToBack(item);
            }

            return item;
        }

        // ---- funnel part 7, group 2: the death re-key (the plan's Rekey)

        /// <summary>
        /// 0xAF (DisplayDeath): the dying mobile moves to <c>serial | 0x80000000</c> (its corpse animation runs under that key
        /// while the server's corpse item takes the old serial's place). Before T1: out of the mobiles table under its serial,
        /// each child's Container to the new serial, into the table under the new serial (replacing whatever was there, as
        /// before: an earlier death's re-keyed mobile is not removed), the new Serial, then
        /// <see cref="WorldEcs.OnSerialChanged"/> (the old entity removed, the new one synced with the mirror bit carried over,
        /// then each child synced). Since funnel part 9 the ECS commit takes its values and runs first, as every other
        /// operation's: <see cref="WorldEcs.CommitRekey"/> (the old record removed, the record under the new serial from the
        /// computed <see cref="MobileState"/>, the mirror bit carried over), then each child's <see cref="WorldEcs.CommitItem"/>
        /// with the new Container; then the cache writes in the old order (out of the table, each child's Container, into the
        /// table, the Serial); then the derived inputs and clock from the getters
        /// (<see cref="WorldEcs.CommitMobileInputs(Mobile, EcsEntity)"/>, as every mobile operation). The mirror ends as
        /// <c>OnSerialChanged</c> left it, and nothing reads the serial or a Container back from the cache (T4 needs this). In
        /// a scope that owns the mobile and its subtree, not folded.
        /// </summary>
        /// <param name="mobile">The handler's <c>World.Mobiles.Get(serial)</c> (never the player: the handler returns first).
        /// One this World does not hold for its serial is a violation, rejected (0, nothing changes; no pre-T1 path: the
        /// handler passes what it just looked up, so the old <c>Mobiles.Remove</c> always succeeded).</param>
        /// <returns>The new serial, or 0 when rejected.</returns>
        public uint RekeyDeadMobile(Mobile mobile)
        {
            if (mobile == null || !Admitted(nameof(RekeyDeadMobile), mobile.Serial))
            {
                return 0;
            }

            if (!Holds(mobile))
            {
                Reject(Violation.NotHeld, $"WorldWriter.RekeyDeadMobile: 0x{mobile.Serial:X8} is not the mobile this World holds for its serial", rejected: true);

                return 0;
            }

            // compute
            uint oldSerial = mobile.Serial;
            uint serial = oldSerial | 0x8000_0000;

            using (Open(mobile, null))
            {
#if CUO_DEVTOOLS
                if (FunnelCheck.Enabled)
                {
                    CollectSubtree(mobile);
                }
#endif

                // the ECS from the values: the record under the new serial, then each child under its new parent
                MobileState v;
                v.Serial = serial;
                v.Graphic = mobile.Graphic;
                v.Hue = mobile.Hue;
                v.X = mobile.X;
                v.Y = mobile.Y;
                v.Z = mobile.Z;
                v.Direction = mobile.Direction;
                v.Flags = mobile.Flags;
                v.IsPlayer = false;

                // Codex audit P2 (#2): an earlier death's re-keyed mobile still under the new serial is displaced (the table
                // entry is replaced); its children keep their Container but leave the contents World reads under that
                // serial (the new mobile's list), so they are detached before the incoming list is attached
                // T9 child-list slice A: the dying mobile's children from the ECS, copied before anything changes (the
                // re-key and the re-attach move them to the new serial)
                using ContentsSnapshot children = _world.State.SnapshotContents(oldSerial);
                Mobile displaced = _world.Mobiles.Get(serial);

                if (displaced != null && !ReferenceEquals(displaced, mobile))
                {
                    using (ContentsSnapshot gone = _world.State.SnapshotContents(serial))
                    {
                        foreach (uint s in gone.Serials)
                        {
                            if (_world.Items.Get(s) is Item it)
                            {
                                _world.Ecs.CommitDetach(it);
                            }
                        }
                    }
                }

                EcsEntity e = _world.Ecs.CommitRekey(mobile, oldSerial, in v);

                // (the children keep their order under the new parent: each is re-stamped in it, attached)
                foreach (uint s in children.Serials)
                {
                    if (_world.Items.Get(s) is Item child)
                    {
                        ItemState c = CurrentState(child);
                        c.Container = serial;
                        _world.Ecs.CommitItem(child, in c, attach: true);
                    }
                }

                // then the cache, in the old order
                _world.Mobiles.Remove(oldSerial);

                foreach (uint s in children.Serials)
                {
                    if (_world.Items.Get(s) is Item child)
                    {
                        child.Container = serial;
                    }
                }

                _world.Mobiles[serial] = mobile;
                mobile.Serial = serial;

                // the derived inputs and clock, from the getters (what OnSerialChanged's Sync pushed)
                _world.Ecs.CommitMobileInputs(mobile, e);
            }

            return serial;
        }

        /// <summary>An item's current values: the starting point of an operation that changes only some of them.</summary>
        private static ItemState CurrentState(Item item)
        {
            ItemState v;
            v.Serial = item.Serial;
            v.Graphic = item.Graphic;
            v.Hue = item.Hue;
            v.X = item.X;
            v.Y = item.Y;
            v.Z = item.Z;
            v.Direction = item.Direction;
            v.Flags = item.Flags;
            v.Amount = item.Amount;
            v.Container = item.Container;
            v.Layer = item.Layer;
            v.IsMulti = item.IsMulti;

            return v;
        }

        // ---- funnel part 7, group 3: the per-frame animation (Mobile.Update after the ProcessAnimation split)

        /// <summary>
        /// The per-frame frame advance of a mobile (<c>Mobile.Update</c> → <c>AdvanceFrame</c>, once its frame is due): with
        /// <paramref name="frameCount"/> &gt; 0 (the frames of its current animation group and direction, read by the caller
        /// from the animation data) the timer steps (<c>AnimationTiming.Advance</c>), its frame count and repeat state are
        /// written, a finished animation is reset (<c>SetAnimation(0xFF)</c>, which also draws the next idle delay) and the
        /// frame index is written; then, in every case, the next frame's due time (<c>LastAnimationChangeTime</c>). The old
        /// order. None of it is a record field or an input; the due times are the clock, which only <c>SetAnimation</c> pushes
        /// here (the other setters push nothing): the update loops publish the rest after <c>Mobile.Update</c>
        /// (<c>WorldEcs.PushClock</c> on the E4 path), as before T1. So the operation is not folded and commits nothing: a
        /// fold would drop SetAnimation's push and a commit would add one the old path did not make.
        /// <para>Returns true when the mobile's corpse animation is over (a death-rekeyed serial whose animation wrapped, or
        /// that has no frames). Then this operation writes only the timer fields, and the caller passes
        /// <paramref name="frame"/> and <paramref name="due"/> to <see cref="RemoveDeadMobile"/>, which removes the mobile and
        /// then writes them, as before T1 (the removal ran between the timer fields and the frame index; <c>Destroy</c> resets
        /// the due time, and the old code wrote the frame and the due time to the removed mobile after it).</para>
        /// <para>Called per frame from the update loops (the E4 clock path, its skipped-mobile check), which
        /// iterate the mobiles table; an instance this World does not hold for its serial is written as before T1 (every push
        /// ignores it), like <see cref="CompleteStep"/>. Only an instance of another World is a
        /// violation, rejected (nothing changes).</para>
        /// </summary>
        public bool AdvanceAnimation(Mobile mobile, int frameCount, out int frame, out long due)
        {
            frame = -1;
            due = 0;

            if (mobile == null || !Admitted(nameof(AdvanceAnimation), mobile.Serial))
            {
                return false;
            }

            if (mobile.World != _world)
            {
                Reject(Violation.NotHeld, $"WorldWriter.AdvanceAnimation: 0x{mobile.Serial:X8} belongs to another World", rejected: true);

                return false;
            }

            bool dead = (mobile.Serial & 0x8000_0000) != 0;

            // compute: the timer step from the current state, before any write
            int delay = Constants.CHARACTER_ANIMATION_DELAY;
            ClassicUO.Ecs.AnimationTimer timer = default;
            AnimationAdvance advance = default;
            bool remove = dead;

            if (frameCount > 0)
            {
                timer = mobile.AnimationTimerState;
                delay = AnimationTiming.Advance(ref timer, frameCount, out advance);
                remove = dead && advance == AnimationAdvance.Wrapped;
                frame = timer.Frame;
            }

            due = Time.Ticks + delay;

            using (Open(mobile, null))
            {
                if (frameCount > 0)
                {
                    mobile.ApplyAnimationTimer(in timer);

                    if (advance == AnimationAdvance.Finished)
                    {
                        mobile.SetAnimation(0xFF);
                    }
                }

                if (!remove)
                {
                    if (frame >= 0)
                    {
                        mobile.AnimIndex = (byte)frame;
                    }

                    mobile.LastAnimationChangeTime = due;
                }
            }

            return remove;
        }

        /// <summary>
        /// The end of a corpse animation (<c>Mobile.Update</c>, when <see cref="AdvanceAnimation"/> returned true): the
        /// re-keyed mobile leaves the corpse manager (<c>CorpseManager.Remove(0, serial)</c>) and is removed with everything it
        /// wears or holds and destroyed, staying in the mobiles table until the frame's destroyed list (<c>World.RemoveMobile</c>
        /// without force, as before T1). A removal like <see cref="SweepMobile"/>: it owns the removed subtree, writes no value
        /// of its own, does not fold, and first cancels the outstanding placements of the mobile and of everything removed. A
        /// destroyed mobile is a no-op. One this World does not hold for its serial is a violation, rejected (nothing is
        /// removed; no pre-T1 path: the update loops pass what they iterate). After the removal, the frame index (when
        /// <paramref name="frame"/> &gt;= 0) and the due time <see cref="AdvanceAnimation"/> computed are written to the removed
        /// mobile, as the old code did.
        /// </summary>
        public void RemoveDeadMobile(Mobile mobile, int frame, long due)
        {
            if (mobile == null || !Admitted(nameof(RemoveDeadMobile), mobile.Serial))
            {
                return;
            }

            if (!Holds(mobile))
            {
                Reject(Violation.NotHeld, $"WorldWriter.RemoveDeadMobile: 0x{mobile.Serial:X8} is not the mobile this World holds for its serial", rejected: true);

                return;
            }

            if (mobile.IsDestroyed)
            {
                return;
            }

            uint serial = mobile.Serial;
            CancelSubtreePlacements(mobile);

            using (Open(mobile, null))
            {
#if CUO_DEVTOOLS
                if (FunnelCheck.Enabled)
                {
                    CollectSubtree(mobile);
                }
#endif

                _world.CorpseManager.Remove(0, serial);
                _world.RemoveMobile(serial);

                if (frame >= 0)
                {
                    mobile.AnimIndex = (byte)frame;
                }

                mobile.LastAnimationChangeTime = due;
            }
        }

        /// <summary>
        /// T5b (<c>world_anim_system</c>): what <c>MobileAnimationAdvanceSystem</c> computed in the ECS for a due mobile without
        /// steps (the idle animation and the frame advance over its <c>AnimationTimer</c>) written to the Mobile's fields, the
        /// cache the views and the step path read (<c>Mobile.WriteAnimationTimer</c>); with <paramref name="remove"/> the frame
        /// index and the due time are left out and the dying mobile is removed (<see cref="RemoveDeadMobile"/>, which writes
        /// <paramref name="frame"/> and <paramref name="due"/> after the removal), as <see cref="AdvanceAnimation"/>'s caller
        /// does. Unfolded and committing nothing: the update loop pushes the clock (and with it the component) after it, as it
        /// does after <c>Mobile.Update</c>. Admission as <see cref="IdleAnimation"/>.
        /// </summary>
        public void AdvanceAnimationFromEcs(Mobile mobile, in ClassicUO.Ecs.AnimationTimer timer, bool remove, int frame, long due)
        {
            if (!PerFrameAdmitted(mobile, nameof(AdvanceAnimationFromEcs)))
            {
                return;
            }

            using (Open(mobile, null))
            {
                mobile.WriteAnimationTimer(in timer, !remove);
            }

            if (remove)
            {
                RemoveDeadMobile(mobile, frame, due);
            }
        }

        // ---- funnel part 8, group 1: the idle animation and the footstep sound (Mobile.Update, per mobile when due). Like
        // AdvanceAnimation: the update loops pass the instance they iterate, so a stale instance of this World is written as
        // before T1 (every push ignores it) and only another World's is a violation, rejected (nothing changes). Unfolded and
        // committing nothing: the setters push what they pushed before T1 (the idle timer's clock push; SetAnimation's), and
        // the update loops publish the rest after Update.

        /// <summary>
        /// <c>Mobile.SetIdleAnimation</c> (its idle timer is due): the plan <c>Mobile.PlanIdleAnimation</c> computed from the
        /// mobile and the animation data (the random draws included), written in the old order by <c>Mobile.ApplyIdle</c>:
        /// the idle timer's next due time and its clock push; unless mounted, in war mode or not animating, the timer fields'
        /// idle reset (frame 0, no frame count, interval 1, repeat mode 1, forward, no repeat, from the server); then the
        /// group, or <c>SetAnimation</c> of the group it had when neither idle action exists.
        /// </summary>
        public void IdleAnimation(Mobile mobile, in Mobile.IdlePlan plan)
        {
            if (!PerFrameAdmitted(mobile, nameof(IdleAnimation)))
            {
                return;
            }

            using (Open(mobile, null))
            {
                mobile.ApplyIdle(in plan);
            }
        }

        /// <summary>
        /// <c>Mobile.ProcessFootstepsSound</c> (a walking human's step sound is due): the next sound offset, then the next
        /// sound's due time, the old writes in their old order. The caller plays the sound after this returns true (it read
        /// neither field; before T1 it played between the two writes). False: rejected, nothing changed, no sound.
        /// </summary>
        public bool StepSound(Mobile mobile, int offset, long due)
        {
            if (!PerFrameAdmitted(mobile, nameof(StepSound)))
            {
                return false;
            }

            using (Open(mobile, null))
            {
                mobile.StepSoundOffset = offset;
                mobile.LastStepSoundTime = due;
            }

            return true;
        }

        // ---- funnel part 8, group 2: a corpse's frame advance (Item.ProcessAnimation, per item when due)

        /// <summary>
        /// <c>Item.ProcessAnimation</c> for a corpse whose frame is due: the frame the caller read from the animation data
        /// (<c>TryCorpseFrame</c>; when <paramref name="frame"/> &lt; 0 there was none and the frame index stays), then the
        /// next frame's due time, the old writes in their old order. Neither is a record field or an input (the ECS item
        /// extract reads them each frame), so nothing is committed or pushed, as before T1. Admission as
        /// <see cref="IdleAnimation"/> (the item update loop passes what it iterates).
        /// </summary>
        public void AdvanceCorpseFrame(Item corpse, int frame, long due, bool push = true)
        {
            if (!PerFrameAdmitted(corpse, nameof(AdvanceCorpseFrame)))
            {
                return;
            }

            using (Open(corpse, null))
            {
                if (frame >= 0)
                {
                    corpse.AnimIndex = (byte)frame;
                }

                corpse.LastAnimationChangeTime = due;
            }

            // T5b-items: the timer component follows (the system's own advance already wrote it)
            if (push)
            {
                _world.Ecs.PushItemTimer(corpse);
            }
        }

        // ---- funnel part 8, group 3: a server animation that a step ends (Mobile.ProcessSteps)

        /// <summary>
        /// <c>Mobile.ProcessSteps</c> while the mobile has a step queued and its current animation came from the server:
        /// <c>SetAnimation(0xFF)</c> (the default action, the timer reset, the idle delay drawn and its clock push), as before
        /// T1. Unfolded and committing nothing: SetAnimation's own push is the old one. Admission as <see cref="IdleAnimation"/>
        /// (the update loops pass what they iterate).
        /// </summary>
        public void EndServerAnimation(Mobile mobile)
        {
            if (!PerFrameAdmitted(mobile, nameof(EndServerAnimation)))
            {
                return;
            }

            using (Open(mobile, null))
            {
                mobile.SetAnimation(0xFF);
            }
        }

        // ---- funnel part 8, group 4: the player's walk (PlayerMobile.Walk)

        /// <summary>
        /// <c>PlayerMobile.Walk</c>'s step request: first the admission (<see cref="AdmittedPlayerWalk"/>) and the player
        /// check, then <c>CloseBank</c> (the caller's removals, unchanged and unscoped as before T1), then the walk queue's
        /// writes in their old order (<c>PlanWalkStep</c> from the queue as it is now, then <c>Mobile.ApplyStep</c>: when the
        /// walk starts, <c>SetAnimation(0xFF)</c> if the player was not walking and <c>LastStepTime</c>; the step; the
        /// inputs). Unfolded, as every player path. Only <c>World.Player</c> of this World walks this way; anything else is a
        /// violation, rejected (false: nothing changed, the bank stays open, and Walk returns false before the walker records
        /// or sends the step).
        /// </summary>
        public bool QueuePlayerStep(Mobile player, int x, int y, sbyte z, Direction direction, bool run)
        {
            if (player == null || !AdmittedPlayerWalk(player, nameof(QueuePlayerStep), out bool joined))
            {
                return false;
            }

            if (player.World != _world || !ReferenceEquals(player, _world.Player))
            {
                Reject(Violation.NotHeld, $"WorldWriter.QueuePlayerStep: 0x{player.Serial:X8} is not this World's player", rejected: true);

                return false;
            }

            PlayerMobile walker = _world.Player;
            walker.CloseBank();
            walker.PlanWalkStep(x, y, z, direction, run, out Mobile.StepPlan plan);

            using (joined ? default : Open(player, null))
            {
                player.ApplyStep(in plan);
            }

            return true;
        }

        /// <summary>
        /// <c>PlayerMobile.Walk</c>'s coalesced turn (three unconfirmed requests: the direction alone, no step): as
        /// <see cref="SetMobileDirection"/> (the record committed with the new direction, then the setter unfolded for the door
        /// check, whose Sync writes the inputs: the old turn's one push), with the walk's admission (<see cref="AdmittedPlayerWalk"/>). Only <c>World.Player</c> of this
        /// World; anything else is a violation, rejected. False: rejected, nothing changed (Walk then returns false and keeps
        /// its cooldown).
        /// </summary>
        public bool TurnPlayer(Mobile player, Direction direction)
        {
            if (player == null || !AdmittedPlayerWalk(player, nameof(TurnPlayer), out bool joined))
            {
                return false;
            }

            if (player.World != _world || !ReferenceEquals(player, _world.Player))
            {
                Reject(Violation.NotHeld, $"WorldWriter.TurnPlayer: 0x{player.Serial:X8} is not this World's player", rejected: true);

                return false;
            }

            MobileState v;
            v.Serial = player.Serial;
            v.Graphic = player.Graphic;
            v.Hue = player.Hue;
            v.X = player.X;
            v.Y = player.Y;
            v.Z = player.Z;
            v.Direction = direction;
            v.Flags = player.Flags;
            v.IsPlayer = true;

            // the setter's Sync (unfolded) writes the inputs from the new direction, the one push the old turn made; no
            // second inputs commit (unlike SetMobileDirection, whose 0x1B/0x21 callers it keeps as they were)
            using (joined ? default : Open(player, null))
            {
                _world.Ecs.CommitMobile(player, in v);
                player.Direction = v.Direction;
            }

            return true;
        }

        /// <summary>
        /// The admission of the player's walk (<see cref="QueuePlayerStep"/>, <see cref="TurnPlayer"/>), Codex audit of part 8,
        /// P2 #1. With nothing open it is <see cref="Admitted"/>. Inside an operation it is the one documented exception to
        /// "operations do not nest": the player's position and direction callbacks run inside the unfolded operations on the
        /// player (<see cref="PlaceMobile"/>, <see cref="MoveMobile"/>, <see cref="CompleteStep"/>,
        /// <see cref="MoveBoatPassenger"/>, <see cref="SetMobileDirection"/>, <see cref="TurnPlayer"/>), and a plugin's
        /// <c>RequestMove</c> there runs <c>Player.Walk</c> synchronously (<c>PlayerMobile.OnPositionChanged</c> →
        /// <c>Plugin.UpdatePlayerPosition</c>; the door check's packet → the plugins' send hooks). Before T1 that walk wrote at
        /// that point; it still does: the walk joins the open operation (no scope of its own, so the outer scope, its
        /// ownership and its deferred work stay as they are) when that operation's subject is this exact instance (and the
        /// caller then checks it is <c>World.Player</c> of this World) and nothing is folded (the operations that fold the player run no callback).
        /// Its writes are then the outer operation's funnel writes, and every push and Sync publishes as before T1; what the
        /// outer operation writes after its callback is what the old code wrote after it (its commits after the callback are
        /// the inputs from the getters and the hook Syncs from the cache, never values computed before the callback). Any
        /// other nesting is rejected as by <see cref="Admitted"/>.
        /// </summary>
        private bool AdmittedPlayerWalk(Mobile player, string operation, out bool joined)
        {
            // the open operation's own subject, nothing folded. ClearWorld's scope has no subject, so it never matches; a
            // removal's subject is the removed mobile, and no removal runs a position callback. The caller's player check,
            // next and still before any side effect, rejects a subject that is not this World's player
            joined = _open && !_world.Ecs.IsFolding && ReferenceEquals(_subject, player);

            return joined || Admitted(operation, player.Serial);
        }

        // ---- funnel part 8, group 5: logout's removals (World.Clear)

        /// <summary>
        /// <c>World.Clear</c> (the game scene's unload: logout, disconnect): every mobile, then every item, removed and
        /// destroyed (<c>World.RemoveMobile</c>/<c>RemoveItem</c> without force, which leave them in their tables; Clear
        /// empties the tables afterwards), in the tables' order, as before T1. A removal like <see cref="SweepMobile"/>: no
        /// value of its own, no fold, and its scope owns every object of this World (the funnel check sees no direct write
        /// from the removals). Every outstanding placement is cancelled first (their subjects are all removed). A nested
        /// call is rejected (nothing is removed; <c>World.Clear</c> then empties the tables without destroying the objects,
        /// and the mirror is reset by <c>WorldEcs.OnWorldCleared</c>); nothing runs <c>World.Clear</c> inside an operation.
        /// </summary>
        public void ClearWorld()
        {
            if (!Admitted(nameof(ClearWorld), 0))
            {
                return;
            }

            _placements.Clear();

            using (Open(null, null))
            {
                _clearingWorld = true;

                foreach (Mobile mobile in _world.Mobiles.Values)
                {
                    _world.RemoveMobile(mobile.Serial);
                }

                foreach (Item item in _world.Items.Values)
                {
                    _world.RemoveItem(item.Serial);
                }
            }
        }

        // ---- funnel part 9, group 2: the removal sites outside the converted callers (part 8's gate traced its only direct
        // writes, Item.Destroy's, to World.RemoveItem from callers its two-level trace cut off)

        /// <summary>
        /// <c>PlayerMobile.CloseBank</c>'s removals (0x20 for the player, and the player's walk before its step): every item
        /// in the open bank removed with force (<c>World.RemoveItem(item, true)</c>, contents first, as before T1), in the
        /// bank's child order, in a scope that owns the bank and its subtree, not folded (a removal: no value of its own). The
        /// caller then empties the bank's list, closes its gump and its Opened flag, as before. Admission as the walk's
        /// (<see cref="AdmittedPlayerWalk"/>: <c>QueuePlayerStep</c> runs it, and a walk from the player's position or
        /// direction callback joins the open operation on the player; the joined removal opens no scope and its subtree joins
        /// the outer scope's), then the player check: only <c>World.Player</c> of this World; anything else is a violation,
        /// rejected (false: nothing is removed and the caller leaves the bank open). The removed items' placements are
        /// cancelled first.
        /// </summary>
        public bool RemoveBankContents(PlayerMobile player, Item bank)
        {
            if (player == null || bank == null || !AdmittedPlayerWalk(player, nameof(RemoveBankContents), out bool joined))
            {
                return false;
            }

            if (player.World != _world || !ReferenceEquals(player, _world.Player))
            {
                Reject(Violation.NotHeld, $"WorldWriter.RemoveBankContents: 0x{player.Serial:X8} is not this World's player", rejected: true);

                return false;
            }

            CancelSubtreePlacements(bank);

            using (joined ? default : Open(bank, null))
            {
#if CUO_DEVTOOLS
                if (FunnelCheck.Enabled)
                {
                    CollectSubtree(bank);
                }
#endif

                // T9 child-list slice A: over a copy of the ECS contents, in child order (each removal changes them)
                using (ContentsSnapshot children = _world.State.SnapshotContents(bank.Serial))
                {
                    foreach (uint serial in children.Serials)
                    {
                        _world.RemoveItem(serial, true);
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// <c>World.Update</c>'s removal of the item the player picked up (<c>GameActions.PickUp</c> sets
        /// <c>World.ObjectToRemove</c>; the next frame removes it with force, contents first). The old removal in a scope
        /// that owns the item and its subtree, not folded; its placements are cancelled first. Only the item this World
        /// holds for its serial (the caller's <c>Items.Get</c>); anything else is a violation, rejected (nothing removed). A
        /// destroyed held item is left as <c>World.RemoveItem</c> left it (nothing happens).
        /// </summary>
        public void RemovePickedUpItem(Item item)
        {
            if (item == null || !Admitted(nameof(RemovePickedUpItem), item.Serial))
            {
                return;
            }

            if (!Holds(item))
            {
                Reject(Violation.NotHeld, $"WorldWriter.RemovePickedUpItem: 0x{item.Serial:X8} is not the item this World holds for its serial", rejected: true);

                return;
            }

            CancelSubtreePlacements(item);

            using (Open(item, null))
            {
#if CUO_DEVTOOLS
                if (FunnelCheck.Enabled)
                {
                    CollectSubtree(item);
                }
#endif

                _world.RemoveItem(item, true);
            }
        }

        /// <summary>
        /// <c>World.InternalMapChangeClear</c> (a map change: <c>World.MapIndex</c>'s setter, 0xBF.08, the map patches'
        /// reload, EnterWorld without a map): the old body (<see cref="World.MapChangeRemovals"/>: without a kept player the
        /// map and the player destroyed; every item but the player's (houses dropped from the house manager), then every
        /// mobile but the player, removed with force) in a scope that owns every object of this World, as
        /// <see cref="ClearWorld"/>'s. The placements of what it destroyed are cancelled afterwards. A nested call is
        /// rejected (nothing removed); nothing changes the map inside an operation.
        /// </summary>
        public void ClearForMapChange(bool keepPlayer)
        {
            if (!Admitted(nameof(ClearForMapChange), 0))
            {
                return;
            }

            using (Open(null, null))
            {
                _clearingWorld = true;
                _world.MapChangeRemovals(keepPlayer);
            }

            for (int i = _placements.Count - 1; i >= 0; i--)
            {
                if (_placements[i].Subject.IsDestroyed)
                {
                    _placements.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// <c>LoginScene</c>'s reset of the World between login steps: every item, then every mobile, removed without force
        /// (the scene then empties the tables), in the tables' order, as before T1; every placement cancelled first; a scope
        /// that owns every object of this World. A nested call is rejected (nothing removed; the scene still empties the tables).
        /// </summary>
        public void ClearForLogin()
        {
            if (!Admitted(nameof(ClearForLogin), 0))
            {
                return;
            }

            _placements.Clear();

            using (Open(null, null))
            {
                _clearingWorld = true;

                foreach (Item item in _world.Items.Values)
                {
                    _world.RemoveItem(item);
                }

                foreach (Mobile mobile in _world.Mobiles.Values)
                {
                    _world.RemoveMobile(mobile);
                }
            }
        }

        // ---- funnel part 8, group 6: the gump-side writes (survey: ContainerGump.CheckItemControlPosition is the only gump
        // or UI write to an object World holds. CreateCharAppearanceGump and RaceChangeGump write their own preview mobiles
        // and clothes; until the Codex audit of part 8 (P2 #2) character creation put its mobile in World.Mobiles and both
        // made their clothes with World.GetOrCreateItem; they are now detached: Item.Create, no table, and the character
        // creation paperdoll draws its preview instance)

        /// <summary>
        /// <c>ContainerGump.CheckItemControlPosition</c> (0x25's <c>AddItemToContainer</c>, after the item is placed in an
        /// open container gump): the item's position in the gump clamped to the gump's bounds. The gump computes the clamped
        /// <paramref name="x"/> and <paramref name="y"/> (its bounds and the item's art); this writes X, then Y, each only when
        /// it changes (the old code wrote a coordinate only when it was out of bounds, and the clamp always changes it), then
        /// syncs the item's record (the old explicit <c>Sync</c>), unfolded. Admission as <see cref="IdleAnimation"/>: a stale
        /// instance of this World is written as before T1 (the Sync ignores it), another World's is rejected.
        /// </summary>
        public void ClampContainerPosition(Item item, ushort x, ushort y)
        {
            if (!PerFrameAdmitted(item, nameof(ClampContainerPosition)))
            {
                return;
            }

            using (Open(item, null))
            {
                if (item.X != x)
                {
                    item.X = x;
                }

                if (item.Y != y)
                {
                    item.Y = y;
                }

                _world.Ecs.Sync(item);
            }
        }

        // the admission of the per-frame operations of part 8 (as InterpolateStep's): no operation or fold open, and an
        // entity of this World (a stale instance included; only another World's is a violation, rejected)
        private bool PerFrameAdmitted(Entity entity, string operation)
        {
            if (entity == null || !Admitted(operation, entity.Serial))
            {
                return false;
            }

            if (entity.World != _world)
            {
                Reject(Violation.NotHeld, $"WorldWriter.{operation}: 0x{entity.Serial:X8} belongs to another World", rejected: true);

                return false;
            }

            return true;
        }

        // the held check of the status operations: the instance this World holds for the entity's serial, or the detached
        // World.Player (removed under its serial while World.Player stays; written as before T1: nothing of it reaches the
        // ECS). Anything else is a violation, rejected (no pre-T1 path: the handlers pass what they just looked up).
        private bool Accepted(Entity entity, string operation)
        {
            if (Holds(entity) || entity is Mobile m && IsDetachedPlayer(m))
            {
                return true;
            }

            Reject(Violation.NotHeld, $"WorldWriter.{operation}: 0x{entity.Serial:X8} is not the entity this World holds for its serial", rejected: true);

            return false;
        }

        // Codex audit of part 4, #3: a 0x25 under the player's serial removes the player's mobile while World.Player stays,
        // and 0x78/0x20 in the same burst pass World.Player itself. Before T1 they wrote that instance (and ran its position
        // callbacks); the player's body and move operations keep doing so for this exact instance, and reject every other
        // instance World does not hold.
        private bool IsDetachedPlayer(Mobile mobile) =>
            mobile.World == _world && ReferenceEquals(mobile, _world.Player) && !Holds(mobile);

        // the instance this World holds for the entity's serial in the entity's own table (a mobile among the mobiles, an
        // item among the items), so a mobile and an item sharing a serial are each recognised
        private bool Holds(Entity entity)
        {
            if (entity.World != _world)
            {
                return false;
            }

            return entity is Mobile mobile
                ? ReferenceEquals(_world.Mobiles.Get(mobile.Serial), mobile)
                : entity is Item item && ReferenceEquals(_world.Items.Get(item.Serial), item);
        }

        // the destroy half of PlaceInContainer's destroy-vs-reuse, its own scope (owning the removed subtree)
        private void RemoveForReplacement(Item item)
        {
            CancelSubtreePlacements(item);

            using (Open(item, null))
            {
#if CUO_DEVTOOLS
                if (FunnelCheck.Enabled)
                {
                    CollectSubtree(item);
                }
#endif

                _world.RemoveItem(item.Serial, true);
            }
        }

        private Placement Issue(Entity item)
        {
            if (_placements.Count == MaxOutstanding)
            {
                _placements.RemoveAt(0); // the oldest was abandoned (its handler never placed it)
            }

            if (++_placementId == 0)
            {
                _placementId = 1; // 0 is a default token's
            }

            var p = new Placement(item, _placementId);
            _placements.Add(p);

            return p;
        }

        private bool Consume(in Placement placement)
        {
            for (int i = _placements.Count - 1; i >= 0; i--)
            {
                if (_placements[i].Id == placement.Id && ReferenceEquals(_placements[i].Subject, placement.Subject))
                {
                    _placements.RemoveAt(i);

                    return true;
                }
            }

            return false;
        }

        private void CancelPlacements(Entity item)
        {
            for (int i = _placements.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_placements[i].Subject, item))
                {
                    _placements.RemoveAt(i);
                }
            }
        }

        // a removal's: the placements of the removed item and of everything it contains, at any depth (Codex audit of
        // funnel-2, #3: a removed descendant's late placement is rejected as cancelled, not consumed and dropped). Runs before
        // the removal, while the child lists are intact; nothing to walk when no placement is outstanding (the usual case)
        private void CancelSubtreePlacements(Entity root)
        {
            if (_placements.Count == 0)
            {
                return;
            }

            CancelPlacements(root);

            // T9 child-list slice A: the ECS's children (read only: nothing changes during the walk)
            foreach (Item child in _world.State.Children(root))
            {
                CancelSubtreePlacements(child);
            }
        }

        // ---- the pre-T1 paths: a release build's fallback after a placement or removal violation (the old handler code, the
        // hook syncs). Both run with no operation or fold open (a nesting is rejected, not given a fallback). The write-site
        // budget exempts them by name like the operations (coordinator, T1 round 3); its tests pin that each call follows an
        // unconditional Reject in the violation branch of an operation, so they cannot become a general bypass.

        // the placement of UpdateGameObject's item branch before T1, and of its mobile branch (funnel part 4)
        private void PlacePreT1(Entity entity)
        {
            if (entity is Mobile mobile)
            {
                mobile.SetInWorldTile(mobile.X, mobile.Y, mobile.Z);
            }
            else if (entity is Item item && item.OnGround)
            {
                item.SetInWorldTile(item.X, item.Y, item.Z);
            }
        }

        // DeleteObject's item branch before T1, by serial
        private void RemovePreT1(uint serial, out Entity parent)
        {
            parent = null;
            Item item = _world.Items.Get(serial);

            if (item == null)
            {
                return;
            }

            CancelSubtreePlacements(item);

            if (item.IsMulti)
            {
                _world.HouseManager.Remove(serial);
            }

            parent = _world.Get(item.Container);
            parent?.Remove(item);
            _world.RemoveItem(serial, true);
        }

#if CUO_DEVTOOLS
        private void CollectSubtree(Entity root)
        {
            _subtree ??= new HashSet<Entity>(ReferenceEqualityComparer.Instance);
            _subtree.Add(root);

            foreach (Item child in _world.State.Children(root))
            {
                CollectSubtree(child);
            }
        }
#endif
    }
}
