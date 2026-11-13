using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ClassicUO.Loop;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Loop
{
    // evloop-1: the event loop's decisions (mode, frame cap, deadlines, drain caps), the queued actions in both switch states,
    // the late-wake backoff, and the Windows waiter (sticky wake, timer, socket readiness).
    public class EventLoopTests
    {
        // ---- mode decision (fallbacks) ----

        [Theory]
        [InlineData(false, true, true, false, 0)] // switch off
        [InlineData(true, false, true, false, 0)] // not Windows
        [InlineData(true, true, false, false, 0)] // waiter missing or its probe failed
        [InlineData(true, true, true, true, 0)] // late-wake backoff in force
        [InlineData(true, true, true, false, 1)]
        public void Decide_Falls_Back_To_Legacy_Unless_Everything_Is_Ready(bool on, bool windows, bool ready, bool suspended, int expected)
        {
            LoopTiming.Decide(on, windows, ready, suspended).Should().Be((LoopMode)expected);
        }

        // ---- frame cap ----

        [Fact]
        public void DrawDue_Waits_Until_The_Interval_Less_The_Slack()
        {
            LoopTiming.DrawDue(10, 16.67, out var t).Should().BeFalse();
            t.Should().Be(10);

            LoopTiming.DrawDue(16.67 - LoopTiming.DrawSlackMs - 0.01, 16.67, out _).Should().BeFalse();
            LoopTiming.DrawDue(16.67 - LoopTiming.DrawSlackMs + 0.01, 16.67, out t).Should().BeTrue();
            t.Should().BeNegative("an early draw's lead is carried into the next interval");
        }

        [Fact]
        public void DrawDue_Carries_The_Overshoot_And_Wraps_After_A_Stall()
        {
            LoopTiming.DrawDue(20, 16, out var t).Should().BeTrue();
            t.Should().Be(4);

            LoopTiming.DrawDue(50, 16, out t).Should().BeTrue();
            t.Should().Be(34 % 16);
        }

        [Fact]
        public void DrawDue_Uncapped_Always_Draws()
        {
            LoopTiming.DrawDue(0, 0, out var t).Should().BeTrue();
            t.Should().Be(0);
        }

        [Fact]
        public void Average_Rate_Stays_At_The_Cap_With_Early_Draws()
        {
            // a loop that wakes exactly at each computed deadline, plus 0.3 ms of jitter early or late
            double total = 0, interval = 1000.0 / 60;
            int draws = 0;
            double clock = 0;
            var rng = new Random(1);

            while (clock < 10_000)
            {
                double step = LoopTiming.MsUntilDraw(false, interval, total, 0);
                step = Math.Max(0.05, step + (rng.NextDouble() - 0.5) * 0.6);
                clock += step;
                total += step;

                if (LoopTiming.DrawDue(total, interval, out var carried))
                {
                    total = carried;
                    draws++;
                }
            }

            draws.Should().BeInRange(599, 601);
        }

        // ---- deadlines ----

        [Fact]
        public void MsUntilDraw_Counts_Time_Since_The_Update()
        {
            LoopTiming.MsUntilDraw(false, 16, 4, 3).Should().Be(9);
            LoopTiming.MsUntilDraw(false, 16, 14, 5).Should().Be(0);
            LoopTiming.MsUntilDraw(false, 0, 4, 3).Should().Be(0, "uncapped never waits");
            LoopTiming.MsUntilDraw(true, 16, 4, 3).Should().Be(double.PositiveInfinity, "a minimized window has no draw deadline");
        }

        [Fact]
        public void WaitMs_Is_The_Earliest_Deadline_Or_None_Below_The_Slack()
        {
            LoopTiming.WaitMs(9, 30, 16).Should().Be(9);
            LoopTiming.WaitMs(9, 3, 16).Should().Be(3);
            LoopTiming.WaitMs(double.PositiveInfinity, double.PositiveInfinity, 16).Should().Be(16);
            LoopTiming.WaitMs(0.4, 30, 16).Should().Be(0);
            LoopTiming.WaitMs(0, 30, 16).Should().Be(0);
        }

        [Fact]
        public void MsUntilTick_Fires_Once_Time_Passes_The_Due_Tick()
        {
            // an action due at tick 100 runs when Time.Ticks > 100, i.e. at 101
            LoopTiming.MsUntilTick(true, 100, 90, 0).Should().Be(11);
            LoopTiming.MsUntilTick(true, 100, 90, 4).Should().Be(7);
            LoopTiming.MsUntilTick(true, 100, 120, 0).Should().Be(0);
            LoopTiming.MsUntilTick(false, 100, 90, 0).Should().Be(double.PositiveInfinity);
        }

        [Fact]
        public void ScaleCap_Grows_With_Time_Since_The_Last_Drain_Up_To_Four_Times()
        {
            LoopTiming.ScaleCap(1000, 0).Should().Be(1000);
            LoopTiming.ScaleCap(1000, 1).Should().Be(1000);
            LoopTiming.ScaleCap(1000, 1000.0 / 60 * 2).Should().BeInRange(1999, 2000);
            LoopTiming.ScaleCap(1000, 500).Should().Be(4000);
            LoopTiming.ScaleCap(1000, double.NaN).Should().Be(1000);
        }

        // ---- queued actions (both switch states) ----

        [Theory]
        [InlineData(false, 1)] // legacy: one due action per Update
        [InlineData(true, 3)] // event loop: every due one
        public void Queued_Actions_Run_One_Or_All_Due(bool drainAll, int expected)
        {
            var q = new QueuedActions();
            var ran = new List<int>();
            q.Add(10, () => ran.Add(1));
            q.Add(20, () => ran.Add(2));
            q.Add(30, () => ran.Add(3));
            q.Add(500, () => ran.Add(4));

            q.RunDue(100, drainAll).Should().Be(expected);
            ran.Should().HaveCount(expected);
            ran.Should().NotContain(4);
            q.Count.Should().Be(4 - expected);

            if (!drainAll)
            {
                ran.Should().Equal(3); // newest first, as the old loop
                q.RunDue(100, false);
                q.RunDue(100, false);
                ran.Should().Equal(3, 2, 1);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Queued_Action_Queued_By_A_Running_One_Waits_For_The_Next_Call(bool drainAll)
        {
            var q = new QueuedActions();
            var ran = new List<string>();
            q.Add(10, () =>
            {
                ran.Add("a");
                q.Add(0, () => ran.Add("b"));
            });

            q.RunDue(100, drainAll);
            ran.Should().Equal("a");
            q.Count.Should().Be(1);

            q.RunDue(100, drainAll);
            ran.Should().Equal("a", "b");
            q.Count.Should().Be(0);
        }

        [Fact]
        public void Queued_Actions_Not_Due_Stay_And_Report_The_Earliest()
        {
            var q = new QueuedActions();
            q.TryEarliest(out _).Should().BeFalse();

            q.Add(300, () => { });
            q.Add(200, () => { });
            q.TryEarliest(out var due).Should().BeTrue();
            due.Should().Be(200);

            q.RunDue(200, true).Should().Be(0, "an action runs once the time is past its due tick");
            q.RunDue(201, true).Should().Be(1);
            q.TryEarliest(out due).Should().BeTrue();
            due.Should().Be(300);
        }

        // ---- late-wake backoff ----

        private static void Second(LateWakeMonitor m, ref long now, int waits, int late)
        {
            for (int i = 0; i < waits; i++)
            {
                m.RecordWait(i < late);
            }

            now += LateWakeMonitor.SampleMs;
            m.Sample(now);
        }

        [Fact]
        public void LateWake_Clean_Or_Rare_Late_Wakes_Never_Back_Off()
        {
            var m = new LateWakeMonitor();
            long now = 1000;
            m.Sample(now);

            for (int i = 0; i < 10; i++)
            {
                Second(m, ref now, 100, 5); // 5 % late
                m.IsSuspended(now).Should().BeFalse();
            }

            Second(m, ref now, 2, 1); // one late wake is below the floor
            Second(m, ref now, 2, 1);
            m.IsSuspended(now).Should().BeFalse();
            m.TotalBackoffs.Should().Be(0);
        }

        [Fact]
        public void LateWake_Two_Bad_Seconds_Suspend_With_Doubling_Backoff()
        {
            var m = new LateWakeMonitor();
            long now = 1000;
            m.Sample(now);

            Second(m, ref now, 50, 20);
            m.IsSuspended(now).Should().BeFalse("one bad second is not enough");
            Second(m, ref now, 50, 20);
            m.IsSuspended(now).Should().BeTrue();
            m.CurrentBackoffMs.Should().Be(LateWakeMonitor.BackoffBaseMs);
            m.IsSuspended(now + LateWakeMonitor.BackoffBaseMs + 1).Should().BeFalse();

            now += LateWakeMonitor.BackoffBaseMs;
            Second(m, ref now, 50, 20);
            Second(m, ref now, 50, 20);
            m.CurrentBackoffMs.Should().Be(LateWakeMonitor.BackoffBaseMs * 2);
            m.TotalBackoffs.Should().Be(2);
        }

        [Fact]
        public void LateWake_Escalation_Resets_After_A_Clean_Minute_Of_Event_Loop_Time()
        {
            var m = new LateWakeMonitor();
            long now = 1000;
            m.Sample(now);

            Second(m, ref now, 50, 20);
            Second(m, ref now, 50, 20);
            m.CurrentBackoffMs.Should().Be(LateWakeMonitor.BackoffBaseMs);

            // the suspension runs out, then 59 clean seconds: not yet a clean minute
            while (m.IsSuspended(now + LateWakeMonitor.SampleMs))
            {
                Second(m, ref now, 0, 0);
            }

            for (int i = 0; i < 59; i++)
            {
                Second(m, ref now, 50, 0);
            }

            Second(m, ref now, 50, 20); // a bad second breaks the streak
            Second(m, ref now, 50, 20);
            m.CurrentBackoffMs.Should().Be(LateWakeMonitor.BackoffBaseMs * 2, "59 clean seconds are not a clean minute");

            while (m.IsSuspended(now + LateWakeMonitor.SampleMs))
            {
                Second(m, ref now, 0, 0);
            }

            for (int i = 0; i < 60; i++)
            {
                Second(m, ref now, 50, 0);
            }

            Second(m, ref now, 50, 20);
            Second(m, ref now, 50, 20);
            m.CurrentBackoffMs.Should().Be(LateWakeMonitor.BackoffBaseMs, "a clean minute resets the escalation");
        }

        [Fact]
        public void LateWake_A_Suspension_Longer_Than_The_Reset_Period_Does_Not_Reset_The_Escalation()
        {
            var m = new LateWakeMonitor();
            long now = 1000;
            m.Sample(now);

            // escalate to 80 s (5, 10, 20, 40, 80), sampling every second through each suspension (legacy loop: no waits)
            long expected = LateWakeMonitor.BackoffBaseMs;

            for (int k = 0; k < 5; k++)
            {
                Second(m, ref now, 50, 20);
                Second(m, ref now, 50, 20);
                m.CurrentBackoffMs.Should().Be(expected);
                expected = Math.Min(expected * 2, LateWakeMonitor.BackoffMaxMs);

                while (m.IsSuspended(now + LateWakeMonitor.SampleMs))
                {
                    Second(m, ref now, 0, 0);
                }
            }

            m.CurrentBackoffMs.Should().BeGreaterThan(LateWakeMonitor.ResetAfterCleanMs);

            Second(m, ref now, 50, 20);
            Second(m, ref now, 50, 20);
            m.CurrentBackoffMs.Should().Be(LateWakeMonitor.BackoffMaxMs, "suspended seconds are not clean time");
        }

        // ---- the Windows waiter ----

        [Fact]
        public void Win32_Waiter_Wake_Is_Sticky_And_The_Timer_Fires()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var w = new Win32LoopWaiter();
            w.HighResolution.Should().BeTrue("Windows 10 1803+ has high-resolution waitable timers");

            // a wake made before the wait ends the next wait at once, once
            w.Wake();
            var sw = Stopwatch.StartNew();
            w.Wait(2000).Should().Be(WakeReason.Wake);
            sw.ElapsedMilliseconds.Should().BeLessThan(1000);

            sw.Restart();
            w.Wait(5).Should().BeOneOf(WakeReason.Timer, WakeReason.Input); // a test host thread may own a message queue
            sw.Elapsed.TotalMilliseconds.Should().BeLessThan(1000);

            // a wake from another thread ends a wait in progress
            var t = new Thread(() =>
            {
                Thread.Sleep(20);
                w.Wake();
            });
            t.Start();
            sw.Restart();
            w.Wait(5000).Should().Be(WakeReason.Wake);
            sw.ElapsedMilliseconds.Should().BeLessThan(2500);
            t.Join();
        }

        [Fact]
        public void Win32_Waiter_Wakes_On_Socket_Data_And_The_Socket_Still_Reads_And_Sends()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new NetClient();
            client.Load(ClientVersion.CV_70130, EncryptionType.NONE);
            using var w = new Win32LoopWaiter();

            try
            {
                client.Connect("127.0.0.1", (ushort)((IPEndPoint)listener.LocalEndpoint).Port);
                using var server = listener.AcceptSocket();

                w.BindNet(client);
                w.Wait(30).Should().Be(WakeReason.Timer, "nothing was sent yet");

                server.Send(new byte[] { 1, 2, 3, 4, 5 });
                w.Wait(5000).Should().Be(WakeReason.Net);

                var got = new List<byte>();
                w.BindNet(client); // re-arm before the drain, as GameController does
                client.Drain(seg =>
                {
                    got.AddRange(seg);

                    return 0;
                }, maxMs: 0);
                got.Should().Equal(1, 2, 3, 4, 5);

                // drained: the next wait sleeps until the deadline
                w.Wait(30).Should().Be(WakeReason.Timer);

                // Repeat after an empty drain, including arrival while a wait is in progress.
                // A single initial FD_READ does not prove that later reads re-enable notification.
                for (int i = 0; i < 3; i++)
                {
                    w.BindNet(client);
                    client.Drain(_ => 0, maxMs: 0);
                    var sender = new Thread(() =>
                    {
                        Thread.Sleep(20);
                        server.Send(new byte[] { 42 });
                    });
                    sender.Start();
                    try
                    {
                        w.Wait(2000).Should().Be(WakeReason.Net);
                    }
                    finally
                    {
                        sender.Join();
                    }
                    w.BindNet(client);
                    int received = 0;
                    client.Drain(seg => { received += seg.Count; return 0; }, maxMs: 0);
                    received.Should().Be(1);
                }

                // the socket is non-blocking now; sends go through the wrapper's non-blocking path
                var token = client.BeginLoginHandshake(0); // sends open once the handshake is queued
                client.Send(new byte[] { 0x73, 9, 9 }, ignorePlugin: true);
                client.EndHandshake(token);
                client.Flush();
                var buf = new byte[16];
                server.ReceiveTimeout = 5000;
                server.Receive(buf).Should().Be(3);
                buf[0].Should().Be(0x73);

                // a peer close wakes the wait and the drain ends the connection (the re-arm happens before a drain, never
                // between a close and its drain: FD_CLOSE is signalled once, and the drain's readiness poll still sees it)
                var disconnected = 0;
                client.Disconnected += (_, _) => disconnected++;
                w.BindNet(client);
                server.Shutdown(SocketShutdown.Both);
                server.Close();
                w.Wait(5000).Should().Be(WakeReason.Net);

                var deadline = DateTime.UtcNow.AddSeconds(5);

                while (disconnected == 0 && DateTime.UtcNow < deadline)
                {
                    client.Drain(_ => 0, maxMs: 0);
                }

                disconnected.Should().Be(1);
            }
            finally
            {
                client.Disconnect();
                listener.Stop();
            }
        }
    }
}
