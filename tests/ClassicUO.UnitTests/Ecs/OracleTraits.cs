using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // T9 deleted ecs_small_fields' off path: the small cold fields' getters read the record (ItemTraits, MobileTraits), which
    // WorldWriter's operations commit. The old-path oracles (the pre-T1 client) write only the caches, so before a comparison
    // their World's records take the caches' values, and a mobile whose traits changed re-pushes its inputs (its setters
    // pushed them from the stale record).
    internal static class OracleTraits
    {
        public static void Sync(World world)
        {
            foreach (Item item in world.Items.Values)
            {
                if (world.Ecs.Mirror.TryGetEntity(item.Serial, false, out EcsEntity e))
                {
                    e.TryGetComponent(out ItemTraits have);
                    ItemTraits want = item.CachedTraits;

                    if (!have.Equals(want))
                    {
                        world.Ecs.Mirror.SetItemTraits(item.Serial, in want);
                    }
                }
            }

            foreach (Mobile mobile in world.Mobiles.Values)
            {
                if (world.Ecs.Mirror.TryGetEntity(mobile.Serial, true, out EcsEntity e))
                {
                    e.TryGetComponent(out MobileTraits have);
                    MobileTraits want = mobile.CachedTraits;

                    if (!have.Equals(want))
                    {
                        world.Ecs.Mirror.SetMobileTraits(mobile.Serial, in want);
                        world.Ecs.PushInputs(mobile);
                    }
                }
            }
        }
    }
}
