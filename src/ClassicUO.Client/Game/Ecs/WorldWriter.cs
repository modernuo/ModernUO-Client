// SPDX-License-Identifier: BSD-2-Clause

using System.Runtime.CompilerServices;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game
{
    /// <summary>
    /// T1 (ecs-transition-plan.md, "T1: the mutation funnel"): the one writer of world-object state. Each operation matches a
    /// packet's meaning, not a field, and does three things in the order the handler did them before the funnel:
    /// <list type="number">
    /// <item>writes the GameObject properties (still the cache every reader uses) inside its scope;</item>
    /// <item>performs the side effects the writes used to trigger (child lists, <c>CheckGraphicChange</c>, tile placement, which
    /// is observable: tile insertion order breaks equal-priority ties);</item>
    /// <item>writes the ECS once, at the point where the last of the old hook Syncs ran (the <see cref="WorldEcs"/> hooks of the
    /// operation's own entity are folded into that one commit, so the components equal what the old path left by
    /// construction).</item>
    /// </list>
    /// <para>
    /// Every write a scope makes counts as a funnel write for <see cref="FunnelCheck"/> (<c>CUO_FUNNEL_CHECK=1</c>), including
    /// the writes of the methods it calls (<c>CheckGraphicChange</c>, <c>FixHue</c>, <c>SetInWorldTile</c>). Converted so far:
    /// world items (0x1A, 0xF3 and the item branch of 0x1D). Every other writer still relies on <see cref="WorldEcs.Sync"/> and
    /// <see cref="WorldEcs.PushInputs"/>, which stay as the fallback until the last packet group is converted.
    /// </para>
    /// <para>
    /// Detached objects (the character-creation mobiles, the house placement preview, the 0xBF spellbook entries) never pass
    /// here: the funnel creates no entity for an object its World does not hold.
    /// </para>
    /// </summary>
    internal sealed class WorldWriter
    {
        private readonly World _world;
        private int _depth;

        public WorldWriter(World world)
        {
            _world = world;
        }

        /// <summary>True while a funnel operation runs: its writes (and those of the methods it calls) are funnel writes.</summary>
        public bool InScope
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _depth != 0;
        }

        /// <summary>
        /// Opens a scope for a write that has no operation yet (<c>using var s = world.Writer.Enter();</c>). Allocation-free; scopes
        /// nest. Operations open their own.
        /// </summary>
        public Scope Enter()
        {
            _depth++;

            return new Scope(this);
        }

        public readonly ref struct Scope
        {
            private readonly WorldWriter _writer;

            internal Scope(WorldWriter writer)
            {
                _writer = writer;
            }

            public void Dispose()
            {
                _writer._depth--;
            }
        }

        /// <summary>
        /// 0x1A (UpdateItem), 0xF3 (UpdateItemSA) and the other users of <c>UpdateGameObject</c> for a serial that is not a
        /// mobile: an item placed on the ground. Creates the item or takes it out of its container, writes the packet's fields
        /// exactly as <c>UpdateGameObject</c> did, runs <c>CheckGraphicChange</c> (corpse layer and direction, multi load),
        /// places it on its tile and commits the ECS entity. Returns null when no item could be had.
        /// </summary>
        /// <param name="existing">What <c>World.Get(serial)</c> returned: null, a destroyed object, or the live <see cref="Item"/>.</param>
        /// <param name="created">True when the item is new (or replaced a destroyed one).</param>
        public Item UpsertWorldItem
        (
            Entity existing,
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
            WorldEcs ecs = _world.Ecs;
            _depth++;
            ecs.BeginFold(serial);
            Item item = null;

            try
            {
                if (existing == null || existing.IsDestroyed)
                {
                    created = true;
                    item = _world.GetOrCreateItem(serial);

                    if (item == null)
                    {
                        return null;
                    }
                }
                else
                {
                    created = false;
                    item = (Item)existing;

                    if (SerialHelper.IsValid(item.Container))
                    {
                        _world.RemoveItemFromContainer(item);
                    }
                }

                if (graphic != 0x2006)
                {
                    graphic += graphicInc;
                }

                if (type == 2)
                {
                    item.IsMulti = true;
                    item.WantUpdateMulti =
                        (graphic & 0x3FFF) != item.Graphic
                        || item.X != x
                        || item.Y != y
                        || item.Z != z
                        || item.Hue != hue;
                    item.Graphic = (ushort)(graphic & 0x3FFF);
                }
                else
                {
                    item.IsDamageable = type == 3;
                    item.IsMulti = false;
                    item.Graphic = graphic;
                }

                item.X = x;
                item.Y = y;
                item.Z = z;
                item.LightID = (byte)direction;

                if (graphic == 0x2006)
                {
                    item.Layer = (Layer)direction;
                }

                item.FixHue(hue);

                if (count == 0)
                {
                    count = 1;
                }

                item.Amount = count;
                item.Flags = flags;
                item.Direction = direction;
                item.CheckGraphicChange(item.AnimIndex);

                // out of its container (or new), the item is on the ground: UpdateGameObject placed it here
                if (item.OnGround)
                {
                    item.SetInWorldTile(item.X, item.Y, item.Z);
                }
            }
            finally
            {
                ecs.EndFold();
                _depth--;
            }

            // the old path's last hook Sync ran in SetInWorldTile (AddToTile); nothing between it and here writes state
            ecs.Commit(item);

            return item;
        }

        /// <summary>
        /// 0x1D (DeleteObject) for an item: removes a house's components, takes the item out of its parent's child list, then
        /// removes it and its contents from World (each child first, OPL, Destroy, which deletes the ECS entities through
        /// <see cref="WorldEcs.OnDestroyed"/>). The hooks are not folded here: the removal's own Sync of a corpse queues it in
        /// the active-items list as before, so the list order is unchanged.
        /// </summary>
        /// <param name="parent">What <c>World.Get(item.Container)</c> returned (the handler needs it for its gumps).</param>
        public void RemoveWorldItem(Item item, Entity parent)
        {
            _depth++;

            try
            {
                if (item.IsMulti)
                {
                    _world.HouseManager.Remove(item.Serial);
                }

                parent?.Remove(item);
                _world.RemoveItem(item.Serial, true);
            }
            finally
            {
                _depth--;
            }
        }
    }
}
