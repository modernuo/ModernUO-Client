using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
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
                var p = new MobPacket { Serial = MOB, Graphic = 0xC8, X = 1000, Y = 1000, Z = 0, Direction = Direction.North };
                Mobile m = NewUpsertMobile(world, in p);

                Assert.Equal(0, m.AlphaHue);
                Assert.Equal(!fadeIn, m.AlphaSnap);

                // the first walk: a snap reaches the target in one call, a fade step moves 25
                byte alpha = m.AlphaHue;
                GameScene.CalculateAlpha(ref alpha, 0xFF, m.AlphaSnap);
                Assert.Equal(fadeIn ? 25 : 0xFF, alpha);

                // above the max Z the target is 0: a snapped mobile is not drawn there at all
                byte above = 0;
                Assert.False(GameScene.CalculateAlpha(ref above, 0, snap: true));
                Assert.Equal(0, above);

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
