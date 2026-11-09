using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    // W5: World.Update's active item list and the hits percentage computed on change.
    // Destroying entities and the hits text need the running client (GameActions, RenderedText); the in-client
    // checker CUO_W5_CHECK covers those paths.
    public class WorldUpdateActiveItemsTests
    {
        [Theory]
        [InlineData(0, 100, 0)]
        [InlineData(50, 100, 50)]
        [InlineData(100, 100, 100)]
        [InlineData(150, 100, 100)]
        [InlineData(1, 3, 33)]
        [InlineData(2, 3, 66)]
        [InlineData(25, 25, 100)]
        public void HitsPercent_Matches_The_Per_Frame_Rule(int hits, int max, int expected)
        {
            Assert.Equal((byte)expected, Entity.HitsPercent((ushort)hits, (ushort)max));
        }

        [Fact]
        public void Only_Corpses_And_Opened_Handles_Need_A_Frame_Update()
        {
            var world = new World();
            Item item = world.GetOrCreateItem(0x40000001);
            item.Graphic = 0x0EED;
            Assert.False(item.NeedsFrameUpdate);

            item.ObjectHandlesStatus = ObjectHandlesStatus.OPEN;
            Assert.True(item.NeedsFrameUpdate);
            item.ObjectHandlesStatus = ObjectHandlesStatus.DISPLAYING;
            Assert.False(item.NeedsFrameUpdate);
            item.ObjectHandlesStatus = ObjectHandlesStatus.CLOSED;
            Assert.False(item.NeedsFrameUpdate);

            item.Graphic = 0x2006;
            Assert.True(item.NeedsFrameUpdate);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)] // ecs_active_items_source (default on): membership is the item's ActiveItemEntry
        public void QueueItemUpdate_Lists_An_Item_Once(bool source)
        {
            var world = new World { ActiveItemsSourceOverride = source };
            Item a = world.GetOrCreateItem(0x40000002);
            Item b = world.GetOrCreateItem(0x40000003);

            world.QueueItemUpdate(a);
            world.QueueItemUpdate(a);
            world.QueueItemUpdate(b);
            Assert.True(world.IsItemListed(a));
            Assert.True(world.IsItemListed(b));
            Assert.Equal(2, world.ActiveItemCount);
        }

        [Fact]
        public void DropDestroyedItems_Without_Destroyed_Items_Keeps_Items()
        {
            var world = new World();
            Item a = world.GetOrCreateItem(0x40000004);

            Assert.Equal(0, world.DropDestroyedItems());
            Assert.Same(a, world.Items[a.Serial]);
        }
    }
}
