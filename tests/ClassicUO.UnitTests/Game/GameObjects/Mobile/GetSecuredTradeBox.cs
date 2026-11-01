using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using Xunit;

namespace ClassicUO.UnitTests.Game.GameObjects.Mobile
{
    // The mobiles here are built outside any World table with hand-linked child lists, so they test the legacy list path.
    // With ecs_read_contents on, an instance the World doesn't hold reads as a leaf (T2); WorldStateReaderTests covers
    // GetSecureTradeBox both ways on a World that holds its objects.
    public class GetSecuredTradeBox
    {
        [Fact]
        public void GetSecureTradeBox_Returns_Null_Without_Items()
        {
            var world = new World();
            world.State.ReadContentsOverride = false; // a mobile no World table holds: the list path (see the class comment)
            var sut = new ClassicUO.Game.GameObjects.Mobile(world);

            Assert.Null(sut.GetSecureTradeBox());

            world.Clear();
        }

        [Fact]
        public void GetSecureTradeBox_Returns_Null_With_Irrelevant_Items()
        {
            var world = new World();
            world.State.ReadContentsOverride = false; // a mobile no World table holds: the list path (see the class comment)

            var irrelevantItem1 = new Item(world);
            var irrelevantItem2 = new Item(world);

            var sut = new ClassicUO.Game.GameObjects.Mobile(world);

            sut.Insert(sut.Items, irrelevantItem1);
            sut.Insert(sut.Items, irrelevantItem2);

            Assert.Null(sut.GetSecureTradeBox());

            world.Clear();
        }

        [Fact]
        public void GetSecureTradeBox_Returns_Only_Relevant_Item()
        {
            var world = new World();
            world.State.ReadContentsOverride = false; // a mobile no World table holds: the list path (see the class comment)

            var irrelevantItem1 = new Item(world);
            var irrelevantItem2 = new Item(world);

            var relevantItem = new Item(world)
            {
                Graphic = 0x1E5E,
                Layer = 0
            };

            var sut = new ClassicUO.Game.GameObjects.Mobile(world);

            sut.Insert(sut.Items, irrelevantItem1);
            sut.Insert(sut.Items, relevantItem);
            sut.Insert(sut.Items, irrelevantItem2);

            Assert.Same(relevantItem, sut.GetSecureTradeBox());

            world.Clear();
        }
    }
}
