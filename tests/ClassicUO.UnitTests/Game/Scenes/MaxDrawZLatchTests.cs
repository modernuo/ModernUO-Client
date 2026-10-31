using ClassicUO.Game.Scenes;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    // GameScene.UpdateMaxDrawZ's recompute rule (known-issues "the roof over the player is sometimes drawn"): a teleport
    // whose first frame comes before the player's chunk loads must not keep that frame's "not under a roof" answer.
    public class MaxDrawZLatchTests
    {
        [Fact]
        public void First_Call_Computes()
        {
            var latch = new MaxDrawZLatch();

            latch.NeedsUpdate(0, 0, 0, false).Should().BeTrue();
        }

        [Fact]
        public void Same_Position_With_Chunks_Loaded_Is_Kept()
        {
            var latch = new MaxDrawZLatch();
            latch.Record(1427, 1691, 0, complete: true);

            latch.NeedsUpdate(1427, 1691, 0, false).Should().BeFalse();
            latch.NeedsUpdate(1427, 1691, 0, true).Should().BeTrue("a forced update (a house multi arrives, DrawRoofs changes) always runs");
        }

        [Fact]
        public void Teleport_Before_The_Chunk_Loads_Recomputes_Once_It_Is_There()
        {
            // the failing order: Britain (chunks loaded), then a teleport into the house; the first frame's decision runs
            // before Map.UpdateLoading has loaded the player's chunk
            var latch = new MaxDrawZLatch();
            latch.Record(1495, 1629, 10, complete: true);

            latch.NeedsUpdate(1427, 1691, 0, false).Should().BeTrue("the player moved");
            latch.Record(1427, 1691, 0, complete: false); // chunk missing: max Z 127, roofs drawn

            // the old rule (position only) stopped here, for as long as the player stood still
            latch.NeedsUpdate(1427, 1691, 0, false).Should().BeTrue("the last decision read a chunk that was not loaded");
            latch.Record(1427, 1691, 0, complete: false);
            latch.IncompleteFrames.Should().Be(2);
            latch.IsIncomplete.Should().BeTrue();

            // the chunk loads (UpdateLoading, or a synchronous loader in the walk): the decision sees the roof and is kept
            latch.NeedsUpdate(1427, 1691, 0, false).Should().BeTrue();
            latch.Record(1427, 1691, 0, complete: true);
            latch.IsIncomplete.Should().BeFalse();
            latch.IncompleteFrames.Should().Be(0);
            latch.NeedsUpdate(1427, 1691, 0, false).Should().BeFalse();
        }

        [Fact]
        public void Z_Change_Recomputes()
        {
            var latch = new MaxDrawZLatch();
            latch.Record(10, 10, 0, complete: true);

            latch.NeedsUpdate(10, 10, 20, false).Should().BeTrue();
        }
    }
}
