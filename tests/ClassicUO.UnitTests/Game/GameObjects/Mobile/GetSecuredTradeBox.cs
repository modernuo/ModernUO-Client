using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using Xunit;

namespace ClassicUO.UnitTests.Game.GameObjects.Mobile
{
    // The mobiles here are built outside any World table with hand-linked child lists. An instance the World doesn't hold
    // reads as a leaf (T2), so these only check the empty answer; WorldStateReaderTests covers GetSecureTradeBox on a World
    // that holds its objects.
    public class GetSecuredTradeBox
    {
        [Fact]
        public void GetSecureTradeBox_Returns_Null_Without_Items()
        {
            var world = new World();
            var sut = new ClassicUO.Game.GameObjects.Mobile(world);

            Assert.Null(sut.GetSecureTradeBox());

            world.Clear();
        }

        [Fact]
        public void GetSecureTradeBox_Returns_Null_With_Irrelevant_Items()
        {
            var world = new World();

            var irrelevantItem1 = new Item(world);
            var irrelevantItem2 = new Item(world);

            var sut = new ClassicUO.Game.GameObjects.Mobile(world);

            Assert.Null(sut.GetSecureTradeBox());

            world.Clear();
        }
    }
}
