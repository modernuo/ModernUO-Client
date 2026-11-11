// SPDX-License-Identifier: BSD-2-Clause

using System.Runtime.CompilerServices;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game
{
    // T9 (ecs-transition-plan.md §T9, survey t9-world-index-2026-11-08): World.Get, GetItem, GetMobile, FindItem and
    // FindMobile answer from the mirror's serial index (each record's handle); Holds (the identity guards) from the tables'
    // marks on the object; Items.Values and Mobiles.Values walk the tables' ordered slots. world_entity_index's off path (the
    // tables' lookups) and CUO_ENTITY_INDEX_CHECK were deleted after a phase default on. The tables' serial dictionaries stay
    // for World's own bookkeeping only (GetOrCreate*, RemoveItem/RemoveMobile, WorldWriter's item-only container lookup, the death
    // re-key): they must find objects the index cannot answer (destroyed but not yet swept, created but not yet committed,
    // an item a mobile wears under its own serial).
    internal sealed partial class World
    {
        /// <summary>The live item under <paramref name="serial"/> (null: none, or destroyed), from the mirror's serial index.
        /// Kind-checked: a mobile under the serial is not an item. Where an item and a mobile share a serial (an item sent with
        /// a mobile's serial, or a corpse under a death-rekeyed mobile's), the index holds the kind written last.</summary>
        public Item GetItem(uint serial) => Ecs.Mirror.GetHandle(serial) is Item it && !it.IsDestroyed ? it : null;

        /// <summary>The live mobile under <paramref name="serial"/>; see <see cref="GetItem"/>.</summary>
        public Mobile GetMobile(uint serial) => Ecs.Mirror.GetHandle(serial) is Mobile m && !m.IsDestroyed ? m : null;

        /// <summary>T9 slice 3's reader form of <c>Items.Get(serial)</c>; since the switch's off path went it is
        /// <see cref="GetItem"/> (a destroyed object not yet swept does not exist: owner decision 2026-11-08).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Item FindItem(uint serial) => GetItem(serial);

        /// <summary>The reader form of <c>Mobiles.Get(serial)</c>; <see cref="GetMobile"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Mobile FindMobile(uint serial) => GetMobile(serial);

        /// <summary>
        /// T9 slice 3 (WorldEcs' commit guard, <c>Entity.FindScalarEntity</c>): the record under <paramref name="serial"/> when
        /// it stands for this live instance (the record's handle is the instance).
        /// </summary>
        internal bool TryGetLiveRecord(Entity e, uint serial, out Friflo.Engine.ECS.Entity record)
        {
            if (e.IsDestroyed)
            {
                record = default;

                return false;
            }

            return Ecs.Mirror.TryGetEntityOf(serial, e, out record);
        }

        /// <summary>
        /// T9 slice 3 part 2 (<c>WorldEcs.OnDestroyed</c>): a live object other than <paramref name="e"/> that owns the record
        /// under its serial (it must stay); null when none. The index's handle of either kind (a newer object not yet committed
        /// has no handle, but then e's record is gone already). Any kind: destroying the mobile that lost a shared serial to an
        /// item must not remove the item's record (Codex audit t9-world-index-5 P2).
        /// </summary>
        internal Entity NewerOwner(Entity e)
        {
            Entity c = Ecs.Mirror.GetHandle(e.Serial) as Entity;

            return c != null && !ReferenceEquals(c, e) && !c.IsDestroyed ? c : null;
        }

        /// <summary>
        /// Whether the Items or Mobiles table (by the object's kind) holds this very instance under its serial: the identity
        /// guard of the funnel and the ECS writes. Destroyed objects not yet swept are still held: the tables keep them until
        /// <see cref="Update"/> drops them, since loops over <c>Values</c> destroy objects as they go (the sweep window).
        /// Answered from the tables' marks on the object (<see cref="ItemTable.Holds(Item, uint)"/>,
        /// <see cref="MobileTable.Holds"/>), not a serial lookup.
        /// </summary>
        public bool Holds(Entity e) => e != null && Holds(e, e.Serial);

        /// <summary><see cref="Holds(Entity)"/> under <paramref name="serial"/> (a serial the object had or is about to have).</summary>
        public bool Holds(Entity e, uint serial) => e is Mobile m ? Mobiles.Holds(m, serial) : e is Item it && Items.Holds(it, serial);
    }
}
