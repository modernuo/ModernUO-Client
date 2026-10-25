// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Scenes;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary><see cref="WorldSettleGate"/>: hold the last world frame only after a discontinuity, until ready or timeout.</summary>
    public class WorldSettleGateTests
    {
        private static readonly object PassA = new object(), PassB = new object();

        // one frame: Begin, then the world pass's readiness after the render (or stream)
        private static bool Frame(WorldSettleGate g, double now, double camX, double camY, bool ready, object pass = null, int facet = 0, bool kept = true)
        {
            bool hold = g.Begin(now, pass ?? PassA, facet, camX, camY, kept);
            g.End(ready);

            return hold;
        }

        // past the entry episode, settled at (0, 0)
        private static WorldSettleGate Settled(double now = 0)
        {
            var g = new WorldSettleGate();
            Frame(g, now, 0, 0, true);
            Frame(g, now + 16, 0, 0, true).Should().BeFalse();
            g.Episode.Should().Be(WorldDiscontinuity.None);
            g.Stats = default;

            return g;
        }

        [Fact]
        public void Entry_IsBlackUntilReady_ThenFadesIn()
        {
            var g = new WorldSettleGate();

            Frame(g, 0, 0, 0, false).Should().BeTrue();
            g.Episode.Should().Be(WorldDiscontinuity.Entry);
            g.Fade.Should().Be(0f);
            Frame(g, 16, 0, 0, true).Should().BeTrue("readiness is known only after a frame streamed");
            g.Fade.Should().Be(0f);

            Frame(g, 32, 0, 0, true).Should().BeFalse();
            g.Fade.Should().Be(0f, "the fade starts at the release");
            Frame(g, 32 + g.FadeMs / 2, 0, 0, true).Should().BeFalse();
            g.Fade.Should().BeApproximately(0.5f, 0.01f);
            Frame(g, 32 + g.FadeMs, 0, 0, true);
            g.Fade.Should().Be(1f);

            g.Stats.Held.Should().Be(2);
            g.Stats.HeldBlack.Should().Be(2);
            g.Stats.PresentedUnsettled.Should().Be(0);
        }

        [Fact]
        public void Walking_NeverHolds_AndCountsUnsettledFramesAsSteady()
        {
            var g = Settled();
            double now = 100;

            // a tile every 4 frames, both axes (22 px), and a 3-tile hitch (132 px)
            for (int i = 0; i < 40; i++)
            {
                Frame(g, now += 16, i / 4 * 22, i / 4 * 22, i % 10 != 0).Should().BeFalse();
            }

            Frame(g, now += 16, 220 + 132, 220, true).Should().BeFalse();

            g.Stats.Holds.Should().Be(0);
            g.Stats.PresentedUnsettled.Should().Be(4);
            g.Stats.PresentedUnsettledSteady.Should().Be(4);
        }

        [Fact]
        public void Teleport_HoldsTheLastFrame_UntilReady()
        {
            var g = Settled();

            Frame(g, 100, 5000, 3000, false).Should().BeTrue();
            g.Episode.Should().Be(WorldDiscontinuity.Teleport);
            g.Fade.Should().Be(1f, "a frame is held, not black");
            Frame(g, 116, 5000, 3000, false).Should().BeTrue();
            Frame(g, 132, 5000, 3000, true).Should().BeTrue();
            Frame(g, 148, 5000, 3000, true).Should().BeFalse();

            g.Stats.Held.Should().Be(3);
            g.Stats.HeldBlack.Should().Be(0);
            g.Stats.PresentedUnsettled.Should().Be(0);
            g.Stats.HoldMsMax.Should().Be(48);
        }

        [Fact]
        public void JumpThreshold_IsEightTilesOnEitherAxis()
        {
            var g = Settled();

            Frame(g, 100, WorldSettleGate.DefaultJumpPixels, 0, true).Should().BeFalse();
            Frame(g, 116, WorldSettleGate.DefaultJumpPixels, -WorldSettleGate.DefaultJumpPixels - 1, true).Should().BeTrue();
        }

        [Fact]
        public void Timeout_PresentsAnyway_AndCountsTheUnsettledFrames()
        {
            var g = Settled();
            double now = 100;

            Frame(g, now, 9000, 9000, false).Should().BeTrue();

            while (now < 100 + g.TimeoutMs - 16)
            {
                Frame(g, now += 16, 9000, 9000, false).Should().BeTrue();
            }

            Frame(g, 100 + g.TimeoutMs, 9000, 9000, false).Should().BeFalse("the client never freezes");
            Frame(g, 116 + g.TimeoutMs, 9000, 9000, false).Should().BeFalse();
            Frame(g, 132 + g.TimeoutMs, 9000, 9000, true).Should().BeFalse();
            Frame(g, 148 + g.TimeoutMs, 9000, 9000, false).Should().BeFalse();

            g.Stats.Timeouts.Should().Be(1);
            g.Stats.PresentedUnsettledAfterTimeout.Should().Be(2);
            g.Stats.PresentedUnsettledSteady.Should().Be(1, "after the first ready frame it's walking again");
        }

        [Fact]
        public void SecondTeleportDuringAHold_KeepsTheTimeoutFromTheFirst()
        {
            var g = Settled();

            Frame(g, 100, 5000, 0, true).Should().BeTrue();
            Frame(g, 116, 9000, 0, false).Should().BeTrue("the ready seen before the second jump no longer counts");
            Frame(g, 100 + g.TimeoutMs, 9000, 0, false).Should().BeFalse();
            g.Stats.Holds.Should().Be(1, "one episode");
            g.Stats.Timeouts.Should().Be(1);
        }

        [Fact]
        public void FacetChange_AndRecreatedPass_AreDiscontinuities()
        {
            var g = Settled();

            Frame(g, 100, 0, 0, false, PassB, facet: 1).Should().BeTrue();
            g.Episode.Should().Be(WorldDiscontinuity.Facet);
            Frame(g, 116, 0, 0, true, PassB, facet: 1).Should().BeTrue();
            Frame(g, 132, 0, 0, true, PassB, facet: 1).Should().BeFalse();

            Frame(g, 148, 0, 0, false, PassA, facet: 1).Should().BeTrue();
            g.Episode.Should().Be(WorldDiscontinuity.Recreated);
        }

        [Fact]
        public void RecreatedTarget_DuringAHold_GoesBlack()
        {
            var g = Settled();

            Frame(g, 100, 0, 0, false, PassB).Should().BeTrue();
            g.Fade.Should().Be(1f);
            Frame(g, 116, 0, 0, false, PassB, kept: false).Should().BeTrue();
            g.Fade.Should().Be(0f, "the held frame was in the old target");
        }

        [Fact]
        public void RecreatedTarget_WithoutADiscontinuity_DoesNotHold()
        {
            var g = Settled();

            Frame(g, 16 + g.FadeMs, 0, 0, true, kept: false).Should().BeFalse("a resize presents directly");
            g.Fade.Should().Be(1f);
        }

        [Fact]
        public void BackFromLegacy_HoldsLegacysLastFrame()
        {
            var g = Settled();

            g.OnOtherRenderer();
            g.OnOtherRenderer();
            Frame(g, 100, 0, 0, false).Should().BeTrue();
            g.Episode.Should().Be(WorldDiscontinuity.Resume);
            g.Fade.Should().Be(1f);
            Frame(g, 116, 0, 0, true).Should().BeTrue();
            Frame(g, 132, 0, 0, true).Should().BeFalse();
            g.Stats.LegacyFrames.Should().Be(2);
        }

        [Fact]
        public void ObserveOnly_NeverHolds_AndCountsEpisodeFrames()
        {
            var g = new WorldSettleGate { HoldEnabled = false };
            WorldDiscontinuity endedKind = WorldDiscontinuity.None;
            int endedUnsettled = -1;
            g.EpisodeEnded += (kind, ms, held, unsettled, timedOut) => { endedKind = kind; endedUnsettled = unsettled; };

            Frame(g, 0, 0, 0, true).Should().BeFalse();
            Frame(g, 16, 0, 0, true).Should().BeFalse();
            Frame(g, 32, 5000, 0, false).Should().BeFalse();
            Frame(g, 48, 5000, 0, false).Should().BeFalse();
            Frame(g, 64, 5000, 0, true).Should().BeFalse();
            Frame(g, 80, 5000, 0, true).Should().BeFalse();

            endedKind.Should().Be(WorldDiscontinuity.Teleport);
            endedUnsettled.Should().Be(2);
            g.Stats.Held.Should().Be(0);
            g.Stats.PresentedUnsettled.Should().Be(2);
            g.Stats.PresentedUnsettledSteady.Should().Be(0);
        }
    }
}
