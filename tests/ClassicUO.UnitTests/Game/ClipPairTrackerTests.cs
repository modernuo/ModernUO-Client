using ClassicUO.Game.UI.Controls;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    public class ClipPairTrackerTests
    {
        [Fact]
        public void PushedBeginPairsWithEnd()
        {
            ClipPairTracker.Reset();
            ClipPairTracker.Begin(true);
            Assert.True(ClipPairTracker.End());
        }

        [Fact]
        public void FailedBeginSkipsItsEnd()
        {
            ClipPairTracker.Reset();
            ClipPairTracker.Begin(false);
            Assert.False(ClipPairTracker.End());
            Assert.True(ClipPairTracker.End());
        }

        [Fact]
        public void NestedFailedInsidePushed()
        {
            ClipPairTracker.Reset();
            ClipPairTracker.Begin(true);
            ClipPairTracker.Begin(false);
            Assert.False(ClipPairTracker.End());
            Assert.True(ClipPairTracker.End());
        }
    }
}
