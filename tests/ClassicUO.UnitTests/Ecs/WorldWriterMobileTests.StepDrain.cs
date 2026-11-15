using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using EcsEntity = Friflo.Engine.ECS.Entity;
using StepsComponent = ClassicUO.Ecs.Steps;

namespace ClassicUO.UnitTests.Ecs
{
    // step_drain: a non-local mobile's step lasts its learned server pace (StepDrain). Synthetic packet arrival sequences go
    // through WorldWriter.UpsertMobile and the step system frame by frame (16 ms frames), with the switch on and off.
    public partial class WorldWriterMobileTests
    {
        private readonly ITestOutputHelper _output;

        public WorldWriterMobileTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private struct DrainPacket
        {
            public uint Time;
            public int X, Y;
            public Direction Dir;
            public bool Run, TurnOnly;
        }

        private sealed class DrainResult
        {
            public readonly List<(int Duration, bool Drained, bool Run, bool Mounted)> Steps = new List<(int, bool, bool, bool)>();
            public readonly List<uint> Arrivals = new List<uint>();
            public readonly List<uint> Completions = new List<uint>();
            public int Rejected, MaxQueueBefore, LagAtLastArrival;
            public uint FirstStart;

            public int DrainedCount => Steps.Count(s => s.Drained);
            public double DrainedShare => Steps.Count == 0 ? 0 : DrainedCount / (double)Steps.Count;
        }

        // a walk of `steps` moves from (1000,1000), one every `pace` ms (+- jitter), changing between East and SouthEast every
        // `turnEvery` moves; turnPackets: each change is a turn-only packet, then the move 0-20 ms later (a player's turn)
        private static List<DrainPacket> DrainPath(int seed, int steps, Func<int, int> pace, int jitter, bool run, int turnEvery, bool turnPackets, uint start)
        {
            var rnd = new Random(seed);
            var list = new List<DrainPacket>();
            int x = 1000, y = 1000;
            Direction dir = Direction.East;
            uint nominal = start, prev = 0;

            for (int k = 0; k < steps; k++)
            {
                if (k > 0)
                {
                    nominal += (uint)pace(k);
                }

                uint t = (uint)Math.Max((long)prev, nominal + rnd.Next(-jitter, jitter + 1));
                bool turn = turnEvery > 0 && k > 0 && k % turnEvery == 0;

                if (turn)
                {
                    dir = dir == Direction.East ? Direction.Down : Direction.East;

                    if (turnPackets)
                    {
                        list.Add(new DrainPacket { Time = t, X = x, Y = y, Dir = dir, Run = run, TurnOnly = true });
                        t += (uint)rnd.Next(0, 21);
                    }
                }

                Pathfinder.GetNewXY((byte)dir, ref x, ref y);
                list.Add(new DrainPacket { Time = t, X = x, Y = y, Dir = dir, Run = run });
                prev = t;
            }

            return list;
        }

        private static World DrainWorld(bool mounted, out Mobile m)
        {
            World world = Build(Start.Standing);
            TestPlayer p = TestPlayer.Create(world, STEP_PLAYER);
            typeof(World).GetProperty(nameof(World.Player))!.SetValue(world, p);
            world.Mobiles.Add(p);
            world.Ecs.Sync(p);
            p.TestSetGraphic(0x190);
            p.SetInWorldTile(900, 900, 0);
            m = world.Mobiles.Get(MOB);

            if (mounted)
            {
                EquipMount(world, ETHEREAL);
            }

            SlotsSeam(world);

            return world;
        }

