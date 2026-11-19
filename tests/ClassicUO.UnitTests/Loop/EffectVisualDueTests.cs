using System;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Loop;
using ClassicUO.UnitTests.Ecs;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Loop
{
    // evloop-5: effects report their next visual change into VisualDue (visual-due tracking) instead of counting as
    // motion while any is alive. Every effect's drawn state (frame, position, offset, life) may change only in an update that
    // the previous update reported as due (Time.Ticks past the earliest due tick) or as changing every frame.
    [Collection("ChunkStaticTileDataCollection")] // Time.Ticks and VisualDue are process-wide
    public sealed class EffectVisualDueTests : IDisposable
    {
        private const uint PLAYER = 0x0000_0100;
        private const uint T0 = 2_000_000;

        private readonly World _world;
        private readonly bool _savedTracking;

        public EffectVisualDueTests()
        {
            _world = WorldWriterMobileTests.BuildPlayer(out _, PLAYER);
            _world.RangeSize.X = 1000;
            _world.RangeSize.Y = 1000;
            _savedTracking = VisualDue.Tracking;
            VisualDue.Tracking = true;
            VisualDue.Reset();
            Time.Ticks = T0;
            Time.Delta = 0.001f;
        }

        public void Dispose()
        {
            VisualDue.Tracking = _savedTracking;
            VisualDue.Reset();
        }

        private static unsafe FixedEffect FixedWithFrames(World w, int count, int duration, ushort interval)
        {
            var e = new FixedEffect(w, w.Effects, 1001, 1001, 0, 0x3779, 0, duration, 0);
            e.AnimDataFrame.FrameCount = (byte)count;
            e.AnimDataFrame.FrameInterval = 1;

            for (int i = 0; i < count; i++)
            {
                e.AnimDataFrame.FrameData[i] = (sbyte)(i * 3);
            }

            e.IntervalInMs = interval;
            w.Effects.Add(e);

            return e;
        }

        private string Snap()
        {
            var sb = new System.Text.StringBuilder();

            for (GameEffect f = (GameEffect)_world.Effects.Items; f != null; f = (GameEffect)f.Next)
            {
                EffectState s = f.EcsEntity.IsNull ? default : f.EcsEntity.GetComponent<EffectState>();
                sb.Append(f.GetType().Name).Append('|').Append(f.X).Append(',').Append(f.Y).Append('|').Append(s.OffsetX.ToString("R"))
                    .Append(',').Append(s.OffsetY.ToString("R")).Append("|f").Append(s.AnimGraphic).Append('\n');
            }

            return sb.ToString();
        }

        private void Update()
        {
            VisualDue.BeginUpdate();
            _world.Effects.Update();
        }

        // 1 ms steps: a change of the drawn state must be in an update whose time is past the due tick the previous update
        // reported, or follow an update that reported a continuous source
        private int RunAndCheck(int ms)
        {
            Update();
            string last = Snap();
            long due = VisualDue.NextTick;
            bool continuous = VisualDue.Continuous != VisualSource.None;
            int changes = 0;

            for (int i = 0; i < ms; i++)
            {
                Time.Ticks++;
                Update();
                string now = Snap();

                if (now != last)
                {
                    changes++;
                    (continuous || LoopTiming.VisualDueReached(due, Time.Ticks)).Should().BeTrue(
                        $"the change at {Time.Ticks} was reported (due {due}, continuous {continuous})");
                }

                last = now;
                due = VisualDue.NextTick;
                continuous = VisualDue.Continuous != VisualSource.None;
            }

            return changes;
        }

        [Fact]
        public void A_Fixed_Effect_With_Frames_Reports_Its_Next_Frame_And_Its_End()
        {
            FixedWithFrames(_world, 4, 300, 40);
            Update();
            VisualDue.Continuous.Should().Be(VisualSource.None);
            VisualDue.NextSource.Should().Be(VisualSource.Effects);
            VisualDue.NextTick.Should().BeInRange(Time.Ticks, Time.Ticks + 40);

            RunAndCheck(400).Should().BeGreaterThan(4, "the frames advanced and the effect ended");
            _world.Effects.IsEmpty.Should().BeTrue();
            VisualDue.NextTick.Should().Be(long.MaxValue);
        }

        [Fact]
        public void A_Single_Frame_Effect_Reports_Only_Its_End_And_An_Endless_One_Nothing()
        {
            FixedEffect timed = FixedWithFrames(_world, 1, 500, 40);
            Update();
            VisualDue.NextTick.Should().Be(timed.Duration);

            timed.Destroy();
            FixedWithFrames(_world, 0, -1, 40);
            Update();
            VisualDue.NextTick.Should().Be(long.MaxValue);
            VisualDue.Continuous.Should().Be(VisualSource.None);
            RunAndCheck(200).Should().Be(0);
        }

        [Fact]
        public void A_Moving_Effect_Changes_Every_Frame()
        {
            _world.Effects.CreateEffect(GraphicEffectType.Moving, 0, 0, 0x36D4, 0, 1000, 1000, 0, 1006, 1004, 0, 5, 0, false, false, false,
                GraphicEffectBlendMode.Normal);
            Update();
            VisualDue.Continuous.Should().HaveFlag(VisualSource.Effects);
            RunAndCheck(300);
        }

        [Fact]
        public void Lightning_Reports_Each_Step_Until_It_Ends()
        {
            _world.Effects.CreateEffect(GraphicEffectType.Lightning, 0, 0, 0, 0, 1001, 1001, 0, 0, 0, 0, 5, 0, false, false, false,
                GraphicEffectBlendMode.Normal);
            RunAndCheck(1500).Should().BeGreaterThan(5);
            _world.Effects.IsEmpty.Should().BeTrue();
        }

        [Fact]
        public void Tracking_Off_Reports_Nothing()
        {
            VisualDue.Tracking = false;
            FixedWithFrames(_world, 4, 300, 40);
            Update();
            VisualDue.NextTick.Should().Be(long.MaxValue);
        }
    }
}
