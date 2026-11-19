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
    // evloop-1: the event loop's decisions (mode, frame cap, deadlines, drain caps), the queued actions,
    // the late-wake backoff, and the Windows waiter (sticky wake, timer, socket readiness).
    public class EventLoopTests
    {
        // ---- mode decision (fallbacks) ----

        [Theory]
        [InlineData(false, true, false, 0)] // no waiter for this OS (or event_loop_linux off)
        [InlineData(true, false, false, 0)] // waiter missing or its probe failed
        [InlineData(true, true, true, 0)] // late-wake backoff in force
        [InlineData(true, true, false, 1)]
        public void Decide_Falls_Back_To_Legacy_Unless_Everything_Is_Ready(bool hasWaiter, bool ready, bool suspended, int expected)
        {
            LoopTiming.Decide(hasWaiter, ready, suspended).Should().Be((LoopMode)expected);
        }

        // ---- loop-linux: the waiter per OS ----

        [Theory]
        [InlineData(true, false, false, (int)LoopWaiterKind.Win32)]
        [InlineData(true, false, true, (int)LoopWaiterKind.Win32)] // the Linux switch does not touch Windows
        [InlineData(false, true, false, (int)LoopWaiterKind.None)] // event_loop_linux off: legacy loop
        [InlineData(false, true, true, (int)LoopWaiterKind.LinuxEpoll)]
        [InlineData(false, false, true, (int)LoopWaiterKind.None)] // other OSes have none yet
        [InlineData(false, false, false, (int)LoopWaiterKind.None)]
        public void Factory_Chooses_The_Waiter_Per_OS_And_Switch(bool windows, bool linux, bool linuxSwitch, int expected)
        {
            LoopWaiterFactory.Choose(windows, linux, linuxSwitch).Should().Be((LoopWaiterKind)expected);
        }

        [Fact]
        public void Factory_Creates_No_Waiter_For_None()
        {
            LoopWaiterFactory.Create(LoopWaiterKind.None, 0).Should().BeNull();
        }

        [Theory]
        [InlineData(1.0, 0, 1_000_000)]
        [InlineData(16.5, 0, 16_500_000)]
        [InlineData(1500.25, 1, 500_250_000)]
        [InlineData(0.0, 0, 1000)] // a zero it_value would disarm the timerfd
        [InlineData(-3.0, 0, 1000)]
        [InlineData(0.0001, 0, 1000)]
        public void Timerfd_Deadline_Is_Relative_Seconds_And_Nanoseconds_Never_Zero(double ms, long sec, long nsec)
        {
            var ts = LoopLinux.ToRelativeTimespec(ms);
            ts.Sec.Should().Be(sec);
            ts.Nsec.Should().Be(nsec);
        }

        [Theory]
        [InlineData(0.4, 1)] // rounded up so the fallback wait never returns before its deadline
        [InlineData(16.0, 16)]
        [InlineData(16.01, 17)]
        [InlineData(0.0, 0)]
        [InlineData(double.PositiveInfinity, -1)]
        public void Epoll_Fallback_Timeout_Rounds_Up(double ms, int expected)
        {
            LoopLinux.ToEpollTimeoutMs(ms).Should().Be(expected);
        }

        [Theory]
        [InlineData(true, true, true, true, (int)WakeReason.Net)]
        [InlineData(false, true, true, true, (int)WakeReason.Wake)]
        [InlineData(false, false, true, true, (int)WakeReason.Timer)]
        [InlineData(false, false, false, true, (int)WakeReason.Input)]
        [InlineData(false, false, false, false, (int)WakeReason.Timer)]
        public void Linux_Wait_Reports_Sources_In_The_Windows_Order(bool net, bool wake, bool timer, bool input, int expected)
        {
            LinuxEpollLoopWaiter.PickReason(net, wake, timer, input).Should().Be((WakeReason)expected);
        }

        [Fact]
        public unsafe void Epoll_Event_Layout_Puts_The_Tag_After_The_Mask()
        {
            byte* ev = stackalloc byte[16];
            LoopLinux.WriteEvent(ev, LoopLinux.EPOLLIN | LoopLinux.EPOLLONESHOT, 5);
            LoopLinux.ReadEvents(ev).Should().Be(LoopLinux.EPOLLIN | LoopLinux.EPOLLONESHOT);
            LoopLinux.ReadTag(ev).Should().Be(5u);
            LoopLinux.EpollEventSize.Should().Be(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                System.Runtime.InteropServices.Architecture.X64 ? 12 : 16);
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

        // ---- queued actions ----

        [Fact]
        public void Queued_Actions_Run_Every_Due_One()
        {
            const int expected = 3;
            var q = new QueuedActions();
            var ran = new List<int>();
            q.Add(10, () => ran.Add(1));
            q.Add(20, () => ran.Add(2));
            q.Add(30, () => ran.Add(3));
            q.Add(500, () => ran.Add(4));

            q.RunDue(100).Should().Be(expected);
            ran.Should().Equal(3, 2, 1); // newest first, as the old loop
            q.Count.Should().Be(4 - expected);
        }

        [Fact]
        public void Queued_Action_Queued_By_A_Running_One_Waits_For_The_Next_Call()
        {
            var q = new QueuedActions();
            var ran = new List<string>();
            q.Add(10, () =>
            {
                ran.Add("a");
                q.Add(0, () => ran.Add("b"));
            });

            q.RunDue(100);
            ran.Should().Equal("a");
            q.Count.Should().Be(1);

            q.RunDue(100);
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

            q.RunDue(200).Should().Be(0, "an action runs once the time is past its due tick");
            q.RunDue(201).Should().Be(1);
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

        // ---- the Linux waiter (epoll; runs on Linux only, without SDL) ----

        [Fact]
        public void Linux_Waiter_Wake_Is_Sticky_ClearWake_Drops_It_And_The_Timer_Fires()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            using var w = new LinuxEpollLoopWaiter(0, sdl: false);
            w.Ready.Should().BeTrue();
            w.HighResolution.Should().BeTrue("timerfd exists on every supported kernel");

            w.Wake();
            var sw = Stopwatch.StartNew();
            w.Wait(2000).Should().Be(WakeReason.Wake);
            sw.ElapsedMilliseconds.Should().BeLessThan(1000);

            // consumed: the next wait runs to its deadline
            sw.Restart();
            w.Wait(5).Should().Be(WakeReason.Timer);
            sw.Elapsed.TotalMilliseconds.Should().BeInRange(4.5, 1000);

            w.Wake();
            w.ClearWake();
            w.Wait(5).Should().Be(WakeReason.Timer, "ClearWake dropped the pending wake");

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
        public void Linux_Waiter_Wakes_On_Socket_Data_Once_Per_Arm_And_On_Close()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new NetClient();
            client.Load(ClientVersion.CV_70130, EncryptionType.NONE);
            using var w = new LinuxEpollLoopWaiter(0, sdl: false);

            try
            {
                client.Connect("127.0.0.1", (ushort)((IPEndPoint)listener.LocalEndpoint).Port);
                using var server = listener.AcceptSocket();

                w.BindNet(client);
                w.Wait(30).Should().Be(WakeReason.Timer, "nothing was sent yet");

                server.Send(new byte[] { 1, 2, 3, 4, 5 });
                w.Wait(5000).Should().Be(WakeReason.Net);
                w.Wait(30).Should().Be(WakeReason.Timer, "one-shot: not re-armed until BindNet");

                // re-armed with the data still unread: the next wait ends at once
                w.BindNet(client);
                w.Wait(5000).Should().Be(WakeReason.Net);

                var got = new List<byte>();
                w.BindNet(client);
                client.Drain(seg =>
                {
                    got.AddRange(seg);

                    return 0;
                }, maxMs: 0);
                got.Should().Equal(1, 2, 3, 4, 5);
                w.Wait(30).Should().Be(WakeReason.Timer, "drained");

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

                // sends are unchanged (the socket's mode is left alone)
                var token = client.BeginLoginHandshake(0);
                client.Send(new byte[] { 0x73, 9, 9 });
                client.EndHandshake(token);
                client.Flush();
                var buf = new byte[16];
                server.ReceiveTimeout = 5000;
                server.Receive(buf).Should().Be(3);

                w.BindNet(client);
                server.Shutdown(SocketShutdown.Both);
                w.Wait(5000).Should().Be(WakeReason.Net, "a peer close ends the wait");
                w.Wait(30).Should().Be(WakeReason.Timer, "and does not spin the loop before the drain");
            }
            finally
            {
                client.Disconnect();
                listener.Stop();
            }
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
                client.Send(new byte[] { 0x73, 9, 9 });
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