        private static DrainResult RunDrain(World world, Mobile m, List<DrainPacket> packets, uint tail = 3000)
        {
            var r = new DrainResult();
            EcsEntity e = StepEntity(world, m);
            uint t0 = packets[0].Time;
            uint end = packets[^1].Time + tail;
            uint frame = t0;
            int next = 0, lastX = m.X, lastY = m.Y;
            StepEntry recorded = default;
            bool haveRecorded = false;
            int movesSent = 0;
            uint lastArrival = packets.Where(q => !q.TurnOnly).Max(q => q.Time);

            while (frame <= end)
            {
                // the packets due before this frame, in arrival order
                while (next < packets.Count && packets[next].Time <= frame)
                {
                    DrainPacket q = packets[next++];
                    Time.Ticks = q.Time;
                    int before = m.Steps.Count;
                    r.MaxQueueBefore = Math.Max(r.MaxQueueBefore, before);

                    if (before >= Constants.MAX_STEP_COUNT)
                    {
                        r.Rejected++;
                    }

                    Direction d = q.Dir | (q.Run ? Direction.Running : 0);
                    world.Writer.UpsertMobile(m, MOB, 0x190, 0, (ushort)q.X, (ushort)q.Y, 0, d, 0x83EA, ClassicUO.Game.Data.Flags.None, out _, out WorldWriter.Placement pl);
                    world.Writer.PlaceMobile(pl);

                    if (!q.TurnOnly)
                    {
                        movesSent++;
                        r.Arrivals.Add(q.Time);

                        if (r.Arrivals.Count == 1 && m.Steps.Count > 0)
                        {
                            r.FirstStart = m.LastStepTime;
                        }
                    }
                }

                Time.Ticks = frame;
                long drained0 = StepDrain.Drained;
                world.StepSystem.TryRun(world, m, frame, FRAME_DELAY, out _).Should().BeTrue();

                if (m.X != lastX || m.Y != lastY)
                {
                    r.Completions.Add(frame);
                    lastX = m.X;
                    lastY = m.Y;
                }

                ref readonly StepsComponent s = ref e.GetComponent<StepsComponent>();

                if (s.Count > 0 && s.FrontDuration != 0)
                {
                    StepEntry f = s.Ring[s.Head];

                    if (!haveRecorded || f.X != recorded.X || f.Y != recorded.Y)
                    {
                        haveRecorded = true;
                        recorded = f;
                        bool mounted = StepSystem.Mounted(m);
                        r.Steps.Add((s.FrontDuration, StepDrain.Drained != drained0, f.Run, mounted));
                    }
                }

                if (frame <= lastArrival && frame + FRAME_DELAY > lastArrival)
                {
                    r.LagAtLastArrival = movesSent - r.Completions.Count;
                }

                frame += FRAME_DELAY;
            }

            return r;
        }

        private DrainResult Drain(bool on, bool mounted, List<DrainPacket> packets, string name)
        {
            Mobile.FrameDelayOverride = FRAME_DELAY;
            StepDrain.OverrideForTests = on;

            try
            {
                World world = DrainWorld(mounted, out Mobile m);
                world.Writer.ThrowOnViolation = true;
                DrainResult r = RunDrain(world, m, packets);
                m.Steps.Count.Should().Be(0, "every step is rendered after the tail");
                _output.WriteLine($"{name} step_drain {(on ? "on" : "off")}: {r.Arrivals.Count} moves, rejected {r.Rejected}, max queue before a packet {r.MaxQueueBefore}, lag at the last arrival {r.LagAtLastArrival}, durations fixed {r.Steps.Count}: drained {r.DrainedCount} ({r.DrainedShare:P0}), min {(r.Steps.Count > 0 ? r.Steps.Min(s => s.Duration) : 0)} ms, median {(r.Steps.Count > 0 ? r.Steps.OrderBy(s => s.Duration).ElementAt(r.Steps.Count / 2).Duration : 0)} ms");

                return r;
            }
            finally
            {
                StepDrain.OverrideForTests = null;
                Mobile.FrameDelayOverride = null;
            }
        }

        // the render spans at least the server's: it never runs ahead of the moves (the first start to the last completion
        // against the first to the last arrival, less one step)
        private static void RenderNotAhead(DrainResult r, int table)
        {
            r.Completions.Should().HaveCount(r.Arrivals.Count);
            long render = r.Completions[^1] - r.FirstStart;
            long server = r.Arrivals[^1] - r.Arrivals[0];
            render.Should().BeGreaterOrEqualTo(server - table);
        }

        [Fact]
        public void StepDrain_Ettin_Chase_Keeps_Up_Without_Rejections()
        {
            List<DrainPacket> path = DrainPath(1, 60, _ => 200, 40, run: false, turnEvery: 4, turnPackets: false, start: NOW + 1000);
            Drain(false, false, path, "ettin");
            DrainResult r = Drain(true, false, path, "ettin");

            r.Rejected.Should().Be(0);
            r.MaxQueueBefore.Should().BeLessThan(Constants.MAX_STEP_COUNT);
            r.LagAtLastArrival.Should().BeLessOrEqualTo(2);
            r.Steps.Should().OnlyContain(s => s.Duration >= StepDrain.MIN_MS && s.Duration <= MovementSpeed.STEP_DELAY_WALK);
            r.Steps.Should().OnlyContain(s => s.Drained || s.Duration == MovementSpeed.STEP_DELAY_WALK || s.Duration >= 160, "a learned pace is the ~200 ms server pace");
            RenderNotAhead(r, 200);
        }

