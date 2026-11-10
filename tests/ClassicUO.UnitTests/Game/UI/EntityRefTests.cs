using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.UI;
using FluentAssertions;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Game.UI
{
    // perf-8: the paperdoll's slots read their mobile (and each slot its item) through EntityRef instead of a dictionary
    // lookup per slot per frame. Whatever happened to the object, the reference must return what the lookup returns now.
    public sealed class EntityRefTests
    {
        private const uint MOB = 0x0000_1001, OTHER = 0x0000_1002, ITEM = 0x4000_B001, ITEM2 = 0x4000_B002;

        [Fact]
        public void Returns_The_Cached_Mobile_While_Nothing_Changed()
        {
            var world = new World();
            Mobile m = world.GetOrCreateMobile(MOB);
            var r = new EntityRef();

            r.Mobile(world, MOB).Should().BeSameAs(m);
            r.Mobile(world, MOB).Should().BeSameAs(m);
        }

        [Fact]
        public void A_Removed_Mobile_Reads_As_Gone_And_A_New_One_Under_The_Serial_Is_Found()
        {
            var world = new World();
            Mobile old = world.GetOrCreateMobile(MOB);
            var r = new EntityRef();
            r.Mobile(world, MOB).Should().BeSameAs(old);

            world.Writer.RemoveMobile(old);

            r.Mobile(world, MOB).Should().BeSameAs(world.FindMobile(MOB));
            (r.Mobile(world, MOB) == null || r.Mobile(world, MOB).IsDestroyed).Should().BeTrue();

            Mobile next = world.GetOrCreateMobile(MOB);
            r.Mobile(world, MOB).Should().BeSameAs(next);
            r.Mobile(world, MOB).IsDestroyed.Should().BeFalse();
        }

        [Fact]
        public void A_Destroyed_Object_Is_Not_Returned_From_The_Cache()
        {
            var world = new World();
            Mobile m = world.GetOrCreateMobile(MOB);
            var r = new EntityRef();
            r.Mobile(world, MOB);

            // destroyed (pooled for reuse) while still in the table and with no mirror deletion: the lookup decides
            m.Destroy();

            r.Mobile(world, MOB).Should().BeSameAs(world.FindMobile(MOB));
        }

        [Fact]
        public void Another_Serial_Or_A_Changed_Serial_Repeats_The_Lookup()
        {
            var world = new World();
            Mobile m = world.GetOrCreateMobile(MOB);
            Mobile o = world.GetOrCreateMobile(OTHER);
            var r = new EntityRef();
            r.Mobile(world, MOB).Should().BeSameAs(m);

            r.Mobile(world, OTHER).Should().BeSameAs(o, "the window now shows another serial");
            r.Mobile(world, MOB).Should().BeSameAs(m);
        }

        [Fact]
        public void Any_Mirror_Deletion_Repeats_The_Lookup()
        {
            var world = new World();
            Mobile m = world.GetOrCreateMobile(MOB);
            Mobile o = world.GetOrCreateMobile(OTHER);
            var r = new EntityRef();
            r.Mobile(world, MOB);
            uint epoch = world.Ecs.Mirror.Epoch;

            world.Writer.RemoveMobile(o);

            world.Ecs.Mirror.Epoch.Should().NotBe(epoch);
            r.Mobile(world, MOB).Should().BeSameAs(m);
        }

        [Fact]
        public void A_Missing_Object_Is_Not_Cached()
        {
            var world = new World();
            var r = new EntityRef();

            r.Mobile(world, MOB).Should().BeNull();

            Mobile m = world.GetOrCreateMobile(MOB); // an add moves no epoch
            r.Mobile(world, MOB).Should().BeSameAs(m);
        }

        [Fact]
        public void Items_Follow_The_Same_Rules()
        {
            var world = new World();
            Item it = world.Writer.UpsertWorldItem(ITEM, 0x1515, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _,
                out var placement);
            world.Writer.PlaceWorldItem(placement);
            var r = new EntityRef();

            r.Item(world, ITEM).Should().BeSameAs(it);
            r.Item(world, ITEM2).Should().BeNull();

            world.Writer.RemoveWorldItem(it, out _);

            r.Item(world, ITEM).Should().BeSameAs(world.FindItem(ITEM));
            (r.Item(world, ITEM) == null || r.Item(world, ITEM).IsDestroyed).Should().BeTrue();
        }

        // gump-handles: GridLootGump and InspectorGump hold their corpse or entity by serial; Entity() is World.Get's answer
        [Fact]
        public void Entity_Returns_What_World_Get_Returns_For_Mobiles_And_Items()
        {
            var world = new World();
            Mobile m = world.GetOrCreateMobile(MOB);
            Item it = world.Writer.UpsertWorldItem(ITEM, 0x2006, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _,
                out var placement);
            world.Writer.PlaceWorldItem(placement);
            var rm = new EntityRef();
            var ri = new EntityRef();

            rm.Entity(world, MOB).Should().BeSameAs(m);
            ri.Entity(world, ITEM).Should().BeSameAs(it);
            ri.Entity(world, ITEM2).Should().BeNull();
        }

        [Fact]
        public void Entity_Of_A_Removed_And_Recreated_Item_Is_The_New_Object()
        {
            var world = new World();
            Item old = world.Writer.UpsertWorldItem(ITEM, 0x2006, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _,
                out var placement);
            world.Writer.PlaceWorldItem(placement);
            var r = new EntityRef();
            r.Entity(world, ITEM).Should().BeSameAs(old);

            world.Writer.RemoveWorldItem(old, out _);

            Entity gone = r.Entity(world, ITEM);
            gone.Should().BeSameAs(world.Get(ITEM));
            (gone == null || gone.IsDestroyed).Should().BeTrue("a holder resolving at use sees the removal");

            world.DropDestroyedItems();
            Item next = world.Writer.UpsertWorldItem(ITEM, 0x2006, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _,
                out var placement2);
            world.Writer.PlaceWorldItem(placement2);

            next.Should().NotBeSameAs(old);
            r.Entity(world, ITEM).Should().BeSameAs(next, "the handle was re-created under the same serial");
        }
    }
}
