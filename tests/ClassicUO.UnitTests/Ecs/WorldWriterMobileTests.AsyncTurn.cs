using ClassicUO.Game;
using ClassicUO.Game.Data;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // The turn prefetch: each world has its own note queue, so a turn written in one world is never drained by another
    // (the static queue it replaces let parallel test worlds drain each other's notes).
    public partial class WorldWriterMobileTests
    {
        [Fact]
        public void Async_Turn_Notes_Stay_In_The_Writers_World()
        {
            World a = BuildPlayer(out TestPlayer pa);
            World b = BuildPlayer(out _);

            a.Writer.SetMobileDirection(pa, Direction.West);

            Assert.Equal(1, a.AsyncTurn.Count);
            Assert.Equal(0, b.AsyncTurn.Count);

            Assert.True(a.AsyncTurn.TryTake(out uint serial));
            Assert.Equal(pa.Serial, serial);
        }
    }
}
