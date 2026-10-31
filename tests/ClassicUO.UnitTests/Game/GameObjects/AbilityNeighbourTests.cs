using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.GameObjects
{
    // UpdateAbilities looks for a weapon's neighbouring graphic with the same AnimID (the other facing of the weapon).
    // The next-entry lookup must not index past a 0x10000-entry static table when the weapon is graphic 0xFFFF.
    public class AbilityNeighbourTests
    {
        private static StaticTiles[] Table()
        {
            var statics = new StaticTiles[0x10000];
            statics[0x0F00].AnimID = 7;
            statics[0x0F01].AnimID = 7;
            statics[0x0F02].AnimID = 7;
            statics[0xFFFE].AnimID = 1;
            statics[0xFFFF].AnimID = 2;
            return statics;
        }

        [Fact]
        public void Last_Graphic_With_A_Different_Previous_AnimID_Returns_Zero_Without_Throwing()
        {
            var statics = Table();

            PlayerMobile.FindAnimNeighbour(statics, 0xFFFF, statics[0xFFFF].AnimID).Should().Be(0);
        }

        [Fact]
        public void Last_Graphic_Still_Matches_Its_Previous_Entry()
        {
            var statics = Table();
            statics[0xFFFE].AnimID = 2;

            PlayerMobile.FindAnimNeighbour(statics, 0xFFFF, 2).Should().Be(0xFFFE);
        }

        [Fact]
        public void Previous_Entry_Wins_Over_Next_Entry()
        {
            PlayerMobile.FindAnimNeighbour(Table(), 0x0F01, 7).Should().Be(0x0F00);
        }

        [Fact]
        public void Next_Entry_Is_Used_When_Previous_Differs()
        {
            PlayerMobile.FindAnimNeighbour(Table(), 0x0F00, 7).Should().Be(0x0F01);
        }

        [Fact]
        public void Graphic_Zero_Has_No_Previous_Entry()
        {
            var statics = Table();
            statics[1].AnimID = 3;

            PlayerMobile.FindAnimNeighbour(statics, 0, 3).Should().Be(1);
        }
    }
}
