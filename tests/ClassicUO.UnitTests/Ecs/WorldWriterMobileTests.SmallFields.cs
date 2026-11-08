using System;
using System.Collections.Generic;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Ecs
{
    // ecs_small_fields (wave 1C): WorldWriter's operations commit the small cold fields (Item LightID, IsDamageable, UsedLayer;
    // Mobile Race, SpeedMode, IsFemale, IsRenamable; the player's InWarMode) to the record's ItemTraits/MobileTraits before
    // their cache writes, whatever the switch. With the switch on the getters read the record (an instance without a held
    // record reads its cache); CUO_SMALL_FIELDS_CHECK (here World.SmallFieldsCheckOverride) compares the two at every read.
    public partial class WorldWriterMobileTests
    {
        private const uint LAMP = 0x4000_7A01, BARREL = 0x4000_7A02, HOUSE = 0x4000_7A03, BODY = 0x4000_7A04;

        private static World BuildSmall(bool on, bool check = true)
        {
            World world = Build(Start.Standing);
            world.SmallFieldsOverride = on;
            world.SmallFieldsCheckOverride = check;
            world.Writer.ThrowOnViolation = true;

            return world;
        }

        private static World BuildSmallPlayer(bool on, out TestPlayer player)
        {
            World world = BuildPlayer(out player);
            world.SmallFieldsOverride = on;
            world.SmallFieldsCheckOverride = true;
            world.Writer.ThrowOnViolation = true;

            return world;
        }

        private static Item Upsert(World world, uint serial, ushort graphic, byte direction, byte type)
        {
            return world.Writer.UpsertWorldItem(serial, graphic, 0, 1, 1002, 1000, 0, (Direction)direction, 0, Flags.None, type, out _, out _);
        }

        private static ItemTraits ItemRecord(World world, uint serial)
        {
            world.Ecs.Mirror.TryGetEntity(serial, false, out EcsEntity e).Should().BeTrue($"0x{serial:X8} has a record");
            e.TryGetComponent(out ItemTraits t);

            return t;
        }

        private static MobileTraits MobileRecord(World world, uint serial)
        {
            world.Ecs.Mirror.TryGetEntity(serial, true, out EcsEntity e).Should().BeTrue($"0x{serial:X8} has a record");
            e.TryGetComponent(out MobileTraits t);

            return t;
        }

        // every held item and mobile: the record holds the cache's values, the getters return them, the checker finds nothing
        private static void AssertSmallFields(World world, string because)
        {
            long diffs = SmallFields.Diffs;

            foreach (Item it in world.Items.Values)
            {
                ItemTraits t = ItemRecord(world, it.Serial);
                t.Should().Be(it.CachedTraits, $"0x{it.Serial:X8} record ({because})");
                (it.LightID, it.IsDamageable, it.UsedLayer).Should().Be((it.CachedTraits.LightId, (t.Bits & ItemTraitBits.Damageable) != 0, (t.Bits & ItemTraitBits.UsedLayer) != 0), because);
            }

            foreach (Mobile m in world.Mobiles.Values)
            {
                MobileTraits t = MobileRecord(world, m.Serial);
                t.Should().Be(m.CachedTraits, $"0x{m.Serial:X8} record ({because})");
                (m.Race, m.SpeedMode, m.IsFemale, m.IsRenamable).Should().Be(
                    ((RaceType)t.Race, (CharacterSpeedType)t.SpeedMode, (t.Bits & MobileTraitBits.Female) != 0, (t.Bits & MobileTraitBits.Renamable) != 0), because);

                if (m is PlayerMobile p)
                {
                    p.InWarMode.Should().Be((t.Bits & MobileTraitBits.WarMode) != 0, because);
                }
                else
                {
                    (t.Bits & MobileTraitBits.WarMode).Should().Be(MobileTraitBits.None, $"only the player keeps a war mode ({because})");
                }
            }

            SmallFields.Diffs.Should().Be(diffs, because);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Item_Operations_Commit_Light_Damageable_And_UsedLayer(bool on)
        {
            World world = BuildSmall(on);
            long reads = SmallFields.Reads;

            // 0x1A/0xF3: a light source (the direction byte is its light id), a damageable barrel (type 3), a multi (type 2
            // keeps IsDamageable), then the barrel as a plain item again
            Upsert(world, LAMP, 0xA22, 0x1E, 0);
            Upsert(world, BARREL, 0xE77, 0, 3);
            world.Items.Get(LAMP).LightID.Should().Be(0x1E);
            world.Items.Get(BARREL).IsDamageable.Should().BeTrue();
            AssertSmallFields(world, "light and damageable");

            Upsert(world, HOUSE, 0x0064, 0, 2);
            world.Items.Get(HOUSE).IsMulti.Should().BeTrue();
            Upsert(world, BARREL, 0xE77, 3, 0);
            world.Items.Get(BARREL).IsDamageable.Should().BeFalse();
            world.Items.Get(BARREL).LightID.Should().Be(3);
            Upsert(world, LAMP, 0xA22, 0, 0);
            world.Items.Get(LAMP).LightID.Should().Be(0);
            AssertSmallFields(world, "multi, barrel and lamp again");

            // a corpse: the running bit of its direction is UsedLayer (the second death action), then a corpse without it
            Upsert(world, BODY, 0x2006, 0x82, 0);
            world.Items.Get(BODY).UsedLayer.Should().BeTrue();
            AssertSmallFields(world, "running corpse");
            world.Items.Get(BODY).CorpseTimer.UsedLayer.Should().BeTrue("the timer push reads the committed bit");
            Upsert(world, BODY, 0x2006, 0x02, 0);
            world.Items.Get(BODY).UsedLayer.Should().BeFalse();
            AssertSmallFields(world, "corpse");

            // a corpse put in a container and returned to the ground (PlaceInContainer, ReturnHeldItem)
            world.Writer.PlaceInContainer(BODY, 0x2006, 1, 10, 10, 0, BAG, out _).Should().NotBeNull();
            AssertSmallFields(world, "corpse in a bag");

            // ReturnHeldItem and WearItem with the corpse graphic: the death bit comes from the item's running direction
            Upsert(world, 0x4000_7A05, 0xE77, 0x82, 0);
            Upsert(world, 0x4000_7A06, 0xE77, 0x82, 0);
            world.Writer.ReturnHeldItem(0x4000_7A05, 0x2006, 0, 1, Flags.None, Layer.Invalid, 1002, 1000, 0, 0xFFFF_FFFF, out _, out _);
            world.Writer.WearItem(world.Mobiles.Get(MOB), 0x4000_7A06, 0x2006, Layer.Cloak, 0);
            world.Items.Get(0x4000_7A05).UsedLayer.Should().BeTrue();
            world.Items.Get(0x4000_7A06).UsedLayer.Should().BeTrue();
            AssertSmallFields(world, "returned and worn corpses");

            (SmallFields.Reads > reads).Should().BeTrue("the getters asked the record");
            world.Writer.Violations.Should().Be(0);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Mobile_Operations_Commit_Race_Sex_Renamable_And_Speed(bool on)
        {
            World world = BuildSmall(on);
            Mobile m = world.Mobiles.Get(MOB);

            // 0x11 (renamable, the extended part's sex; the race), 0xBF.26, then body changes with CheckGraphicChange's sex
            // and race (0x77/0x78 SetMobileBody, 0x1B SetMobileGraphic)
            world.Writer.SetMobileStatus(m, true, true, true);
            world.Writer.SetRace(m, RaceType.ELF);
            world.Writer.SetSpeedMode(m, CharacterSpeedType.FastUnmount);
            (m.IsRenamable, m.IsFemale, m.Race, m.SpeedMode).Should().Be((true, true, RaceType.ELF, CharacterSpeedType.FastUnmount));
            AssertSmallFields(world, "status, race, speed");

            world.Writer.SetMobileBody(m, 0x190, 0x83EA, Flags.None, true, false, Direction.North);
            (m.IsFemale, m.Race).Should().Be((false, RaceType.HUMAN));
            AssertSmallFields(world, "male human body");
            world.Writer.SetMobileGraphic(m, 0x29B, true);
            (m.IsFemale, m.Race).Should().Be((true, RaceType.GARGOYLE));
            AssertSmallFields(world, "female gargoyle body");

            world.Writer.SetMobileStatus(m, false, false, false);
            m.IsRenamable.Should().BeFalse();
            m.IsFemale.Should().BeTrue("0x11 without the extended part keeps the sex");
            world.Writer.SetSpeedMode(m, CharacterSpeedType.Normal);
            AssertSmallFields(world, "renamable and speed cleared");

            // a new mobile (0x78/0x20 UpsertMobile): its record starts with the body's sex and race (0x25E: a female elf)
            world.Writer.UpsertMobile(null, NEWMOB, 0x25E, 0, 1003, 1000, 0, Direction.East, 0, Flags.None, out bool created, out _);
            created.Should().BeTrue();
            (world.Mobiles.Get(NEWMOB).IsFemale, world.Mobiles.Get(NEWMOB).Race).Should().Be((true, RaceType.ELF));
            AssertSmallFields(world, "new elf");

            // war mode on another mobile is its flag: no bit, the getter is the flag's
            world.Writer.SetWarMode(m, true);
            AssertSmallFields(world, "war mode of a non-player");
            world.Writer.Violations.Should().Be(0);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Players_War_Mode_Is_Committed(bool on)
        {
            World world = BuildSmallPlayer(on, out TestPlayer player);

            foreach (bool war in new[] { true, true, false, true })
            {
                world.Writer.SetWarMode(player, war);
                player.InWarMode.Should().Be(war);
                ((MobileRecord(world, MOB).Bits & MobileTraitBits.WarMode) != 0).Should().Be(war);
                AssertSmallFields(world, $"war {war}");
            }

            // the pushed inputs follow the getter
            world.Ecs.Mirror.TryGetEntity(MOB, out EcsEntity e);
            (e.GetComponent<MobileInputs>().Flags & AnimStateFlags.WarMode).Should().NotBe((AnimStateFlags)0);
        }

        // a write that bypasses the funnel (the cache only): the switch decides what the getter returns, the checker reports it
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Getters_Read_The_Record_With_The_Switch_On(bool on)
        {
            World world = BuildSmallPlayer(on, out TestPlayer player);
            Upsert(world, LAMP, 0xA22, 0x1E, 3);
            Upsert(world, BODY, 0x2006, 0x82, 0);
            world.Writer.SetMobileStatus(player, true, true, true);
            world.Writer.SetRace(player, RaceType.ELF);
            world.Writer.SetSpeedMode(player, CharacterSpeedType.CantRun);
            world.Writer.SetWarMode(player, true);
            AssertSmallFields(world, "committed");

            Item lamp = world.Items.Get(LAMP), body = world.Items.Get(BODY);
            lamp.LightID = 0x05;
            lamp.IsDamageable = false;
            body.UsedLayer = false;
            player.Race = RaceType.GARGOYLE;
            player.SpeedMode = CharacterSpeedType.Normal;
            player.IsFemale = false;
            player.IsRenamable = false;
            player.InWarMode = false;

            long diffs = SmallFields.Diffs;
            lamp.LightID.Should().Be(on ? (byte)0x1E : (byte)0x05);
            lamp.IsDamageable.Should().Be(on);
            body.UsedLayer.Should().Be(on);
            player.Race.Should().Be(on ? RaceType.ELF : RaceType.GARGOYLE);
            player.SpeedMode.Should().Be(on ? CharacterSpeedType.CantRun : CharacterSpeedType.Normal);
            player.IsFemale.Should().Be(on);
            player.IsRenamable.Should().Be(on);
            player.InWarMode.Should().Be(on);
            (SmallFields.Diffs - diffs).Should().Be(8, "the checker reports each stale field");

            // the checker off: with the switch off the getters do not ask the record
            world.SmallFieldsCheckOverride = false;
            long reads = SmallFields.Reads;
            diffs = SmallFields.Diffs;
            player.Race.Should().Be(on ? RaceType.ELF : RaceType.GARGOYLE);
            lamp.LightID.Should().Be(on ? (byte)0x1E : (byte)0x05);
            SmallFields.Diffs.Should().Be(diffs, "no check");

            if (!on)
            {
                SmallFields.Reads.Should().Be(reads, "switch and checker off: the cache only");
            }
        }

        // an instance without a held record (a preview, the detached player, a destroyed instance) reads its cache
        [Fact]
        public void Without_A_Held_Record_The_Getters_Read_The_Cache()
        {
            World world = BuildSmallPlayer(true, out TestPlayer player);
            long fallbacks = SmallFields.Fallbacks;

            var preview = new Item(world) { LightID = 7, IsDamageable = true, UsedLayer = true };
            (preview.LightID, preview.IsDamageable, preview.UsedLayer).Should().Be(((byte)7, true, true));

            var fake = new Mobile(world, 0) { Race = RaceType.GARGOYLE, IsFemale = true, IsRenamable = true, SpeedMode = CharacterSpeedType.CantRun };
            (fake.Race, fake.IsFemale, fake.IsRenamable, fake.SpeedMode).Should().Be((RaceType.GARGOYLE, true, true, CharacterSpeedType.CantRun));
            (SmallFields.Fallbacks - fallbacks).Should().BeGreaterThanOrEqualTo(7);

            // the detached player (removed under its serial while World.Player stays) is written as before T1, cache only
            world.Writer.PlaceInContainer(MOB, 0xE76, 1, 10, 10, 0, BAG, out _).Should().NotBeNull();
            world.Mobiles.Get(MOB).Should().BeNull();
            world.Writer.SetWarMode(player, true);
            world.Writer.SetSpeedMode(player, CharacterSpeedType.FastUnmount);
            (player.InWarMode, player.SpeedMode).Should().Be((true, CharacterSpeedType.FastUnmount));
            world.Writer.Violations.Should().Be(0);
        }

        // a record created while the instance already holds values (a held instance whose record went, a record of the other
        // kind replaced under its serial) starts from the cache
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Record_Created_Later_Is_Seeded_From_The_Cache(bool on)
        {
            World world = BuildSmallPlayer(on, out TestPlayer player);
            world.Writer.SetWarMode(player, true);
            world.Writer.SetSpeedMode(player, CharacterSpeedType.FastUnmount);
            world.Writer.SetMobileStatus(player, true, true, true);
            world.Writer.SetRace(player, RaceType.ELF);
            Upsert(world, LAMP, 0xA22, 0x1E, 3);
            world.Ecs.Mirror.Remove(MOB).Should().BeTrue();
            world.Ecs.Mirror.Remove(LAMP).Should().BeTrue();

            long seeds = world.Ecs.TraitSeeds;
            world.Ecs.Sync(player);
            world.Ecs.Sync(world.Items.Get(LAMP));
            (world.Ecs.TraitSeeds - seeds).Should().Be(2);
            ItemRecord(world, LAMP).Should().Be(new ItemTraits { LightId = 0x1E, Bits = ItemTraitBits.Damageable });
            MobileRecord(world, MOB).Should().Be(new MobileTraits
            {
                Race = (byte)RaceType.ELF,
                SpeedMode = (byte)CharacterSpeedType.FastUnmount,
                Bits = MobileTraitBits.Female | MobileTraitBits.Renamable | MobileTraitBits.WarMode
            });
            AssertSmallFields(world, "records re-created");

            // an update of an existing record seeds nothing
            seeds = world.Ecs.TraitSeeds;
            world.Ecs.Sync(player);
            world.Ecs.Sync(world.Mobiles.Get(OTHER));
            world.Ecs.TraitSeeds.Should().Be(seeds);
        }

        // 0xAF: the record moves to serial | 0x80000000 and keeps the traits
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Death_Rekey_Carries_The_Mobile_Traits(bool on)
        {
            World world = BuildSmall(on);
            Mobile m = world.Mobiles.Get(MOB);
            world.Writer.SetMobileStatus(m, true, true, true);
            world.Writer.SetRace(m, RaceType.GARGOYLE);
            world.Writer.SetSpeedMode(m, CharacterSpeedType.CantRun);
            MobileTraits before = MobileRecord(world, MOB);

            uint dead = world.Writer.RekeyDeadMobile(m);
            dead.Should().Be(MOB | 0x8000_0000);
            MobileRecord(world, dead).Should().Be(before);
            (m.Race, m.IsFemale, m.IsRenamable, m.SpeedMode).Should().Be((RaceType.GARGOYLE, true, true, CharacterSpeedType.CantRun));
            AssertSmallFields(world, "re-keyed");
        }

        [Fact]
        public void The_Mirror_Adds_The_Traits_Only_For_A_Non_Default_Value()
        {
            var mirror = new WorldMirror();
            mirror.UpsertItem(LAMP, 0xA22, 0, 1, 1, 0, 0, 0, 1, WorldMirror.NO_CONTAINER, 0, false);
            mirror.UpsertMobile(MOB, 0x190, 0, 1, 1, 0, 0, 0, false);
            mirror.TryGetEntity(LAMP, false, out EcsEntity item).Should().BeTrue();
            mirror.TryGetEntity(MOB, true, out EcsEntity mob).Should().BeTrue();

            mirror.SetLightId(LAMP, 0).Should().BeTrue();
            mirror.SetItemBit(LAMP, ItemTraitBits.Damageable, false).Should().BeTrue();
            mirror.SetItemTraits(LAMP, default).Should().BeTrue();
            item.HasComponent<ItemTraits>().Should().BeFalse();
            mirror.SetRace(MOB, 0);
            mirror.SetSpeedMode(MOB, 0);
            mirror.SetMobileBit(MOB, MobileTraitBits.Female, false);
            mirror.SetMobileTraits(MOB, default);
            mob.HasComponent<MobileTraits>().Should().BeFalse();

            mirror.SetItemBit(LAMP, ItemTraitBits.UsedLayer, true);
            mirror.SetLightId(LAMP, 9);
            mirror.SetItemBit(LAMP, ItemTraitBits.Damageable, true);
            mirror.SetItemBit(LAMP, ItemTraitBits.UsedLayer, false);
            item.GetComponent<ItemTraits>().Should().Be(new ItemTraits { LightId = 9, Bits = ItemTraitBits.Damageable });

            mirror.SetSpeedMode(MOB, 2);
            mirror.SetRace(MOB, 3);
            mirror.SetMobileBit(MOB, MobileTraitBits.WarMode, true);
            mirror.SetMobileBit(MOB, MobileTraitBits.Renamable, true);
            mirror.SetMobileBit(MOB, MobileTraitBits.WarMode, false);
            mob.GetComponent<MobileTraits>().Should().Be(new MobileTraits { Race = 3, SpeedMode = 2, Bits = MobileTraitBits.Renamable });

            mirror.TryGetExtras(MOB, out MirrorExtras x).Should().BeTrue();
            x.MobileTraits.Should().Be(mob.GetComponent<MobileTraits>());
            mirror.UpsertMobile(NEWMOB, 0x190, 0, 1, 1, 0, 0, 0, false);
            mirror.CarryMobileExtras(in x, NEWMOB);
            mirror.TryGetEntity(NEWMOB, true, out EcsEntity carried).Should().BeTrue();
            carried.GetComponent<MobileTraits>().Should().Be(x.MobileTraits);

            mirror.SetLightId(0x4000_7FFF, 1).Should().BeFalse("no record");
            mirror.SetMobileBit(0x0000_3FFF, MobileTraitBits.Female, true).Should().BeFalse("no record");
        }
    }
}
