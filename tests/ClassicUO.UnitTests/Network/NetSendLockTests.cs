using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ClassicUO.IO;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Network.Socket;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network
{
    // Send-side lock: encrypt + enqueue is one critical section, every clear of the send queue takes the same lock, a send
    // belongs to the connection it started on, and nothing is queued ahead of a connection's handshake. The game-server
    // stream cipher (Twofish/MD5 keystream) is a process-wide static, so these tests must not run in parallel with each other.
    // Interleavings are forced with events (and by waiting for a thread to be blocked on the lock), never with sleeps that
    // hope to overlap. Every worker runs inside a Scope: its exceptions come back to the test thread through Join(), and the
    // scope releases and joins all of them (bounded) when the test ends, however it ends.
    [Collection("NetSendLock")]
    public class NetSendLockTests
    {
        private const uint Seed = 0x7F000001;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Queued_Send_Wakes_Event_Loop_Even_On_Main_Thread(bool eventLoop)
        {
            var (client, socket) = Make(EncryptionType.NONE);
            var previous = NetClient.SendQueuedWake;
            int wakes = 0;
            try
            {
                NetClient.SendQueuedWake = eventLoop ? () => wakes++ : null;
                client.Flush(); // packet phase has already run
                client.Send(new byte[] { 0x73, 1 }, ignorePlugin: true);
                wakes.Should().Be(eventLoop ? 1 : 0);
                socket.Bytes.Should().BeEmpty();
                client.Flush();
                socket.Bytes.Should().Equal(0x73, 1);
            }
            finally
            {
                NetClient.SendQueuedWake = previous;
                client.Disconnect();
            }
        }
        private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(30);

        private sealed class Worker
        {
            private readonly Thread _thread;
            private Exception _error;

            public Worker(Action body)
            {
                _thread = new Thread(() =>
                {
                    try
                    {
                        body();
                    }
                    catch (Exception e)
                    {
                        _error = e;
                    }
                })
                { IsBackground = true };
                _thread.Start();
            }

            public Thread Thread => _thread;

            public bool TryJoin() => _thread.Join(Bounded);

            public void Join()
            {
                _thread.Join(Bounded).Should().BeTrue("worker thread must finish");

                if (_error != null)
                {
                    ExceptionDispatchInfo.Capture(_error).Throw();
                }
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly List<Worker> _workers = new List<Worker>();
            public readonly CancellationTokenSource Stop = new CancellationTokenSource();

            public Worker Run(Action body)
            {
                var w = new Worker(body);
                _workers.Add(w);

                return w;
            }

            public void Dispose()
            {
                Stop.Cancel();

                foreach (var w in _workers)
                {
                    w.TryJoin();
                }
            }
        }

        private sealed class CaptureSocket : SocketWrapper
        {
            public readonly List<byte> Bytes = new List<byte>();
            public Action OnFirstSend;
            public Action OnIsConnected;
            private int _sends;
            private int _delivered;

            public int Delivered => Volatile.Read(ref _delivered);

            public override bool IsConnected
            {
                get
                {
                    OnIsConnected?.Invoke();

                    return true;
                }
            }

            public override EndPoint LocalEndPoint => null;
            public override void Connect(Uri uri) { }

            public override void Send(byte[] buffer, int offset, int count)
            {
                Bytes.AddRange(new ArraySegment<byte>(buffer, offset, count));
                Interlocked.Increment(ref _delivered);

                if (_sends++ == 0)
                {
                    OnFirstSend?.Invoke();
                }
            }

            public override int Read(byte[] buffer) => 0;
            public override void Disconnect() { }
            public override void Dispose() { }
        }

        private static NetClient MakeClient(EncryptionType type)
        {
            var client = new NetClient();
            client.Load(ClientVersion.CV_70130, type);

            return client;
        }

        // A client in game-server mode with sends open (the handshake done).
        private static (NetClient client, CaptureSocket socket) Make(EncryptionType type)
        {
            var client = MakeClient(type);
            var socket = new CaptureSocket();
            client.AttachSocket(socket);
            client.EndHandshake(client.BeginGameHandshake(Seed));

            return (client, socket);
        }

        // frame: [len][thread][seq lo][seq hi][fill...]
        private static byte[] Frame(int thread, int seq, int len)
        {
            var f = new byte[len];
            f[0] = (byte)len;
            f[1] = (byte)thread;
            f[2] = (byte)seq;
            f[3] = (byte)(seq >> 8);

            for (var i = 4; i < len; i++)
            {
                f[i] = (byte)(thread * 31 + seq + i);
            }

            return f;
        }

        private static int FrameLen(int thread, int seq) => 5 + (thread * 7 + seq) % 60;

        private static byte[] OwnFrame(int thread, int seq) => Frame(thread, seq, FrameLen(thread, seq));

        // Walks the stream frame by frame; returns the number of frames. Frames may be missing (dropped by a reset) when
        // allowDropped, never torn, duplicated or reordered.
        private static int AssertFramesIntact(ReadOnlySpan<byte> stream, int threads, int perThread, bool allowDropped)
        {
            var next = new int[threads];
            var pos = 0;
            var frames = 0;

            while (pos < stream.Length)
            {
                var len = stream[pos];
                (len >= 5 && pos + len <= stream.Length).Should().BeTrue($"a frame header at {pos} must fit (len {len})");
                var thread = stream[pos + 1];
                var seq = stream[pos + 2] | (stream[pos + 3] << 8);
                thread.Should().BeLessThan((byte)threads);
                len.Should().Be((byte)FrameLen(thread, seq), "frame length of thread {0} seq {1}", thread, seq);

                var expect = Frame(thread, seq, len);

                for (var i = 0; i < len; i++)
                {
                    stream[pos + i].Should().Be(expect[i], "byte {0} of thread {1} seq {2}", i, thread, seq);
                }

                if (allowDropped)
                {
                    seq.Should().BeGreaterThanOrEqualTo(next[thread], "frames of one thread stay in order");
                }
                else
                {
                    seq.Should().Be(next[thread], "frames of one thread arrive in order, none lost");
                }

                next[thread] = seq + 1;
                pos += len;
                frames++;
            }

            if (!allowDropped)
            {
                next.Should().AllBeEquivalentTo(perThread);
            }

            return frames;
        }

        // True when the thread is blocked (on the send lock, the only thing the callers below can block on); false if it
        // finished instead, which is what a missing lock looks like.
        private static bool WaitUntilBlocked(Thread t)
        {
            var deadline = DateTime.UtcNow + Bounded;

            while (DateTime.UtcNow < deadline)
            {
                var state = t.ThreadState;

                if ((state & ThreadState.Stopped) != 0)
                {
                    return false;
                }

                if ((state & ThreadState.WaitSleepJoin) != 0)
                {
                    return true;
                }

                Thread.Yield();
            }

            return false;
        }

        private static byte[] Send3(byte marker) => new byte[] { marker, marker, marker };

        [Fact]
        public void Concurrent_Sends_Reach_The_Receiver_Intact_And_In_Cipher_Order()
        {
            const int threads = 6, perThread = 3000;
            var (client, socket) = Make(EncryptionType.TWOFISH_MD5);

            // receiver: the same keystream, from its own cipher instance (XOR, so "encrypt" decrypts)
            var rx = new TwofishEncryption();
            rx.Initialize(Seed, true);

            using var scope = new Scope();
            using var ready = new CountdownEvent(threads);
            using var start = new ManualResetEventSlim();

            var flusher = scope.Run(() =>
            {
                while (!scope.Stop.IsCancellationRequested)
                {
                    client.Flush();
                    Thread.Yield();
                }
            });

            var senders = new Worker[threads];

            for (var t = 0; t < threads; t++)
            {
                var thread = t;
                senders[t] = scope.Run(() =>
                {
                    ready.Signal();
                    start.Wait(scope.Stop.Token);

                    for (var s = 0; s < perThread; s++)
                    {
                        client.Send(OwnFrame(thread, s), true);
                    }
                });
            }

            ready.Wait(Bounded).Should().BeTrue();
            start.Set();

            foreach (var w in senders)
            {
                w.Join();
            }

            scope.Stop.Cancel();
            flusher.Join();
            client.Flush();

            var cipher = socket.Bytes.ToArray();
            var plain = new byte[cipher.Length];
            rx.Encrypt(cipher, plain, cipher.Length);

            AssertFramesIntact(plain, threads, perThread, allowDropped: false);
            client.Statistics.TotalPacketsSent.Should().Be(threads * perThread);
        }

        [Fact]
        public void Reset_During_A_Flush_Waits_For_The_Flush()
        {
            // 100 frames of 100 bytes: more than one 4096-byte send chunk. A reset requested while the flush is between
            // chunks must block on the send lock until the flush is done, not clear the rest of the queue under it.
            var (client, socket) = Make(EncryptionType.NONE);
            using var scope = new Scope();
            var total = 0;

            for (var s = 0; s < 100; s++)
            {
                var f = Frame(0, s, 100);
                total += f.Length;
                client.Send(f, true);
            }

            Worker reset = null;
            var wasBlocked = false;
            socket.OnFirstSend = () =>
            {
                reset = scope.Run(() => client.EnableCompression());
                wasBlocked = WaitUntilBlocked(reset.Thread); // the reset has started and is waiting for the lock we hold
            };

            client.Flush();
            reset.Join();

            wasBlocked.Should().BeTrue("the reset must be waiting on the send lock while the flush runs");
            socket.Bytes.Count.Should().Be(total, "the flush that was running finished before the reset cleared anything");
        }

        [Fact]
        public void Resets_Overlapping_Sends_Leave_Only_Whole_Frames_And_Something_Arrives()
        {
            const int threads = 4, resetsWanted = 20, tail = 50;
            var (client, socket) = Make(EncryptionType.NONE);
            using var scope = new Scope();
            using var ready = new CountdownEvent(threads);
            using var start = new ManualResetEventSlim();
            var stopSenders = false;
            var sent = 0;
            var resets = 0;

            var flusher = scope.Run(() =>
            {
                while (!scope.Stop.IsCancellationRequested)
                {
                    client.Flush();
                    Thread.Yield();
                }
            });

            var senders = new Worker[threads];

            for (var t = 0; t < threads; t++)
            {
                var thread = t;
                senders[t] = scope.Run(() =>
                {
                    ready.Signal();
                    start.Wait(scope.Stop.Token);

                    // keeps sending until the resetter is done, so every reset overlaps live senders
                    for (var s = 0; s < 50000 && !Volatile.Read(ref stopSenders); s++)
                    {
                        client.Send(OwnFrame(thread, s), true);
                        Interlocked.Increment(ref sent);
                    }
                });
            }

            var resetter = scope.Run(() =>
            {
                SpinWait.SpinUntil(() => Volatile.Read(ref sent) >= 200 || scope.Stop.IsCancellationRequested, Bounded).Should().BeTrue();

                for (var i = 0; i < resetsWanted && !scope.Stop.IsCancellationRequested; i++)
                {
                    var before = socket.Delivered;
                    client.EnableCompression(); // clears the queue under live senders
                    resets++;
                    SpinWait.SpinUntil(() => socket.Delivered != before, TimeSpan.FromMilliseconds(200)); // let something through between resets
                }

                Volatile.Write(ref stopSenders, true);
            });

            ready.Wait(Bounded).Should().BeTrue();
            start.Set();
            resetter.Join();

            foreach (var w in senders)
            {
                w.Join();
            }

            scope.Stop.Cancel();
            flusher.Join();
            client.Flush();

            resets.Should().Be(resetsWanted);
            var stormBytes = socket.Bytes.ToArray();
            AssertFramesIntact(stormBytes, threads, 0, allowDropped: true).Should().BeGreaterThan(0, "frames delivered between resets must be there, and whole");

            // a quiet tail after the storm must arrive completely (appended after what the storm delivered)
            var mark = socket.Bytes.Count;

            for (var s = 0; s < tail; s++)
            {
                client.Send(OwnFrame(0, 60000 + s), true);
            }

            client.Flush();
            AssertFramesIntact(socket.Bytes.ToArray().AsSpan(mark), threads, 0, allowDropped: true).Should().Be(tail);

            // nothing stale: after a final reset, a flush sends nothing
            client.EnableCompression();
            var before2 = socket.Bytes.Count;
            client.Flush();
            socket.Bytes.Count.Should().Be(before2);
        }

        // ---- connection identity ----

        // A plugin thread's Send is paused at the given point while the connection ends and the next one starts (reset, new
        // socket, login handshake, seed, EndHandshake); the paused send then resumes and must not reach the new connection.
        private static void StaleSendAcrossReconnect(Action<NetClient, CaptureSocket, Action> pauseAt)
        {
            var client = MakeClient(EncryptionType.NONE);
            var oldSocket = new CaptureSocket();
            client.AttachSocket(oldSocket);
            client.EndHandshake(client.BeginLoginHandshake(1));

            using var scope = new Scope();
            using var arrived = new SemaphoreSlim(0);
            using var proceed = new SemaphoreSlim(0);
            Thread pluginThread = null;
            var pausedOnce = false;

            void Pause()
            {
                if (Thread.CurrentThread == Volatile.Read(ref pluginThread) && !pausedOnce)
                {
                    pausedOnce = true;
                    arrived.Release();
                    proceed.Wait(Bounded).Should().BeTrue();
                }
            }

            pauseAt(client, oldSocket, Pause);

            var plugin = scope.Run(() =>
            {
                Volatile.Write(ref pluginThread, Thread.CurrentThread);
                client.Send(new byte[] { 0xEE, 0xEE, 0xEE }, true);
            });

            var newSocket = new CaptureSocket();

            try
            {
                arrived.Wait(Bounded).Should().BeTrue("the plugin send must be paused");

                // the connection ends and the next one starts: reset, a new socket, its handshake and seed
                client.Disconnect();
                client.AttachSocket(newSocket);
                var handshake = client.BeginLoginHandshake(2);
                client.Send(new byte[] { 1, 2, 3, 4 }, true, true);
                client.EndHandshake(handshake);
            }
            finally
            {
                proceed.Release();
            }

            plugin.Join();
            client.Flush();

            newSocket.Bytes.Should().Equal(new byte[] { 1, 2, 3, 4 }, "only the new connection's seed: no stale packet, before or after it");
            oldSocket.Bytes.Should().BeEmpty();
            client.DroppedStaleSends.Should().Be(1);
        }

        [Fact]
        public void A_Send_Paused_Before_The_Lock_Is_Dropped_After_Disconnect_And_Reconnect()
        {
            StaleSendAcrossReconnect((client, _, pause) => client.BeforeSendLock = pause);
        }

        [Fact]
        public void A_Send_Paused_In_The_Connection_Check_Is_Dropped_After_Disconnect_And_Reconnect()
        {
            // the sender saw the old socket as connected, paused there, and resumes with the new connection fully set up
            StaleSendAcrossReconnect((_, oldSocket, pause) => oldSocket.OnIsConnected = pause);
        }

        // ---- handshake ordering ----

        [Fact]
        public void Other_Threads_Sends_During_The_Game_Handshake_Follow_It_In_Order()
        {
            var client = MakeClient(EncryptionType.TWOFISH_MD5);
            client.AttachSocket(new CaptureSocket());
            client.Disconnect(); // sends are closed from a reset until a handshake ends
            var socket = new CaptureSocket();
            client.AttachSocket(socket);
            using var scope = new Scope();

            void PluginSend(byte marker) => scope.Run(() => client.Send(Send3(marker), true)).Join();

            PluginSend(0xA1); // before the handshake starts
            var handshake = client.BeginGameHandshake(Seed);
            PluginSend(0xA2); // game cipher initialised, seed not queued yet

            var seed = new byte[] { 0x7F, 0x00, 0x00, 0x01 };
            client.Send(seed, true, true);
            client.Send(OwnFrame(0, 0), true); // the handshake thread's own follow-up
            PluginSend(0xA3); // still inside the handshake

            client.EndHandshake(handshake);
            PluginSend(0xA4); // after it
            client.Flush();

            var bytes = socket.Bytes.ToArray();
            bytes.AsSpan(0, 4).ToArray().Should().Equal(seed, "the seed is the first thing on the connection, as plaintext");

            var rx = new TwofishEncryption();
            rx.Initialize(Seed, true);
            var plain = new byte[bytes.Length - 4];
            rx.Encrypt(bytes.AsSpan(4), plain, plain.Length);

            var expected = new List<byte>(OwnFrame(0, 0));

            foreach (var m in new byte[] { 0xA1, 0xA2, 0xA3, 0xA4 })
            {
                expected.AddRange(Send3(m));
            }

            plain.Should().Equal(expected, "the handshake frame first, then the held-back sends in the order they were made, then the later one");
            client.DeferredSends.Should().Be(3);
        }

        [Fact]
        public void A_Packet_Injected_By_The_Login_Hook_Follows_The_Login_Packet()
        {
            var client = MakeClient(EncryptionType.NONE);
            var socket = new CaptureSocket();
            client.AttachSocket(socket);
            client.Disconnect();
            client.AttachSocket(socket = new CaptureSocket());

            var login = new byte[] { 0x91, 1, 2, 3 };
            var ping = new byte[] { 0x73, 9 };
            client.TestOutgoingHook = (ref Span<byte> m) =>
            {
                if (m[0] == 0x91)
                {
                    client.Send(ping, true); // a plugin sends from inside the outgoing hook of the login packet
                }

                return true;
            };

            var handshake = client.BeginLoginHandshake(5);
            client.Send(new byte[] { 0, 0, 0, 5 }, true, true); // seed
            client.Send(login); // runs the hook
            client.EndHandshake(handshake);
            client.Flush();

            socket.Bytes.Should().Equal(new byte[] { 0, 0, 0, 5, 0x91, 1, 2, 3, 0x73, 9 }, "seed, login packet, then the injected packet");
        }

        [Fact]
        public void A_Plugin_That_Replaces_The_Login_Packet_Still_Gets_Its_Replacement_Queued()
        {
            var client = MakeClient(EncryptionType.NONE);
            client.AttachSocket(new CaptureSocket());
            client.Disconnect();
            var socket = new CaptureSocket();
            client.AttachSocket(socket);

            // suppress 0x91 and forward a replacement from inside the hook (re-entrantly)
            client.TestOutgoingHook = (ref Span<byte> m) =>
            {
                if (m[0] == 0x91)
                {
                    client.Send(new byte[] { 0x91, 7, 7, 7 }, true);

                    return false;
                }

                return true;
            };

            var handshake = client.BeginLoginHandshake(5);
            client.Send(new byte[] { 0, 0, 0, 5 }, true, true);
            client.Send(new byte[] { 0x91, 1, 2, 3 });
            client.EndHandshake(handshake);
            client.Flush();

            socket.Bytes.Should().Equal(new byte[] { 0, 0, 0, 5, 0x91, 7, 7, 7 });

            // and one that rewrites the packet in place (same call): accepted as the handshake packet
            client.Disconnect();
            client.AttachSocket(socket = new CaptureSocket());
            client.TestOutgoingHook = (ref Span<byte> m) =>
            {
                m = new byte[] { 0x91, 8, 8, 8 };

                return true;
            };
            handshake = client.BeginLoginHandshake(6);
            client.Send(new byte[] { 0, 0, 0, 6 }, true, true);
            client.Send(new byte[] { 0x91, 1, 2, 3 });
            client.EndHandshake(handshake);
            client.Flush();

            socket.Bytes.Should().Equal(new byte[] { 0, 0, 0, 6, 0x91, 8, 8, 8 });
        }

        [Fact]
        public void A_Reset_Closes_Sends_Until_The_Next_Handshake_Ends_And_Drops_What_Was_Held()
        {
            var (client, socket) = Make(EncryptionType.NONE);
            client.Disconnect();

            var socket2 = new CaptureSocket();
            client.AttachSocket(socket2);
            client.Send(new byte[] { 9, 9, 9 }, true);
            client.Flush();
            socket2.Bytes.Should().BeEmpty("a connection's sends wait for its handshake");

            client.EndHandshake(client.BeginLoginHandshake(3));
            client.Flush();
            socket2.Bytes.Should().Equal(9, 9, 9);

            client.Send(new byte[] { 7, 7, 7 }, true);
            client.Flush();
            socket2.Bytes.Should().Equal(9, 9, 9, 7, 7, 7);

            // a send held during a handshake is discarded with its connection, and a stale token doesn't reopen the next one
            var stale = client.BeginLoginHandshake(4);
            client.Disconnect();
            var socket3 = new CaptureSocket();
            client.AttachSocket(socket3);
            client.Send(new byte[] { 6, 6, 6 }, true); // held for the new connection
            client.EndHandshake(stale);
            client.Flush();
            socket3.Bytes.Should().BeEmpty("the stale token must not reopen the new connection");

            client.Disconnect();
            var socket4 = new CaptureSocket();
            client.AttachSocket(socket4);
            client.EndHandshake(client.BeginLoginHandshake(5));
            client.Flush();
            socket4.Bytes.Should().BeEmpty("what was held for a connection that ended is gone");
            socket.Bytes.Should().BeEmpty();
        }

        [Fact]
        public void Send_Statistics_Count_Every_Accepted_Send_And_Reset_With_The_Connection()
        {
            var (client, _) = Make(EncryptionType.NONE);
            const int threads = 4, per = 2000;
            using var scope = new Scope();
            var workers = new Worker[threads];

            for (var t = 0; t < threads; t++)
            {
                workers[t] = scope.Run(() =>
                {
                    for (var i = 0; i < per; i++)
                    {
                        client.Send(new byte[] { 1, 2 }, true);
                    }
                });
            }

            foreach (var w in workers)
            {
                w.Join();
            }

            client.Statistics.TotalPacketsSent.Should().Be(threads * per);
            client.Statistics.TotalBytesSent.Should().Be(threads * per * 2);

            client.Disconnect();
            client.Statistics.TotalPacketsSent.Should().Be(0);
        }
    }
}
