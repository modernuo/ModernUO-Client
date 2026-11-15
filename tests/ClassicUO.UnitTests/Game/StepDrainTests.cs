using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    // step_drain's pure rules: the learned pace (StepDrain.NoteArrival, Cadence) and the front step's duration
    public class StepDrainTests
    {
        private static StepCadence Learn(params int[] intervals)
        {
            StepCadence c = default;
            uint t = 1000;
            StepDrain.NoteArrival(ref c, t, true, false, false);

            foreach (int i in intervals)
            {
                t += (uint)i;
                StepDrain.NoteArrival(ref c, t, true, false, false);
            }

            return c;
        }

        [Fact]
        public void Nothing_Is_Learned_Before_Five_Samples()
        {
            StepDrain.Cadence(Learn(200, 200, 200, 200)).Should().Be(0);
            StepDrain.Cadence(Learn(200, 200, 200, 200, 200)).Should().Be(200);
        }

        [Fact]
        public void One_Short_Interval_Does_Not_Speed_It_Up()
        {
            StepDrain.Cadence(Learn(400, 400, 400, 400, 60)).Should().Be(400);
            StepDrain.Cadence(Learn(400, 400, 400, 400, 400, 400, 400, 100)).Should().Be(400);
        }

        [Fact]
        public void A_Sustained_Faster_Pace_Is_Learned()
        {
            StepDrain.Cadence(Learn(400, 400, 400, 400, 400, 200, 200, 200, 200)).Should().BeGreaterOrEqualTo(300);
            StepDrain.Cadence(Learn(400, 400, 400, 400, 400, 200, 200, 200, 200, 200)).Should().BeLessThan(300).And.BeGreaterOrEqualTo(200);
            StepDrain.Cadence(Learn(400, 400, 400, 400, 400, 200, 200, 200, 200, 200, 200, 200, 200)).Should().Be(200);
        }

        [Fact]
        public void Same_Tick_Bursts_And_Turn_Adjacent_Intervals_Are_Not_Samples()
        {
            StepCadence c = Learn(100, 100, 100, 100, 100);
            c.Count.Should().Be(5);
            uint t = c.LastMove;

            // a burst: two moves in one tick
            StepDrain.NoteArrival(ref c, t + 5, true, false, false);
            c.Count.Should().Be(5);

            // a turn, then a move 10 ms later: the turn-adjacent move is no sample either; the next move's sample spans both
            // folded moves from the anchor (as if they were no moves: never a faster pace)
            StepDrain.NoteArrival(ref c, t + 60, false, false, false);
            StepDrain.NoteArrival(ref c, t + 70, true, false, false);
            c.Count.Should().Be(5);
            c.Anchor.Should().Be(t);
            StepDrain.NoteArrival(ref c, t + 170, true, false, false);
            c.Count.Should().Be(6);
            c.Anchor.Should().Be(t + 170);
            StepDrain.Cadence(in c).Should().Be((5 * 100 + 170) / 6);
        }

        [Fact]
        public void A_Pause_Or_A_Mode_Change_Restarts_The_Samples()
        {
            StepCadence c = Learn(200, 200, 200, 200);
            StepDrain.NoteArrival(ref c, c.LastMove + 1500, true, false, false);
            c.Count.Should().Be(0);
            StepDrain.Cadence(in c).Should().Be(0);

            c = Learn(200, 200, 200, 200);
            StepDrain.NoteArrival(ref c, c.LastMove + 200, true, true, false);
            c.Count.Should().Be(0);
            c.Mode.Should().Be(StepDrain.Mode(true, false));
        }

        [Theory]
        // not learned: the table
        [InlineData(400, 0, 0, 0, 400, false)]
        // learned and clearly faster: the pace; slower or near the table: the table
        [InlineData(400, 200, 0, 0, 200, false)]
        [InlineData(400, 600, 0, 0, 400, false)]
        [InlineData(100, 90, 0, 0, 100, false)]
        [InlineData(100, 75, 0, 0, 100, false)]
        [InlineData(100, 65, 0, 0, 65, false)]
        [InlineData(200, 120, 0, 0, 120, false)]
        // a move behind the front: drained to 3/4
        [InlineData(100, 0, 1, 0, 75, true)]
        [InlineData(400, 200, 2, 50, 150, true)]
        // never below 50 ms
        [InlineData(200, 30, 0, 0, 50, false)]
        [InlineData(200, 60, 1, 0, 50, true)]
        public void The_Front_Duration(int table, int cadence, int behind, int since, int expected, bool drained)
        {
            StepDrain.Duration(table, cadence, behind, since, out bool d).Should().Be(expected);
            d.Should().Be(drained);
        }

        [Fact]
        public void Duration_Zero_Is_The_Old_Decision()
        {
            // Duration 0 is the old timing: the table less the frame delay
            StepInput i = default;
            i.Front = new StepEntry { X = 11, Y = 10, Direction = (byte)Direction.East };
            i.X = 10;
            i.Y = 10;
            i.Count = 1;
            i.LastStepTime = 1000;
            StepRules.Decide(in i, 1000 + 400 - 16, 16, out StepDecision a);
            a.Remove.Should().BeTrue();
            StepRules.Decide(in i, 1000 + 400 - 17, 16, out a);
            a.Remove.Should().BeFalse();

            // a fixed duration: exactly that long, no frame subtracted
            i.Duration = 200;
            StepRules.Decide(in i, 1000 + 199, 16, out a);
            a.Remove.Should().BeFalse();
            StepRules.Decide(in i, 1000 + 200, 16, out a);
            a.Remove.Should().BeTrue();
        }

        [Fact]
        public void The_Next_Step_Starts_Where_It_Was_Due_At_Most_A_Frame_Back()
        {
            StepDrain.NextStepTime(1000, 200, 1210, 16).Should().Be(1200u);
            StepDrain.NextStepTime(1000, 200, 1300, 16).Should().Be(1284u);
            StepDrain.NextStepTime(1000, 200, 1200, 16).Should().Be(1200u);
        }
    }
}
