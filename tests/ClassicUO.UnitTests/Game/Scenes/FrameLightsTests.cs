using ClassicUO.Game.Scenes;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// The light pass draws the lights of its own frame's world walk (FrameLights, GameScene.Draw): a light is placed at its
    /// world pixel minus that frame's draw offset, the same offset the world is drawn with. The draws below simulate several
    /// draws per update with a camera moving by a different amount each draw (uncapped FPS while walking), the case where the
    /// old order (light pass before the walk, drawing the previous frame's list) put lights a varying distance behind the world.
    /// </summary>
    public sealed class FrameLightsTests
    {
        private const int LampWorldX = 1000, LampWorldY = 700;

        // the per-draw camera offsets while walking: uneven steps, including repeated offsets (more draws than updates)
        private static readonly (int X, int Y)[] Offsets =
        {
            (100, 50), (100, 50), (101, 51), (103, 53), (103, 53), (104, 54), (107, 57), (107, 57), (108, 58)
        };

        private static void Walk(FrameLights lights, int offsetX, int offsetY)
        {
            ref FrameLights.Entry e = ref lights.Next();
            e.ID = 2;
            e.DrawX = LampWorldX - offsetX;
            e.DrawY = LampWorldY - offsetY;
            lights.Commit();
        }

        [Fact]
        public void LightPassAfterTheWalk_DrawsEveryLightAtTheFramesOwnOffset()
        {
            var lights = new FrameLights(100);
            uint frame = 0;

            foreach (var (x, y) in Offsets)
            {
                lights.BeginFrame(++frame);
                Walk(lights, x, y);

                int n = lights.CountFor(frame);
                n.Should().Be(1);
                lights[0].DrawX.Should().Be(LampWorldX - x, "the world is drawn at offset {0} this frame", x);
                lights[0].DrawY.Should().Be(LampWorldY - y);
            }
        }

        [Fact]
        public void PreviousFramesLights_AreNeverDrawn()
        {
            var lights = new FrameLights(100);
            uint frame = 0;
            (int X, int Y) previous = default;

            foreach (var (x, y) in Offsets)
            {
                lights.BeginFrame(++frame);
                // the old order: the light pass ran here, before this frame's walk, with the previous walk's list
                lights.CountFor(frame).Should().Be(0, "a list placed at offset {0} must not be drawn over a world at offset {1}", previous, (x, y));
                Walk(lights, x, y);
                previous = (x, y);
            }

            // a list left from an earlier frame (a held or skipped draw) is not drawn in a later one
            lights.CountFor(frame + 1).Should().Be(0);
        }

        [Fact]
        public void SeveralWalksInOneFrame_ShareTheList_AndTruncateKeepsThePrefix()
        {
            var lights = new FrameLights(4);
            lights.BeginFrame(7);
            Walk(lights, 10, 10);
            int probeBase = lights.Count;
            Walk(lights, 10, 10);
            lights.Truncate(probeBase);
            lights.CountFor(7).Should().Be(1);

            Walk(lights, 10, 10);
            Walk(lights, 10, 10);
            Walk(lights, 10, 10);
            lights.Full.Should().BeTrue();
        }

        // the owner's lamp repro: lamps offered in walk order, the one next to the player turned off and on again
        private static bool[] Lit(FrameLights lights, int lamps, bool nearLampOn, bool ranked)
        {
            lights.BeginFrame(9, ranked ? FrameLights.SafetyBound : 100, ranked);

            for (int i = 0; i < lamps; i++)
            {
                if (i == 0 && !nearLampOn)
                {
                    continue;
                }

                if (lights.Full)
                {
                    lights.Refused++;

                    continue;
                }

                ref FrameLights.Entry e = ref lights.Next();
                e.ID = 1;
                e.DrawX = i * 10;
                e.DrawY = 0;
                lights.Commit(FrameLights.Priority(e.DrawX, 0, 0, 0, 2000, 600, 0, 0));
            }

            var lit = new bool[lamps];

            for (int i = 0; i < lights.CountFor(9); i++)
            {
                lit[lights[i].DrawX / 10] = true;
            }

            return lit;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Toggling_a_lamp_never_changes_another_lamps_light(bool ranked)
        {
            const int lamps = 150;
            var lights = new FrameLights(100);
            bool[] on = Lit(lights, lamps, true, ranked);
            bool[] off = Lit(lights, lamps, false, ranked);
            bool[] onAgain = Lit(lights, lamps, true, ranked);
            int changed = 0;

            for (int i = 1; i < lamps; i++)
            {
                if (on[i] != off[i])
                {
                    changed++;
                }

                on[i].Should().Be(onAgain[i]);
            }

            if (ranked)
            {
                // view_margin: every lamp keeps its light whatever the near one does
                changed.Should().Be(0);
                on.Should().AllBeEquivalentTo(true);
            }
            else
            {
                // the classic cap: lamp 101 takes the slot the near lamp leaves (the owner's repro)
                changed.Should().Be(1);
                off[100].Should().BeTrue();
                on[100].Should().BeFalse();
            }
        }

        [Fact]
        public void Ranked_list_grows_to_its_high_water_mark_and_reuses_it()
        {
            var lights = new FrameLights(100);
            lights.BeginFrame(1, FrameLights.SafetyBound, true);

            for (int i = 0; i < 700; i++)
            {
                lights.Full.Should().BeFalse();
                ref FrameLights.Entry e = ref lights.Next();
                e.DrawX = i;
                lights.Commit(i);
            }

            lights.CountFor(1).Should().Be(700);
            lights.Refused.Should().Be(0);
            int capacity = lights.Capacity;

            lights.BeginFrame(2, FrameLights.SafetyBound, true);

            for (int i = 0; i < 700; i++)
            {
                lights.Next().DrawX = i;
                lights.Commit(i);
            }

            lights.Capacity.Should().Be(capacity, "a frame of the same size allocates nothing");
        }

        [Fact]
        public void Past_the_bound_in_view_and_near_lights_win()
        {
            var lights = new FrameLights(4);
            lights.BeginFrame(1, 3, true);
            // view 0..100 x 0..100, player at 50,50
            Add(lights, 300, 50);   // out of view, far
            Add(lights, 60, 50);    // in view, near
            Add(lights, 90, 90);    // in view
            lights.CountFor(1).Should().Be(3);

            Add(lights, 55, 50);    // in view, nearest: replaces the out-of-view one
            Add(lights, 500, 500);  // out of view: not taken
            lights.Refused.Should().Be(2);

            var xs = new System.Collections.Generic.List<int>();

            for (int i = 0; i < lights.CountFor(1); i++)
            {
                xs.Add(lights[i].DrawX);
            }

            xs.Should().BeEquivalentTo(new[] { 55, 60, 90 });

            static void Add(FrameLights l, int x, int y)
            {
                ref FrameLights.Entry e = ref l.Next();
                e.DrawX = x;
                e.DrawY = y;
                l.Commit(FrameLights.Priority(x, y, 0, 0, 100, 100, 50, 50));
            }
        }
    }
}
