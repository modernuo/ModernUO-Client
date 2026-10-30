using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // E11: SetInWorldTile syncs the mirror once, after AddToTile (the screen position update no longer syncs on its own).
    // The mirror must still hold the new position, whether the entity is new, already mirrored, or an item that left a container.
    public class SetInWorldTileSyncTests
    {
        private const uint MOBILE = 0x0000_2000, ITEM = 0x4000_5000, BAG = 0x4000_6000;

        [Fact]
        public void Mobile_Mirror_Follows_SetInWorldTile()
        {
            var world = new World();
            Mobile m = world.GetOrCreateMobile(MOBILE);
            m.Graphic = 0x190;

            m.SetInWorldTile(1234, 2345, 7);

            world.Ecs.Mirror.TryGet(MOBILE, out MirrorRecord r).Should().BeTrue("the first position write creates the entity");
            (r.X, r.Y, r.Z).Should().Be((1234, 2345, 7));

            m.SetInWorldTile(1235, 2346, -3);

            world.Ecs.Mirror.TryGet(MOBILE, out r).Should().BeTrue();
            (r.X, r.Y, r.Z).Should().Be((1235, 2346, -3));
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
            r.Parent.Should().Be(BAG);

            // what a ground drop does: the container is cleared, then the item is placed
            it.Container = WorldMirror.NO_CONTAINER;
            it.SetInWorldTile(500, 600, 2);

            world.Ecs.Mirror.TryGet(ITEM, out r).Should().BeTrue();
            (r.X, r.Y, r.Z).Should().Be((500, 600, 2));
            r.OnGround.Should().BeTrue();
        }
    }
}
