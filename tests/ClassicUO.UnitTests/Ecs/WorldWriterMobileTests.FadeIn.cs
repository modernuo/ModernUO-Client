using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // mobile_fade_in (mount-fix): off, a mobile the server creates is drawn at its target alpha from its first walk; on, it
    // starts at 0 and fades in. An update of a mobile that already exists never changes its alpha.
    public partial class WorldWriterMobileTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Created_Mobile_Snaps_To_Its_Target_Alpha_Only_With_Fade_In_Off(bool fadeIn)
        {
            MobileFadeIn.OverrideForTests = fadeIn;

            try
            {
                World world = BuildPlayer(out _);
                var p = new MobPacket { Serial = NEWMOB, Graphic = 0xC8, X = 1000, Y = 1000, Z = 0, Direction = Direction.North };
                Mobile m = NewUpsertMobile(world, in p);

                Assert.Equal(0, m.AlphaHue);
                Assert.Equal(!fadeIn, m.AlphaSnap);

                // an update of the existing mobile is not a creation
                m.AlphaSnap = false;
                p.X = 1001;
                NewUpsertMobile(world, in p);
                Assert.False(m.AlphaSnap);
            }
            finally
            {
                MobileFadeIn.OverrideForTests = null;
            }
        }
    }
}