        public static IEnumerable<object[]> PlayerCases()
        {
            // a mounted player running (the raven case) and a player running on foot, both held to the table by the server
            yield return new object[] { true, MovementSpeed.STEP_DELAY_MOUNT_RUN };
            yield return new object[] { false, MovementSpeed.STEP_DELAY_RUN };
        }

        [Theory]
        [MemberData(nameof(PlayerCases))]
        public void StepDrain_Running_Player_Never_Renders_Faster_Than_The_Table(bool mounted, int table)
        {
            for (int seed = 1; seed <= 20; seed++)
            {
                List<DrainPacket> path = DrainPath(seed, 60, _ => table, 60, run: true, turnEvery: 3, turnPackets: true, start: NOW + 1000);
                DrainResult r = Drain(true, mounted, path, $"player mounted {mounted} seed {seed}");

                r.Steps.Should().OnlyContain(s => s.Run && s.Mounted == mounted);
                // steady state: the table (a few ms of tolerance); drained: never below 3/4 of it
                r.Steps.Where(s => !s.Drained).Should().OnlyContain(s => s.Duration >= table - 2);
                r.Steps.Where(s => s.Drained).Should().NotContain(s => !(s.Duration >= table * StepDrain.DRAIN_PCT / 100));
                RenderNotAhead(r, table);
            }
        }

        [Fact]
        public void StepDrain_Running_Npc_At_120ms_Keeps_Up()
        {
            List<DrainPacket> path = DrainPath(2, 60, _ => 120, 20, run: true, turnEvery: 5, turnPackets: false, start: NOW + 1000);
            Drain(false, false, path, "npc 120 ms");
            DrainResult r = Drain(true, false, path, "npc 120 ms");

            r.Rejected.Should().Be(0);
            r.MaxQueueBefore.Should().BeLessThan(Constants.MAX_STEP_COUNT);
            r.LagAtLastArrival.Should().BeLessOrEqualTo(2);
            r.Steps.Should().OnlyContain(s => s.Duration >= StepDrain.MIN_MS);
            RenderNotAhead(r, 120);
        }

        [Fact]
        public void StepDrain_Npc_Pace_Change_From_400_To_200_Adapts()
        {
            // 20 moves at 400 ms, then 40 at 200 ms
            List<DrainPacket> path = DrainPath(3, 60, k => k < 20 ? 400 : 200, 30, run: false, turnEvery: 0, turnPackets: false, start: NOW + 1000);
            DrainResult r = Drain(true, false, path, "npc 400 -> 200 ms");

            r.Rejected.Should().Be(0);
            r.LagAtLastArrival.Should().BeLessOrEqualTo(2);
            // within about five moves of the change the learned (undrained) pace is the new one
            // the first move rendered faster than the old pace allows (the learned pace, or its drain), and from then on
            int adapted = r.Steps.FindIndex(20, s => s.Duration < MovementSpeed.STEP_DELAY_WALK * StepDrain.SNAP_PCT / 100);
            _output.WriteLine($"adapted at move {adapted} (the pace changes at move 20)");
            adapted.Should().BeInRange(20, 25);
            r.Steps.Skip(adapted).Should().OnlyContain(s => s.Duration < MovementSpeed.STEP_DELAY_WALK * StepDrain.SNAP_PCT / 100);
            // ten moves later the learned pace is the new one (+- the jitter)
            r.Steps.Skip(adapted + 10).Where(s => !s.Drained).Should().OnlyContain(s => s.Duration <= 250);
        }

        [Fact]
        public void StepDrain_A_Pause_Restarts_The_Learned_Pace()
        {
            // 15 moves at 200 ms, a 1.5 s pause, then moves again: the first moves after the pause take the table time
            List<DrainPacket> a = DrainPath(4, 15, _ => 200, 10, run: false, turnEvery: 0, turnPackets: false, start: NOW + 1000);
            uint resume = a[^1].Time + 1500;
            List<DrainPacket> b = DrainPath(5, 6, _ => 400, 0, run: false, turnEvery: 0, turnPackets: false, start: resume);
            int dx = a[^1].X - 1000, dy = a[^1].Y - 1000;

            for (int i = 0; i < b.Count; i++)
            {
                DrainPacket q = b[i];
                q.X += dx;
                q.Y += dy;
                b[i] = q;
            }

            DrainResult r = Drain(true, false, a.Concat(b).ToList(), "pause");

            r.Steps.Take(15).Skip(5).Should().Contain(s => s.Duration < 300, "the 200 ms pace was learned before the pause");
            r.Steps.Skip(15).Should().OnlyContain(s => s.Duration == MovementSpeed.STEP_DELAY_WALK);
        }

