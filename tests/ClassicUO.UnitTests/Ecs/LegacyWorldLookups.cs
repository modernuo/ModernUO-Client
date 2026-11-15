using ClassicUO.Game;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.UnitTests.Ecs
{
    // T9: World.Get before world_entity_index (deleted with its off path): the live object under a serial from the Items and
    // Mobiles tables, the serial's kind first. The old-path references (OldEquip, OldWear, LegacyRekeyReference) reproduce
    // the old handlers with it; it differs from World.Get only where an item and a mobile share a serial.
    internal static class LegacyWorldLookups
    {
        public static Entity GetHeld(World world, uint serial)
        {
            Entity ent;

            if (SerialHelper.IsMobile(serial))
            {
                ent = (Entity)world.Mobiles.Get(serial) ?? world.Items.Get(serial);
            }
            else
            {
                ent = (Entity)world.Items.Get(serial) ?? world.Mobiles.Get(serial);
            }

            return ent != null && ent.IsDestroyed ? null : ent;
        }
    }
}
