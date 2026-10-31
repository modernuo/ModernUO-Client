// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

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
    /// Operations do not nest, and only operations open a scope. A violation (nesting, a placement that is not outstanding,
    /// a removal of an item this World does not hold, an item upsert of a live mobile) is a bug in the caller: it throws in
    /// CUO_DEVTOOLS builds; in a release build it logs an error once per kind, so a funnel bug never disconnects a player
    /// (coordinator, T1 round 2). A nested operation is then rejected outright (nothing changes; T1 round 4: production code
    /// cannot re-enter the writer, see <see cref="Admitted"/>); the other violations happen with nothing open, and the packet
    /// takes the pre-T1 path (the old writes, the hook syncs). While one runs,
    /// writes to its own object (and, for a removal, to the removed subtree) are funnel writes for <see cref="FunnelCheck"/>;
    /// writes to any other object are still direct writes. Converted so far: world items (0x1A, 0xF3 and the item branch of
    /// 0x1D) and items into containers (0x25, 0x3C's items, 0x27's deny path: <see cref="PlaceInContainer"/>), the bulk clear
    /// of 0x3C and 0x24 (<see cref="ClearContainer"/>) and 0x27's other deny branches (<see cref="ReturnHeldItem"/>). Every other writer still relies on <see cref="WorldEcs.Sync"/> and <see cref="WorldEcs.PushInputs"/>, which
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
            internal readonly Item Item;
            internal readonly int Id;

            internal Placement(Item item, int id)
            {
                Item = item;
                Id = id;
            }
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
            _open && (ReferenceEquals(e, _subject) || (_subtree != null && _subtree.Contains(e)));

        /// <summary>The scope of one operation. Only the operations of this class open one (no bare scopes).</summary>
        private ref struct Scope
        {
            private WorldWriter _writer;
            private readonly int _id;
            private readonly Item _fold;

            internal Scope(WorldWriter writer, int id, Item fold)
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
                w._subtree?.Clear();
            }
        }

        // only after Admitted (nothing is open, so BeginFold cannot throw)
        private Scope Open(Entity subject, Item fold)
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
        // 0xF3, 0x1D, 0x25/0x3C and 0x27 handlers, which run only from ParsePackets in GameController's update; inside an
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
        // the pre-T1 path for the others)
        private void Reject(Violation kind, string message)
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
                Log.Error(message + (kind == Violation.Nesting ? " (logged once per kind; rejected)" : " (logged once per kind; the packet takes the pre-T1 path)"));
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

            if (!Consume(placement))
            {
                Reject(Violation.Placement, $"WorldWriter.PlaceWorldItem: 0x{item?.Serial ?? 0:X8} is not an outstanding placement (used twice, superseded, abandoned or never issued)");

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
            CancelPlacements(item);

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
        /// removed (as before T1). An item whose Container differs is destroyed with its contents and recreated, except an
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
                _world.RemoveMobile(serial, true);
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
                // 3. write: the ECS, then the cache from v in the old order
                _world.Ecs.CommitItem(item, in v);

                item.Graphic = v.Graphic;

                if (corpse)
                {
                    item.AnimIndex = 99;
                    item.UsedLayer = usedLayer;
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

                // at the end of the container's child list, also when it already was there (the re-push)
                container.PushToBack(item);
            }

            return item;
        }

        /// <summary>
        /// 0x3C's bulk clear before its first item and 0x24 (OpenContainer) of a container that is not a corpse
        /// (<c>ClearContainerAndRemoveItems</c> before T1): removes every item in the container
        /// (<c>World.Get(containerSerial)</c>) with its contents, or, with <paramref name="keepEquipped"/> (0x3C into a
        /// corpse), every item without a Layer, keeping the corpse's equipment in its child list in order. A removal: it owns
        /// the removed subtrees and writes no value of its own (each removal's Sync and Destroy run as in
        /// <see cref="RemoveWorldItem"/>, not folded: a corpse's removal queues it in the active-items list as before). The
        /// container itself is not written (its child list only). Cancels the removed items' outstanding placements.
        /// </summary>
        public void ClearContainer(uint containerSerial, bool keepEquipped)
        {
            if (!Admitted(nameof(ClearContainer), containerSerial))
            {
                return;
            }

            Entity container = _world.Get(containerSerial);

            if (container == null || container.IsEmpty)
            {
                return;
            }

            // 1. resolve: the removed children, in child-list order, before anything changes
            for (LinkedObject i = container.Items; i != null; i = i.Next)
            {
                if (i is Item it && !(keepEquipped && it.Layer != 0))
                {
                    CancelPlacements(it);
                }
            }

            using (Open(container, null))
            {
#if CUO_DEVTOOLS
                if (FunnelCheck.Enabled)
                {
                    for (LinkedObject i = container.Items; i != null; i = i.Next)
                    {
                        if (i is Item it && !(keepEquipped && it.Layer != 0))
                        {
                            CollectSubtree(it);
                        }
                    }
                }
#endif

                // the old loop: each removal by serial (World.RemoveItem: out of the child list, the contents, OPL,
                // Destroy); the kept equipment's links are untouched and the head is set to the first kept one
                LinkedObject first = container.Items;
                LinkedObject newFirst = null;

                while (first != null)
                {
                    LinkedObject next = first.Next;
                    Item it = (Item)first;

                    if (keepEquipped && it.Layer != 0)
                    {
                        newFirst ??= first;
                    }
                    else
                    {
                        _world.RemoveItem(it, true);
                    }

                    first = next;
                }

                container.Items = keepEquipped ? newFirst : null;
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
        /// <c>RemoveItemFromContainer</c>, then one after the re-equip), which moves it to the end of the mirror's equipment
        /// index. The operation does the same with the computed values: it commits the detached state, then (paperdoll) the
        /// final one. Its hook Syncs are folded.
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

            CancelPlacements(item);

            using (Open(item, item))
            {
#if CUO_DEVTOOLS
                if (result == HeldItemReturn.Removed && FunnelCheck.Enabled)
                {
                    CollectSubtree(item);
                }
#endif

                // 3. write: the ECS (the detached state), then the cache from v in the old order
                _world.Ecs.CommitItem(item, in v);

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
                        v.Container = container.Serial;
                        _world.Ecs.CommitItem(item, in v);
                        item.Container = v.Container;

                        break;

                    case HeldItemReturn.Removed:
                        // World.RemoveItem: its own unlink and Container write (owned), the contents, OPL, Destroy
                        _world.RemoveItem(serial, true);

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

        // the destroy half of PlaceInContainer's destroy-vs-reuse, its own scope (owning the removed subtree)
        private void RemoveForReplacement(Item item)
        {
            CancelPlacements(item);

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

        private Placement Issue(Item item)
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
                if (_placements[i].Id == placement.Id && ReferenceEquals(_placements[i].Item, placement.Item))
                {
                    _placements.RemoveAt(i);

                    return true;
                }
            }

            return false;
        }

        private void CancelPlacements(Item item)
        {
            for (int i = _placements.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_placements[i].Item, item))
                {
                    _placements.RemoveAt(i);
                }
            }
        }

        // ---- the pre-T1 paths: a release build's fallback after a placement or removal violation (the old handler code, the
        // hook syncs). Both run with no operation or fold open (a nesting is rejected, not given a fallback). The write-site
        // budget exempts them by name like the operations (coordinator, T1 round 3); its tests pin that each call follows an
        // unconditional Reject in the violation branch of an operation, so they cannot become a general bypass.

        // the placement of UpdateGameObject's item branch before T1
        private void PlacePreT1(Item item)
        {

            if (item.OnGround)
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

            CancelPlacements(item);

            if (item.IsMulti)
            {
                _world.HouseManager.Remove(serial);
            }

            parent = _world.Get(item.Container);
            parent?.Remove(item);
            _world.RemoveItem(serial, true);
        }

#if CUO_DEVTOOLS
        private void CollectSubtree(Item root)
        {
            _subtree ??= new HashSet<Entity>(ReferenceEqualityComparer.Instance);
            _subtree.Add(root);

            for (LinkedObject i = root.Items; i != null; i = i.Next)
            {
                if (i is Item child)
                {
                    CollectSubtree(child);
                }
            }
        }
#endif
    }
}
