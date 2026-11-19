using ClassicUO.Game.UI.Gumps;
using ClassicUO.Loop;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    /// <summary>LoopDue is static: its users run one at a time.</summary>
    [CollectionDefinition("LoopDue", DisableParallelization = true)]
    public sealed class LoopDueCollection
    {
    }

    /// <summary>
    /// worldmap-audit P-1 (worldmap_idle_loop): with the switch off an open world map keeps the loop polling every Update;
    /// on, it polls only while a map image loads and otherwise leaves the party/guild query and the entity prune as
    /// deadlines. LoopDue is static: these tests share a non-parallel collection with LoopIdleTests.
    /// </summary>
    [Collection("LoopDue")]
    public sealed class WorldMapIdleLoopTests
    {
        [Theory]
        [InlineData(false, false, 1000L, false, 2000L)]
        [InlineData(false, true, 1000L, true, 2000L)]
        public void Switch_Off_Polls_Every_Update(bool loadPending, bool queryEnabled, long queryTick, bool hasEntities, long pruneTick)
        {
            LoopDue.Begin();

            WorldMapGump.NoteLoopDue(false, loadPending, queryEnabled, queryTick, hasEntities, pruneTick);

            LoopDue.Busy.Should().Be(LoopDueSource.WorldMap);
            LoopDue.NextTick.Should().Be(long.MaxValue);
        }

        [Fact]
        public void Switch_On_Polls_While_A_Load_Is_Pending()
        {
            LoopDue.Begin();

            WorldMapGump.NoteLoopDue(true, true, true, 1000, true, 2000);

            LoopDue.Busy.Should().Be(LoopDueSource.WorldMap);
        }

        [Fact]
        public void Switch_On_Without_A_Load_Leaves_The_Query_And_Prune_As_Deadlines()
        {
            LoopDue.Begin();

            WorldMapGump.NoteLoopDue(true, false, true, 1500, true, 1200);

            LoopDue.Busy.Should().Be(LoopDueSource.None);
            LoopDue.NextTick.Should().Be(1200);
            LoopDue.NextSource.Should().Be(LoopDueSource.WorldMap);

            LoopDue.Begin();

            WorldMapGump.NoteLoopDue(true, false, true, 1500, false, 1200);

            LoopDue.NextTick.Should().Be(1500, "no entities: no prune deadline");
        }

        [Fact]
        public void Switch_On_With_Nothing_To_Do_Adds_Nothing()
        {
            LoopDue.Begin();

            WorldMapGump.NoteLoopDue(true, false, false, 1500, false, 1200);

            LoopDue.Busy.Should().Be(LoopDueSource.None);
            LoopDue.NextTick.Should().Be(long.MaxValue);
        }

#if CUO_DEVTOOLS
        [Fact]
        public void Checker_Flags_Late_Work_And_Untracked_Changes_Only()
        {
            WorldMapIdleCheck.Late(true, 16 + WorldMapIdleCheck.SlackMs + 0.5, 16).Should().BeTrue();
            WorldMapIdleCheck.Late(true, 16, 16).Should().BeFalse("within the old cadence");
            WorldMapIdleCheck.Late(false, 100, 16).Should().BeFalse("due before the wait: a long frame, not the wait");

            WorldMapIdleCheck.UntrackedChange(true, true, true, false).Should().BeTrue();
            WorldMapIdleCheck.UntrackedChange(true, true, true, true).Should().BeFalse("the wait was planned for the map");
            WorldMapIdleCheck.UntrackedChange(true, true, false, false).Should().BeFalse("a packet or input woke the loop");
            WorldMapIdleCheck.UntrackedChange(true, false, true, false).Should().BeFalse("no idle gap");
            WorldMapIdleCheck.UntrackedChange(false, true, true, false).Should().BeFalse();
        }
#endif
    }
}
