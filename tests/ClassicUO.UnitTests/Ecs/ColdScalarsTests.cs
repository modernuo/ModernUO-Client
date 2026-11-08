using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // T9 (cold-caches): Name, Hits, HitsMax, Stamina, StaminaMax, Mana, ManaMax and Price live only in the entity's mirror
    // record. The health-bar percentage is computed by the funnel operations that write hits, from the values they write.
    public class ColdScalarsTests
    {
        private const uint MOB = 0x0000_3900, DMG = 0x4000_7A00;

        private static Item PlaceDamageable(World world, ushort graphic = 0x0F3F)
        {
            Item item = world.Writer.UpsertWorldItem(DMG, graphic, 0, 1, 1005, 1000, 0, Direction.North, 0, Flags.None, 3, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);

            return item;
        }

        // damageable-items survey: the server never resends the hits with the item, so a 0xF3 re-send of a live item keeps them
        [Fact]
        public void A_Damageable_Items_Hits_Survive_A_Same_Kind_Resend()
        {
            World world = EcsCheckV2Tests.BuildFunnelWorld();
            Item item = PlaceDamageable(world);
            item.IsDamageable.Should().BeTrue();

            WorldWriterMobileTests.NewUpdateHitpoints(world, DMG, 200, 50);
            (item.Hits, item.HitsMax, item.HitsPercentage).Should().Be(((ushort)50, (ushort)200, (byte)25));

            Item again = PlaceDamageable(world, 0x0F40);

            again.Should().BeSameAs(item);
            (item.Graphic, item.IsDamageable).Should().Be(((ushort)0x0F40, true));
            (item.Hits, item.HitsMax, item.HitsPercentage).Should().Be(((ushort)50, (ushort)200, (byte)25));
        }

        [Fact]
        public void Status_0x11_For_An_Item_Writes_Its_Name_And_Hits()
        {
            World world = EcsCheckV2Tests.BuildFunnelWorld();
            Item item = PlaceDamageable(world);

            WorldWriterMobileTests.NewCharacterStatus(world, new WorldWriterMobileTests.StatusPacket { Serial = DMG, Name = "a barrel", Hits = 30, HitsMax = 40, Type = 3 });

            (item.Name, item.Hits, item.HitsMax, item.HitsPercentage).Should().Be(("a barrel", (ushort)30, (ushort)40, (byte)75));
        }

        [Fact]
        public void Attributes_0x2D_For_An_Item_Writes_Only_Its_Hits()
        {
            World world = EcsCheckV2Tests.BuildFunnelWorld();
            Item item = PlaceDamageable(world);

            WorldWriterMobileTests.NewMobileAttributes(world, new WorldWriterMobileTests.AttrPacket { Serial = DMG, HitsMax = 80, Hits = 20, ManaMax = 9, Mana = 8, StaminaMax = 7, Stamina = 6 });

            (item.Hits, item.HitsMax, item.HitsPercentage).Should().Be(((ushort)20, (ushort)80, (byte)25));
            world.Ecs.Mirror.TryGetExtras(DMG, out MirrorExtras x).Should().BeTrue();
            (x.Mana, x.ManaMax, x.Stamina, x.StaminaMax).Should().Be(((ushort)0, (ushort)0, (ushort)0, (ushort)0));
        }

        // the percentage the funnel computes: the clamped share of hits for HitsMax > 0, unchanged for HitsMax 0
        [Theory]
        [InlineData(100, 100, 100)]
        [InlineData(100, 37, 37)]
        [InlineData(200, 50, 25)]
        [InlineData(3, 1, 33)]
        [InlineData(50, 80, 100)]
        [InlineData(60, 0, 0)]
        public void SetHits_And_SetStatus_Compute_The_Percentage_From_The_Values_They_Write(int hitsMax, int hits, int percent)
        {
            World world = EcsCheckV2Tests.BuildFunnelWorld();
            Mobile m = world.Mobiles.Get(MOB);

            world.Writer.SetHits(m, (ushort)hitsMax, (ushort)hits);
            m.HitsPercentage.Should().Be((byte)percent, "0xA1/0x2D");
            m.HitsTexture.Should().NotBeNull();

            world.Writer.SetHits(m, 10, 9);
            world.Writer.SetStatus(m, "Moe", (ushort)hits, (ushort)hitsMax);
            m.HitsPercentage.Should().Be((byte)percent, "0x11");
        }

        [Fact]
        public void HitsMax_Zero_Leaves_The_Percentage_As_It_Was()
        {
            World world = EcsCheckV2Tests.BuildFunnelWorld();
            Mobile m = world.Mobiles.Get(MOB);
            world.Writer.SetHits(m, 4, 3);
            m.HitsPercentage.Should().Be(75);

            world.Writer.SetHits(m, 0, 2);
            m.HitsPercentage.Should().Be(75);
            world.Writer.SetStatus(m, "Moe", 1, 0);
            m.HitsPercentage.Should().Be(75);
            (m.Hits, m.HitsMax).Should().Be(((ushort)1, (ushort)0));
        }

        // 0x11 writes Hits before HitsMax, so with HitsMax 0 the percentage is the new hits of the previous maximum; 0xA1/0x2D
        // write HitsMax first and keep the percentage
        [Fact]
        public void Status_With_HitsMax_Zero_Takes_The_New_Hits_Of_The_Previous_Maximum()
        {
            World world = EcsCheckV2Tests.BuildFunnelWorld();
            Mobile m = world.Mobiles.Get(MOB);
            world.Writer.SetHits(m, 60, 60);
            m.HitsPercentage.Should().Be(100);

            world.Writer.SetStatus(m, "Moe", 15, 0);
            m.HitsPercentage.Should().Be(25);
            (m.Hits, m.HitsMax).Should().Be(((ushort)15, (ushort)0));

            world.Writer.SetHits(m, 60, 60);
            world.Writer.SetHits(m, 0, 15);
            m.HitsPercentage.Should().Be(100);
        }

        // the percentage follows every hits write and nothing else: the pools and the name do not move it
        [Fact]
        public void The_Pools_And_The_Name_Do_Not_Move_The_Percentage()
        {
            World world = EcsCheckV2Tests.BuildFunnelWorld();
            Mobile m = world.Mobiles.Get(MOB);
            world.Writer.SetHits(m, 10, 5);

            world.Writer.SetMana(m, 10, 1);
            world.Writer.SetStamina(m, 10, 9);
            world.Writer.SetName(m, "renamed");

            m.HitsPercentage.Should().Be(50);
        }

        // Owner decision 2026-11-07: a record replaced by the other kind under one serial (only a misconfigured server sends
        // an item and a mobile under one serial) starts without the cold values; they come back with the next status packet.
        [Fact]
        public void A_Kind_Replacement_Starts_Without_Cold_Values_And_The_Next_Status_Brings_Them_Back()
        {
            World world = WorldWriterMobileTests.BuildDead();
            const uint DEAD = WorldWriterMobileTests.DEAD;
            Mobile m = world.Mobiles.Get(DEAD);
            Item corpse = world.Items.Get(DEAD);

            world.Writer.SetStatus(corpse, "a corpse", 1, 2);
            corpse.Name.Should().Be("a corpse");

            // the mobile's record replaces the item's
            world.Ecs.Sync(m);
            m.TryScalarEntity(out _).Should().BeTrue();
            (m.Name, m.Hits, m.HitsMax, m.Mana, m.ManaMax, m.Stamina, m.StaminaMax)
                .Should().Be(((string)null, (ushort)0, (ushort)0, (ushort)0, (ushort)0, (ushort)0, (ushort)0));
            corpse.TryScalarEntity(out _).Should().BeFalse();
            (corpse.Name, corpse.Hits).Should().Be(((string)null, (ushort)0));

            world.Writer.SetStatus(m, "Moe", 30, 40);
            world.Writer.SetMana(m, 20, 10);
            world.Writer.SetStamina(m, 22, 11);
            (m.Name, m.Hits, m.HitsMax, m.Mana, m.ManaMax, m.Stamina, m.StaminaMax)
                .Should().Be(("Moe", (ushort)30, (ushort)40, (ushort)10, (ushort)20, (ushort)11, (ushort)22));
            m.HitsPercentage.Should().Be(75);

            // and back: the item's record replaces the mobile's, again without the cold values, and the item's own status
            // brings its values back
            world.Ecs.Sync(corpse);
            (corpse.Name, corpse.Hits).Should().Be(((string)null, (ushort)0));
            m.Name.Should().BeNull();
            world.Writer.SetStatus(corpse, "a corpse", 1, 2);
            (corpse.Name, corpse.Hits, corpse.HitsMax).Should().Be(("a corpse", (ushort)1, (ushort)2));
        }

        // the notoriety is a hot field (the Mobile keeps it), so a replaced record still gets it
        [Fact]
        public void A_Kind_Replacement_Still_Carries_The_Notoriety()
        {
            World world = WorldWriterMobileTests.BuildDead();
            const uint DEAD = WorldWriterMobileTests.DEAD;
            Mobile m = world.Mobiles.Get(DEAD);
            world.Writer.SetNotoriety(m, NotorietyFlag.Murderer);

            world.Ecs.Sync(m);

            world.Ecs.Mirror.TryGetExtras(DEAD, out MirrorExtras x).Should().BeTrue();
            x.Notoriety.Should().Be((byte)NotorietyFlag.Murderer);
        }

        // an entity without a record (removed, or never in World) reads the defaults
        [Fact]
        public void An_Entity_Without_A_Record_Reads_The_Defaults()
        {
            World world = EcsCheckV2Tests.BuildFunnelWorld();
            Mobile m = world.Mobiles.Get(MOB);
            m.Name.Should().Be("Moe the Brave");

            world.Writer.RemoveMobile(m);

            (m.Name, m.Hits, m.HitsMax).Should().Be(((string)null, (ushort)0, (ushort)0));
            var preview = new PlayerMobile(world, 1);
            (preview.Name, preview.Hits, preview.Mana, preview.Stamina).Should().Be(((string)null, (ushort)0, (ushort)0, (ushort)0));
        }
    }
}
