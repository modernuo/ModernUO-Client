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
    }
}
