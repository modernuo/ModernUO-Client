// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game.UI
{
    /// <summary>
    /// perf-8: a window's reference to the world object it shows by serial, so a per-frame update repeats the dictionary
    /// lookup only when the object may have changed. MobileEntityLookup's rule: the cached object is used while its serial is
    /// the one asked for, it is not destroyed (objects are pooled and reused for other serials), and no mirror record was
    /// deleted since (WorldMirror.Epoch). Otherwise the lookup runs again and its result, also null or a destroyed object
    /// still in the table, is what the caller gets, as before. A null result is not cached (an add moves no epoch). One
    /// reference holds one kind: use either <see cref="Mobile"/> or <see cref="Item"/>.
    /// </summary>
    internal struct EntityRef
    {
        private Entity _entity;
        private uint _epoch;

        private bool TryGet(World world, uint serial, out Entity entity)
        {
            entity = _entity;

            return entity != null && entity.Serial == serial && !entity.IsDestroyed && _epoch == world.Ecs.Mirror.Epoch;
        }

        private void Store(World world, Entity entity)
        {
            _entity = entity;
            _epoch = world.Ecs.Mirror.Epoch;
        }

        /// <summary>What <c>world.Mobiles.Get(serial)</c> returns.</summary>
        public Mobile Mobile(World world, uint serial)
        {
            if (TryGet(world, serial, out Entity e))
            {
                return (Mobile)e;
            }

            Mobile m = world.Mobiles.Get(serial);
            Store(world, m);

            return m;
        }

        /// <summary>What <c>world.Items.Get(serial)</c> returns.</summary>
        public Item Item(World world, uint serial)
        {
            if (TryGet(world, serial, out Entity e))
            {
                return (Item)e;
            }

            Item it = world.Items.Get(serial);
            Store(world, it);

            return it;
        }
    }
}
