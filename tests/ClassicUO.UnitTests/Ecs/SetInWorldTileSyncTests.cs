using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // E11: SetInWorldTile syncs the mirror once, after AddToTile (the screen position update no longer syncs on its own).
    // The mirror must still hold the new position, whether the entity is missing from it, already mirrored, or an item that
    // left a container. Not covered: the parked and loaded-chunk paths of AddToTile, which need a Map (it reads the UO file
    // manager); they end in the same single Sync, read from the code (Map.Park true -> Sync(parked); else AddToTile(chunk) -> Sync).
    public class SetInWorldTileSyncTests
    {
        private const uint MOBILE = 0x0000_2000, ITEM = 0x4000_5000, BAG = 0x4000_6000;

        private static uint Pushes(World world)
        {
            world.Ecs.Mirror.TryGetEntity(MOBILE, out Friflo.Engine.ECS.Entity e).Should().BeTrue();

            return e.GetComponent<MobileInputs>().Pushes;
        }

        [Fact]
        public void Mobile_Missing_From_The_Mirror_Is_Created_By_One_Sync()
        {
            var world = new World();
            Mobile m = world.GetOrCreateMobile(MOBILE);
            m.Graphic = 0x190;

            // GetOrCreateMobile already mirrored it: drop the entry so SetInWorldTile has to create it
            world.Ecs.Mirror.Remove(MOBILE).Should().BeTrue();
            world.Ecs.Mirror.TryGet(MOBILE, out _).Should().BeFalse();

            m.SetInWorldTile(1234, 2345, 7);

            world.Ecs.Mirror.TryGet(MOBILE, out MirrorRecord r).Should().BeTrue("the placement's Sync creates the entity");
            (r.X, r.Y, r.Z).Should().Be((1234, 2345, 7));
            world.Ecs.Mirror.MobileCount.Should().Be(1);
            m.IsPositionChanged.Should().BeTrue();
        }

        [Fact]
        public void Mobile_Mirror_Follows_Each_Move_With_Exactly_One_Push()
        {
            var world = new World();
            Mobile m = world.GetOrCreateMobile(MOBILE);
            m.Graphic = 0x190;
            m.SetInWorldTile(1234, 2345, 7);

            for (int i = 1; i <= 3; i++)
            {
                m.UpdateRealScreenPositionOnly(0, 0);
                m.IsPositionChanged.Should().BeFalse();
                uint before = Pushes(world);

                m.SetInWorldTile((ushort)(1234 + i), (ushort)(2345 + i), (sbyte)(7 - i));

                m.IsPositionChanged.Should().BeTrue("the screen position is marked stale");
                (Pushes(world) - before).Should().Be(1, "one mirror Sync per SetInWorldTile");
                world.Ecs.Mirror.TryGet(MOBILE, out MirrorRecord r).Should().BeTrue();
                (r.X, r.Y, r.Z).Should().Be(((ushort)(1234 + i), (ushort)(2345 + i), (sbyte)(7 - i)));
            }

            world.Ecs.Mirror.MobileCount.Should().Be(1);
        }

        [Fact]
        public void Item_Dropped_From_A_Container_Is_Rekeyed_And_Positioned()
        {
            var world = new World();
            Item bag = world.GetOrCreateItem(BAG);
            bag.Graphic = 0xE75;
            world.Ecs.Sync(bag);

            Item it = world.GetOrCreateItem(ITEM);
            it.Graphic = 0x1515;
            it.Container = BAG;
            world.Ecs.Sync(it);
            world.Ecs.Mirror.TryGet(ITEM, out MirrorRecord r).Should().BeTrue();
            r.OnGround.Should().BeFalse();
            r.ParentKind.Should().Be(ParentKind.Contained);
            r.Parent.Should().Be(BAG);
            world.Ecs.Mirror.GetContents(BAG).Count.Should().Be(1);

            // what a ground drop does: the container is cleared, then the item is placed
            it.Container = WorldMirror.NO_CONTAINER;
            it.UpdateRealScreenPositionOnly(0, 0);
            it.SetInWorldTile(500, 600, 2);

            it.IsPositionChanged.Should().BeTrue();
            world.Ecs.Mirror.TryGet(ITEM, out r).Should().BeTrue();
            (r.X, r.Y, r.Z).Should().Be((500, 600, 2));
            r.OnGround.Should().BeTrue();
            r.ParentKind.Should().Be(ParentKind.None);
            world.Ecs.Mirror.GetContents(BAG).Count.Should().Be(0, "the item left the bag's index");
        }
    }
}