        [Fact]
        public void StepDrain_The_Local_Player_Is_Unchanged()
        {
            // the player's queue through the walker: the switch is never read for it (no duration fixed, the same frames)
            Mobile.FrameDelayOverride = FRAME_DELAY;

            try
            {
                var runs = new List<List<(int, int, float, float)>>();

                foreach (bool on in new[] { false, true })
                {
                    StepDrain.OverrideForTests = on;
                    World world = DrainWorld(false, out _);
                    Mobile p = world.Player;
                    GiveWalker((TestPlayer)p);
                    Time.Ticks = NOW;
                    var trace = new List<(int, int, float, float)>();
                    var walker = ((TestPlayer)p).Walker;

                    // the walker's steps, all accepted by the server (its bookkeeping drops each completed one)
                    for (int k = 1; k <= 4; k++)
                    {
                        p.AddStep(new Mobile.Step { X = 900 + k, Y = 900, Z = 0, Direction = (byte)Direction.East, Run = k % 2 == 0 });
                        walker.StepInfos[k - 1].Accepted = true;
                    }

                    walker.StepsCount = 4;

                    p.SetLastStepTime(NOW);

                    for (int f = 0; f < 200; f++)
                    {
                        Time.Ticks += FRAME_DELAY;
                        world.StepSystem.TryRun(world, p, Time.Ticks, FRAME_DELAY, out _);
                        StepEntity(world, p).GetComponent<StepsComponent>().FrontDuration.Should().Be(0);
                        trace.Add((p.X, p.Y, p.OffsetX, p.OffsetY));
                    }

                    runs.Add(trace);
                }

                runs[1].Should().Equal(runs[0]);
            }
            finally
            {
                StepDrain.OverrideForTests = null;
                Mobile.FrameDelayOverride = null;
            }
        }

        [Fact]
        public void StepDrain_Off_Is_The_Old_Timing()
        {
            // off: no arrival is recorded and no duration fixed, and the frames are those of the old Decide (Duration 0); the
            // same with the checker on, which only compares
            List<DrainPacket> path = DrainPath(6, 30, _ => 200, 40, run: false, turnEvery: 4, turnPackets: false, start: NOW + 1000);
            var traces = new List<List<(int, int, float, float)>>();
#if CUO_DEVTOOLS
            bool check = StepDrain.Check;
            bool[] checkers = { false, true };
#else
            bool[] checkers = { false };
#endif

            try
            {
                foreach (bool checker in checkers)
                {
#if CUO_DEVTOOLS
                    StepDrain.Check = checker;
#endif
                    Mobile.FrameDelayOverride = FRAME_DELAY;
                    StepDrain.OverrideForTests = false;
                    World world = DrainWorld(false, out Mobile m);
                    EcsEntity e = StepEntity(world, m);
                    var trace = new List<(int, int, float, float)>();
                    int next = 0;

                    for (uint frame = path[0].Time; frame < path[^1].Time + 3000; frame += FRAME_DELAY)
                    {
                        while (next < path.Count && path[next].Time <= frame)
                        {
                            DrainPacket q = path[next++];
                            Time.Ticks = q.Time;
                            world.Writer.UpsertMobile(m, MOB, 0x190, 0, (ushort)q.X, (ushort)q.Y, 0, q.Dir, 0x83EA, ClassicUO.Game.Data.Flags.None, out _, out WorldWriter.Placement pl);
                            world.Writer.PlaceMobile(pl);
                        }

                        Time.Ticks = frame;
                        world.StepSystem.TryRun(world, m, frame, FRAME_DELAY, out _);
                        ref readonly StepsComponent s = ref e.GetComponent<StepsComponent>();
                        s.FrontDuration.Should().Be(0);

                        if (!checker)
                        {
                            (s.Cadence.Count, s.Cadence.Mode, s.Cadence.LastMove).Should().Be(((byte)0, (byte)0, 0u));
                        }

                        trace.Add((m.X, m.Y, m.OffsetX, m.OffsetY));
                    }

                    traces.Add(trace);
                }

                traces.Should().OnlyContain(t => t.SequenceEqual(traces[0]));
                traces[0].Select(t => (t.Item1, t.Item2)).Distinct().Count().Should().BeGreaterThan(20, "the walk is rendered");
            }
            finally
            {
#if CUO_DEVTOOLS
                StepDrain.Check = check;
#endif
                StepDrain.OverrideForTests = null;
                Mobile.FrameDelayOverride = null;
            }
        }
    }
}
