using ClassicUO.Game;
using ClassicUO.Game.Data;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // anim_async_turn: each world has its own note queue, so a turn written in one world is never drained by another
    // (the static queue it replaces let parallel test worlds drain each other's notes).
    public partial class WorldWriterMobileTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Async_Turn_Notes_Stay_In_The_Writers_World(bool on)
        {
            AnimAsyncTurn.OverrideForTests = on;

            try
            {
                World a = BuildPlayer(out TestPlayer pa);
                World b = BuildPlayer(out _);

                a.Writer.SetMobileDirection(pa, Direction.West);

                Assert.Equal(on ? 1 : 0, a.AsyncTurn.Count);
                Assert.Equal(0, b.AsyncTurn.Count);

                Assert.Equal(on, a.AsyncTurn.TryTake(out uint serial));
                Assert.Equal(on ? pa.Serial : 0u, serial);
            }
            finally
            {
                AnimAsyncTurn.OverrideForTests = null;
            }
        }
    }
}
