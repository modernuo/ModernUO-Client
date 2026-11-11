using System;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using EcsEntity = Friflo.Engine.ECS.Entity;
using GameEntity = ClassicUO.Game.GameObjects.Entity;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    /// <summary>
    /// T9 S5: Item Amount, Container, Layer and the traits (Item LightID, IsDamageable, UsedLayer; Mobile Race, SpeedMode,
    /// IsFemale, IsRenamable, the player's InWarMode), and T9 S6 Flags and Direction, live only in the record, and their setters are gone. Fixtures and the
    /// old-path oracles (the pre-T1 client) that wrote them now write the record of the instance: its world record (created
    /// from the getters when the held instance has none yet, as the Sync that followed the old writes did) or its preview
    /// record (a bare detached instance gets one). A stale or destroyed instance, or the detached player, has no record: the
    /// write is dropped, as the client has nowhere to keep it either.
    /// </summary>
    internal static class TestRecords
    {
        public static void TestSetAmount(this Item item, ushort amount) => WriteItem(item, amount, item.Container, item.Layer);

        public static void TestSetContainer(this Item item, uint container) => WriteItem(item, item.Amount, container, item.Layer);

        public static void TestSetContainer(this Item item, GameEntity container) => WriteItem(item, item.Amount, container.Serial, item.Layer);

        public static void TestSetLayer(this Item item, Layer layer) => WriteItem(item, item.Amount, item.Container, layer);

        public static void TestSetLightID(this Item item, byte lightId) => Mirror(item)?.SetLightId(item.Serial, lightId);

        public static void TestSetIsDamageable(this Item item, bool on) => Mirror(item)?.SetItemBit(item.Serial, ItemTraitBits.Damageable, on);

        public static void TestSetUsedLayer(this Item item, bool on) => Mirror(item)?.SetItemBit(item.Serial, ItemTraitBits.UsedLayer, on);

        public static void TestSetRace(this Mobile mobile, RaceType race) => Mirror(mobile)?.SetRace(mobile.Serial, (byte)race);

        public static void TestSetSpeedMode(this Mobile mobile, CharacterSpeedType mode) => Mirror(mobile)?.SetSpeedMode(mobile.Serial, (byte)mode);

        public static void TestSetIsRenamable(this Mobile mobile, bool on) => Mirror(mobile)?.SetMobileBit(mobile.Serial, MobileTraitBits.Renamable, on);

        // the setters pushed the inputs (sex and the player's war mode are inputs)
        public static void TestSetIsFemale(this Mobile mobile, bool on)
        {
            Mirror(mobile)?.SetMobileBit(mobile.Serial, MobileTraitBits.Female, on);
            mobile.World.Ecs.PushInputs(mobile);
        }

        public static void TestSetInWarMode(this Mobile mobile, bool on)
        {
            if (mobile is not PlayerMobile)
            {
                return; // the old setter was empty for any other mobile
            }

            Mirror(mobile)?.SetMobileBit(mobile.Serial, MobileTraitBits.WarMode, on);
            mobile.World.Ecs.PushInputs(mobile);
        }

        // T9 S6: Flags and Direction (Facing) are record-only too. The Flags setter only stored the value; the Direction
        // setter also ran the direction-change hook and the Sync (Entity.DirectionCommitted, what the funnel calls now)
        public static void TestSetFlags(this GameEntity entity, Flags flags) => WriteCommon(entity, entity.Direction, flags);

        public static void TestSetDirection(this GameEntity entity, Direction direction)
        {
            Direction before = entity.Direction;

            if (WriteCommon(entity, direction, entity.Flags))
            {
                entity.DirectionCommitted(before);
            }
        }

        private static bool WriteCommon(GameEntity entity, Direction direction, Flags flags)
        {
            WorldMirror mirror = Mirror(entity);

            if (mirror == null)
            {
                return false;
            }

            if (entity as object is Item item)
            {
                mirror.UpsertItem(item.Serial, item.Graphic, item.Hue, item.X, item.Y, item.Z, (byte)direction, (byte)flags, item.Amount, item.Container, (byte)item.Layer, item.IsMulti, handle: item);
            }
            else if (entity as object is Mobile mobile)
            {
                mirror.UpsertMobile(mobile.Serial, mobile.Graphic, mobile.Hue, mobile.X, mobile.Y, mobile.Z, (byte)direction, (byte)flags, ReferenceEquals(mobile, mobile.World.Player) && mirror == mobile.World.Ecs.Mirror, mobile);
            }

            return true;
        }

        private static void WriteItem(Item item, ushort amount, uint container, Layer layer)
        {
            WorldMirror mirror = Mirror(item, create: false);
            mirror?.UpsertItem(item.Serial, item.Graphic, item.Hue, item.X, item.Y, item.Z, (byte)item.Direction, (byte)item.Flags, amount, container, (byte)layer, item.IsMulti, handle: item);
        }

        // the mirror that holds (or, for a held instance, will hold) the entity's record
        private static WorldMirror Mirror(GameEntity entity, bool create = true)
        {
            World world = entity.World ?? throw new InvalidOperationException($"0x{entity.Serial:X8} has no World");

            if (world.TryGetLiveRecord(entity, entity.Serial, out _))
            {
                return world.Ecs.Mirror;
            }

            if (world.Ecs.TryGetPreviewRecord(entity, out EcsEntity _))
            {
                return world.Ecs.Previews;
            }

            if (!entity.IsDestroyed && world.Holds(entity))
            {
                if (create)
                {
                    world.Ecs.Sync(entity);
                }

                return world.Ecs.Mirror;
            }

            // a detached instance (a fixture's bare Item or Mobile, as a preview gump's; not the detached player): a preview
            // record of its own
            if (!entity.IsDestroyed && !world.Holds(entity) && !ReferenceEquals(entity, world.Player))
            {
                if (entity as object is Item item)
                {
                    world.Writer.SetPreviewItem(PreviewOwner, item, item.Graphic, item.Hue, Layer.Invalid, 0xFFFF_FFFF);
                }
                else if (entity as object is Mobile mobile)
                {
                    world.Writer.SetPreviewBody(PreviewOwner, mobile, mobile.Graphic, mobile.Race, mobile.IsFemale);
                }

                if (world.Ecs.TryGetPreviewRecord(entity, out EcsEntity _))
                {
                    return world.Ecs.Previews;
                }
            }

            return null; // stale, destroyed or the detached player: the record was the only copy, the write has nowhere to go
        }

        // the owner of the fixtures' preview records (never released: each test's World goes with them)
        private static readonly object PreviewOwner = new object();
    }
}
