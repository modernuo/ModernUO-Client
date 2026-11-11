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
    // T9 S6: Flags and Direction are the record's only (its Flags and Facing components); the record-less cases read None and
    // North, the values a new object had
    public class FlagsDirectionRecordTests
    {
        private const uint MOB = 0x0000_3900, GROUND = 0x4000_7A60;

        [Fact]
        public void The_Getters_Read_What_The_Operations_Committed()
        {
            World world = WorldStateContentsTests.Build();
            world.Writer.ThrowOnViolation = true;

            Item ground = world.Writer.UpsertWorldItem(GROUND, 0x0EED, 0, 1, 1002, 1000, 0, Direction.East, 0, Flags.Hidden | Flags.Movable, 0, out _, out _);
            (ground.Direction, ground.Flags, ground.IsHidden).Should().Be((Direction.East, Flags.Hidden | Flags.Movable, true));
            world.Ecs.Mirror.TryGetEntity(GROUND, out EcsEntity e).Should().BeTrue();
            (e.GetComponent<Facing>().Value, e.GetComponent<ClassicUO.Ecs.Flags>().Value).Should().Be(((byte)Direction.East, (byte)(Flags.Hidden | Flags.Movable)));

            world.Writer.UpsertWorldItem(GROUND, 0x0EED, 0, 1, 1002, 1000, 0, Direction.South, 0, Flags.None, 0, out _, out _);
            (ground.Direction, ground.Flags, ground.IsHidden).Should().Be((Direction.South, Flags.None, false));

            Mobile m = world.Mobiles.Get(MOB);
            world.Writer.SetMobileDirection(m, Direction.Left);
            m.Direction.Should().Be(Direction.Left);
            world.Ecs.Mirror.TryGetEntity(MOB, out EcsEntity me).Should().BeTrue();
            me.GetComponent<Facing>().Value.Should().Be((byte)Direction.Left);
            world.Writer.Violations.Should().Be(0);
        }

        [Fact]
        public void A_Removed_Item_And_A_Detached_One_Read_The_Defaults()
        {
            World world = WorldStateContentsTests.Build();
            Item ground = world.Writer.UpsertWorldItem(GROUND, 0x0EED, 0, 1, 1002, 1000, 0, Direction.East, 0, Flags.Hidden, 0, out _, out _);

            world.RemoveItem(GROUND, true);

            ground.IsDestroyed.Should().BeTrue();
            (ground.Direction, ground.Flags).Should().Be((Direction.North, Flags.None));

            // the house placement preview's kind: never committed
            Item bare = Item.Create(world, 0);
            (bare.Direction, bare.Flags, bare.IsHidden).Should().Be((Direction.North, Flags.None, false));
        }

        // a preview (character creation, race change) reads its preview record
        [Fact]
        public void A_Preview_Mobile_Reads_Its_Preview_Record()
        {
            World world = WorldStateContentsTests.Build();
            object gump = new object();
            Mobile preview = Mobile.Create(world, 1);

            preview.Flags.Should().Be(Flags.None, "no record before the first preview write");
            world.Writer.SetPreviewBody(gump, preview, 0x191, RaceType.HUMAN, true);
            (preview.Flags & Flags.Female).Should().Be(Flags.Female);

            world.Ecs.ReleasePreviews(gump);
            preview.Flags.Should().Be(Flags.None, "released with its gump");
        }
    }
}
