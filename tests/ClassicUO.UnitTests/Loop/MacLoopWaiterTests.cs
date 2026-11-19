using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using ClassicUO.Configuration;
using ClassicUO.Loop;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Loop
{
    // loop-macos: the waiter choice per OS and switch, the macOS waiter's pure decisions, and (on macOS only) the kqueue over
    // the socket and the wake. The SDL/Cocoa wait itself needs a window and is checked by hand (manual-tests/loop-macos.md).
    public class MacLoopWaiterTests
    {
        [Theory]
        [InlineData(true, false, false, 1)] // Win32
        [InlineData(true, false, true, 1)] // Windows ignores event_loop_macos
        [InlineData(false, true, true, 2)] // MacOS
        [InlineData(false, true, false, 0)] // switch off: the legacy loop, as before
        [InlineData(false, false, true, 0)] // Linux and others: no waiter here
        public void Choose_Picks_The_Waiter_For_The_OS_And_Switch(bool windows, bool macOS, bool macSwitch, int expected)
        {
            LoopWaiter.Choose(windows, macOS, macSwitch).Should().Be((LoopWaiter.WaiterKind)expected);
        }

        [Fact]
        public void Switch_Is_Off_By_Default_And_Serialized()
        {
            new Settings().EventLoopMacOS.Should().BeFalse();
            JsonSerializer.Serialize(new Settings()).Should().Contain("\"event_loop_macos\":false");
        }

        [Fact]
        public void Create_Returns_No_Waiter_Off_Windows_And_MacOS()
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            {
                return;
            }

            LoopWaiter.Create(true).Should().BeNull();
        }

        [Theory]
        [InlineData(false, false, false, 1)] // 1 Timer, 2 Net, 3 Input, 4 Wake
        [InlineData(true, false, false, 3)] // SDL returned a window or input event
        [InlineData(true, true, false, 2)]
        [InlineData(false, true, false, 2)] // fired outside the wait: sticky, the wait was skipped
        [InlineData(true, false, true, 4)]
        [InlineData(true, true, true, 2)] // the net wake wins: it changes the screen, a send wake does not
        public void Classify_Prefers_Net_Then_Wake_Then_Input(bool returned, bool net, bool wake, int expected)
        {
            MacLoopWaiter.Classify(returned, net, wake).Should().Be((WakeReason)expected);
        }

        [Theory]
        // net, wake, inWait, eventPending -> push, reenable
        [InlineData(true, false, true, false, true, false)] // socket readable during the wait: end it, re-arm after the drain
        [InlineData(false, true, true, false, true, true)] // a wake during the wait: end it, keep listening
        [InlineData(true, false, true, true, false, false)] // already ending the wait: one SDL event is enough
        [InlineData(true, false, false, false, false, false)] // outside a wait (FNA's poll): flag only, the next Wait returns at once
        [InlineData(false, true, false, false, false, true)]
        [InlineData(false, false, true, false, false, true)] // nothing pending (already drained or cleared): re-arm only
        public void OnFired_Pushes_Only_Inside_A_Wait_And_Rearms_Unless_The_Socket_Is_Readable(bool net, bool wake, bool inWait,
            bool pending, bool push, bool reenable)
        {
            MacLoopWaiter.OnFired(net, wake, inWait, pending, out bool p, out bool r);
            p.Should().Be(push);
            r.Should().Be(reenable);
        }

        [Fact]
        public void Kevent_Structs_Match_The_64_Bit_Darwin_Layout()
        {
            Marshal.SizeOf<LoopMacOS.kevent_t>().Should().Be(32);
            Marshal.OffsetOf<LoopMacOS.kevent_t>(nameof(LoopMacOS.kevent_t.fflags)).ToInt32().Should().Be(12);
            Marshal.OffsetOf<LoopMacOS.kevent_t>(nameof(LoopMacOS.kevent_t.udata)).ToInt32().Should().Be(24);
            Marshal.SizeOf<LoopMacOS.timespec_t>().Should().Be(16);
        }

        [Fact]
        public void Socket_Fd_Is_Only_Exposed_Off_Windows_While_Connected()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new NetClient();
            client.Load(ClientVersion.CV_70130, EncryptionType.NONE);

            try
            {
                client.TryGetSocketFd(out _).Should().BeFalse("not connected");
                client.Connect("127.0.0.1", (ushort)((IPEndPoint)listener.LocalEndpoint).Port);
                using var server = listener.AcceptSocket();
                bool got = client.TryGetSocketFd(out nint fd);
                got.Should().Be(!OperatingSystem.IsWindows());

                if (got)
                {
                    fd.Should().BeGreaterThanOrEqualTo(0);
                }
            }
            finally
            {
                client.Disconnect();
                listener.Stop();
            }
        }

        [Fact]
        public void MacKqueue_Wake_Is_Latched_Until_Polled_Once()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            using var kq = new MacKqueue();
            kq.Valid.Should().BeTrue();

            kq.Poll(out bool net, out bool wake);
            (net || wake).Should().BeFalse();

            // from another thread, before anyone polls
            var t = new Thread(kq.Wake);
            t.Start();
            t.Join();

            kq.Poll(out net, out wake);
            wake.Should().BeTrue();
            net.Should().BeFalse();

            kq.Poll(out _, out wake);
            wake.Should().BeFalse("EV_CLEAR: retrieved once");
        }

        [Fact]
        public void MacKqueue_Socket_Is_Level_Triggered_Until_Drained_And_Reports_A_Peer_Close()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new NetClient();
            client.Load(ClientVersion.CV_70130, EncryptionType.NONE);
            using var kq = new MacKqueue();

            try
            {
                client.Connect("127.0.0.1", (ushort)((IPEndPoint)listener.LocalEndpoint).Port);
                var server = listener.AcceptSocket();

                client.TryGetSocketFd(out nint fd).Should().BeTrue();
                kq.SetSocket(fd).Should().BeTrue();
                kq.Poll(out bool net, out _);
                net.Should().BeFalse("nothing was sent yet");

                server.Send(new byte[] { 1, 2, 3 });
                SpinUntilNet(kq).Should().BeTrue();
                kq.Poll(out net, out _);
                net.Should().BeTrue("level-triggered: still unread");

                int received = 0;
                client.Drain(seg => { received += seg.Count; return 0; }, maxMs: 0);
                received.Should().Be(3);
                kq.Poll(out net, out _);
                net.Should().BeFalse("drained");

                // a wake and the socket together are both reported
                kq.Wake();
                server.Send(new byte[] { 4 });
                Thread.Sleep(50);
                kq.Poll(out net, out bool wake);
                net.Should().BeTrue();
                wake.Should().BeTrue();
                client.Drain(_ => 0, maxMs: 0);

                // unbinding stops the socket's reports
                server.Send(new byte[] { 5 });
                Thread.Sleep(50);
                kq.SetSocket(-1).Should().BeTrue();
                kq.Poll(out net, out _);
                net.Should().BeFalse();
                kq.SetSocket(fd).Should().BeTrue();
                SpinUntilNet(kq).Should().BeTrue("re-registered while data is unread");
                client.Drain(_ => 0, maxMs: 0);

                // a peer close makes the socket readable (EOF)
                server.Close();
                SpinUntilNet(kq).Should().BeTrue();
            }
            finally
            {
                client.Disconnect();
                listener.Stop();
            }
        }

        private static bool SpinUntilNet(MacKqueue kq)
        {
            for (int i = 0; i < 200; i++)
            {
                kq.Poll(out bool net, out _);

                if (net)
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return false;
        }
    }
}
