using ClassicUO.Game.GameObjects;

namespace ClassicUO.UnitTests.Ecs
{
    // T9 (cold-caches): the pre-T1 handlers wrote Name, Hits, HitsMax, the pools and Price one setter at a time into a cache
    // field (the hits setters refreshed HitsPercentage). The setters and caches are gone; the tests' copies of the old handler
    // code write the record the same way, one field at a time, through the commits WorldEcs keeps for the funnel. Like a
    // commit, a write to an entity World does not hold (or without a record) is dropped.
    internal static class OldScalarWrites
    {
        public static void Name(Entity e, string value) => e.World.Ecs.CommitName(e, value);

        public static void Hits(Entity e, ushort value)
        {
            e.World.Ecs.CommitVitals(e, value, e.HitsMax);
            Refresh(e);
        }

        public static void HitsMax(Entity e, ushort value)
        {
            e.World.Ecs.CommitVitals(e, e.Hits, value);
            Refresh(e);
        }

        public static void Stamina(Mobile m, ushort value) => m.World.Ecs.CommitStamina(m, m.StaminaMax, value);

        public static void StaminaMax(Mobile m, ushort value) => m.World.Ecs.CommitStamina(m, value, m.Stamina);

        public static void Mana(Mobile m, ushort value) => m.World.Ecs.CommitMana(m, m.ManaMax, value);

        public static void ManaMax(Mobile m, ushort value) => m.World.Ecs.CommitMana(m, value, m.Mana);

        public static void Price(Item i, uint value) => i.World.Ecs.CommitPrice(i, value);

        // the old setters' RefreshHitsPercentage, from the values now in the record
        private static void Refresh(Entity e) => e.RefreshHitsPercentage(e.Hits, e.HitsMax);
    }
}
