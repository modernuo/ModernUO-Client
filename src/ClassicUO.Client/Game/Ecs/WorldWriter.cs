// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game
{
    /// <summary>
    /// T1 (ecs-transition-plan.md, "T1: the mutation funnel"): the one writer of world-object state. Each operation matches a
    /// packet's meaning, not a field. Its shape (round 2, after the Codex audit of slice 1):
    /// <list type="number">
    /// <item><b>Resolve and check.</b> The operation finds the object itself (or checks the one it was given belongs to its
    /// World and is the live instance) before any side effect. Foreign or stale arguments throw.</item>
    /// <item><b>Compute.</b> It computes the authoritative values (an <see cref="ItemState"/>) from the packet and the
    /// previous state, with the pure helpers (<see cref="Entity.FixedHue"/>, <see cref="Item.CorpseState"/>). Every read of
    /// the previous state happens here, before the first write.</item>
    /// <item><b>Write.</b> It writes the ECS (<see cref="WorldEcs.CommitItem"/>) and the GameObject cache from those values,
    /// never one read back from the other. The hook Syncs of the operation's own instance are folded (skipped) meanwhile.</item>
    /// <item><b>Derived state.</b> Only then the helpers that read world state run (<see cref="Item.RefreshGraphicState"/>:
    /// drawability, the multi load). They read values that the ECS and the cache both already hold, so the order stays right
    /// when T4 moves the getters to the ECS.</item>
    /// <item><b>Placement.</b> Tile placement is a separate operation when the handler has callbacks between the writes and
    /// the placement (<see cref="PlaceWorldItem"/>: 0x1A's new-corpse SingleClick sends a packet, and plugin send hooks run
    /// synchronously, so they must see the item written but not yet on its tile, as before T1).</item>
    /// </list>
    /// <para>
    /// Operations do not nest (a second scope while one is open throws), and only operations open a scope. While one runs,
    /// writes to its own object (and, for a removal, to the removed subtree) are funnel writes for <see cref="FunnelCheck"/>;
    /// writes to any other object are still direct writes. Converted so far: world items (0x1A, 0xF3 and the item branch of
    /// 0x1D). Every other writer still relies on <see cref="WorldEcs.Sync"/> and <see cref="WorldEcs.PushInputs"/>, which
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
        private readonly World _world;

        // the open scope: its id (a copied or late Dispose of an older scope finds another id and does nothing), its subject
        // and, for a removal with the check on, the subtree whose writes it owns
        private int _scopeId;
        private bool _open;
        private Entity _subject;
        private HashSet<Entity> _subtree;

        // 0x1A between UpsertWorldItem and PlaceWorldItem: the item written but not yet placed
        private Item _pendingPlace;

        public WorldWriter(World world)
        {
            _world = world;
        }

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

        private Scope Open(Entity subject, Item fold)
        {
            if (_open)
            {
                throw new InvalidOperationException(
                    $"WorldWriter: an operation on 0x{subject?.Serial ?? 0:X8} started inside the operation on 0x{_subject?.Serial ?? 0:X8}; operations do not nest");
            }

            if (fold != null)
            {
                _world.Ecs.BeginFold(fold); // throws, before anything is open, if a fold is already running
            }

            _open = true;
            _subject = subject;

            return new Scope(this, ++_scopeId, fold);
        }

        /// <summary>
        /// 0x1A (UpdateItem), 0xF3 (UpdateItemSA) and the other users of <c>UpdateGameObject</c> for a serial that is not a
        /// mobile: an item on the ground, first half. Finds the item (<c>World.Get(serial)</c>) or creates it, takes it out of
        /// its container, writes the packet's values to the ECS and the GameObject, and refreshes its draw state and multi.
        /// It does not place the item on its tile: the handler runs its callbacks, then calls <see cref="PlaceWorldItem"/>.
        /// Returns null when no item could be had.
        /// </summary>
        /// <param name="created">True when the item is new (or replaced a destroyed one, or <c>World.Get</c> did not find a
        /// live item for this serial: a mobile-valued serial with type 3 is looked up among the mobiles, as before T1).</param>
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
            out bool created
        )
        {
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
                throw new ArgumentException($"WorldWriter.UpsertWorldItem: 0x{serial:X8} is a live mobile", nameof(serial));
            }

            _pendingPlace = null;

            using (Open(item, item))
            {
                // only an item World.Get found; a "created" one that GetOrCreateItem found alive stays where it is, as before T1
                if (!created && SerialHelper.IsValid(item.Container))
                {
                    // the parent's child list, its gump, the tile (its own Sync is folded); the item's Container becomes 0xFFFFFFFF
                    _world.RemoveItemFromContainer(item);
                }

                // 2. compute (every read of the previous state is here)
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
                v.Container = item.Container;
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

                // 3. write: the ECS, then the cache, from v (the old path's order of the cache writes)
                _world.Ecs.CommitItem(item, in v);

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

            _pendingPlace = item;

            return item;
        }

        /// <summary>
        /// 0x1A/0xF3, second half: places the item <see cref="UpsertWorldItem"/> returned on its tile when it is on the ground
        /// (tile insertion order breaks equal-priority ties, so this is where the old handler placed it: after its callbacks).
        /// The ECS already holds the item's values (the placement changes none), so the placement's own Sync is folded.
        /// </summary>
        public void PlaceWorldItem(Item item)
        {
            if (item == null || !ReferenceEquals(item, _pendingPlace))
            {
                throw new ArgumentException("WorldWriter.PlaceWorldItem: not the item the last UpsertWorldItem returned", nameof(item));
            }

            _pendingPlace = null;

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
        /// the active-items list as before, so the list order is unchanged.
        /// </summary>
        /// <param name="item">The item World holds for its serial; anything else (a foreign or stale instance) throws before any
        /// side effect.</param>
        /// <param name="parent">What <c>World.Get(item.Container)</c> returned before the removal (the handler's gumps use it).</param>
        public void RemoveWorldItem(Item item, out Entity parent)
        {
            // the instance this World holds (a destroyed one World still holds goes through, as DeleteObject's did before T1:
            // the house and parent cleanup run, World.RemoveItem then finds it destroyed and does nothing)
            if (item == null || item.World != _world || !ReferenceEquals(_world.Items.Get(item.Serial), item))
            {
                throw new ArgumentException("WorldWriter.RemoveWorldItem: not the item this World holds for its serial", nameof(item));
            }

            parent = _world.Get(item.Container);
            uint serial = item.Serial;

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
