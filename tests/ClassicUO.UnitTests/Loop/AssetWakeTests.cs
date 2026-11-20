using System;
using System.Diagnostics;
using ClassicUO.Loop;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Loop
{
    // AssetWake is static: the tests share one collection so they never run beside each other
    [Collection("AssetWake")]
    public class AssetWakeTests : IDisposable
    {
        private sealed class CountingWaiter : LoopWaiter
        {
            public int Wakes;
            public override bool Ready => true;
            public override bool HighResolution => true;
            public override string Name => "test";
            public override void BindNet(NetClient net) { }
            public override WakeReason Wait(double ms) => WakeReason.Timer;
            public override void Wake() => Wakes++;
            public override void ClearWake() { }
            public override void Dispose() { }
        }

        private readonly CountingWaiter _waiter = new CountingWaiter();

        public AssetWakeTests()
        {
            AssetWake.ResetForTests();
            AssetWake.SetWaiter(_waiter);
        }

        public void Dispose()
        {
            AssetWake.OverrideForTests = null;
            AssetWake.ResetForTests();
        }

        private static void Frame(bool incomplete, long tick)
        {
            AssetWake.BeginDraw();
            AssetWake.EndDraw(incomplete, tick);
        }

        [Fact]
        public void Off_IncompleteFrame_CompletionNeitherWakesNorPresents()
        {
            AssetWake.OverrideForTests = false;
            Frame(true, 1000);

            ClassicUO.Renderer.AssetSignal.Published();
            Uo.Render.WorkerPool.JobCompleted?.Invoke();

            _waiter.Wakes.Should().Be(0);
            AssetWake.ChangeDue.Should().BeFalse();
            AssetWake.FallbackTick.Should().Be(long.MaxValue);
        }

        [Fact]
        public void On_IncompleteFrame_DecodeCompletionWakesAndIsAChange()
        {
            AssetWake.OverrideForTests = true;
            Frame(true, 1000);
            AssetWake.ChangeDue.Should().BeFalse();
            AssetWake.FallbackTick.Should().Be(1000 + AssetWake.FallbackMs);

            ClassicUO.Renderer.AssetSignal.Published();

            // other test classes' decoders may publish too (parallel runs): at least this one woke
            _waiter.Wakes.Should().BeGreaterOrEqualTo(1);
            AssetWake.ChangeDue.Should().BeTrue();
        }

        [Fact]
        public void On_IncompleteFrame_GpuWorkerCompletionWakes()
        {
            AssetWake.OverrideForTests = true;
            Frame(true, 1000);

            Uo.Render.WorkerPool.JobCompleted.Should().NotBeNull();
            Uo.Render.WorkerPool.JobCompleted();

            _waiter.Wakes.Should().BeGreaterOrEqualTo(1);
            AssetWake.ChangeDue.Should().BeTrue();
        }

        [Fact]
        public void On_CompleteFrame_CompletionChangesNothing()
        {
            AssetWake.OverrideForTests = true;
            Frame(false, 1000);

            ClassicUO.Renderer.AssetSignal.Published();

            _waiter.Wakes.Should().Be(0);
            AssetWake.ChangeDue.Should().BeFalse();
            AssetWake.FallbackTick.Should().Be(long.MaxValue);
        }

        [Fact]
        public void On_NextDrawTakesTheCompletionAndReportsItsTime()
        {
            AssetWake.OverrideForTests = true;
            Frame(true, 1000);
            long before = Stopwatch.GetTimestamp();
            ClassicUO.Renderer.AssetSignal.Published();

            long signalTs = AssetWake.BeginDraw();
            signalTs.Should().BeGreaterOrEqualTo(before);
            AssetWake.EndDraw(false, 1016);

            AssetWake.LastIncomplete.Should().BeFalse();
        }

        [Fact]
        public void On_CompletionDuringTheIncompleteDraw_IsStillAChangeWithoutAWake()
        {
            AssetWake.OverrideForTests = true;
            AssetWake.BeginDraw();
            ClassicUO.Renderer.AssetSignal.Published(); // not armed yet: no wake, but the loop sees the count before it waits
            AssetWake.EndDraw(true, 1000);

            AssetWake.ChangeDue.Should().BeTrue();
        }

#if CUO_DEVTOOLS
        [Fact]
        public void Check_LateOnlyPastOneIntervalPlusSlack()
        {
            long f = Stopwatch.Frequency;
            AssetWakeCheck.IsLate(0, f, 16.7, f).Should().BeFalse();
            AssetWakeCheck.IsLate(f, f + f * 18 / 1000, 16.7, f).Should().BeFalse();
            AssetWakeCheck.IsLate(f, f + f * 19 / 1000, 16.7, f).Should().BeTrue();
        }
#endif
    }
}
