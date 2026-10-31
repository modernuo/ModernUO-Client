using System;
using System.Collections.Generic;
using System.Net;
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
    // hope to overlap; every wait is bounded and every worker is joined in a finally.
    [Collection("NetSendLock")]
    public class NetSendLockTests
    {
        private const uint Seed = 0x7F000001;
        private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(30);

        private sealed class CaptureSocket : SocketWrapper
        {
            public readonly List<byte> Bytes = new List<byte>();
            public Action OnFirstSend;
            private int _sends;

            public override bool IsConnected => true;
            public override EndPoint LocalEndPoint => null;
            public override void Connect(Uri uri) { }

            public override void Send(byte[] buffer, int offset, int count)
            {
                Bytes.AddRange(new ArraySegment<byte>(buffer, offset, count));

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
        private static int AssertFramesIntact(byte[] stream, int threads, int perThread, bool allowDropped)
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

        private static void Join(Thread t) => t.Join(Bounded).Should().BeTrue("worker thread must finish");

        [Fact]
        public void Concurrent_Sends_Reach_The_Receiver_Intact_And_In_Cipher_Order()
        {
            const int threads = 6, perThread = 3000;
            var (client, socket) = Make(EncryptionType.TWOFISH_MD5);

            // receiver: the same keystream, from its own cipher instance (XOR, so "encrypt" decrypts)
            var rx = new TwofishEncryption();
            rx.Initialize(Seed, true);

            using var start = new ManualResetEventSlim();
            using var ready = new CountdownEvent(threads);
            var flushing = true;
            var flusher = Task.Run(() =>
            {
                while (Volatile.Read(ref flushing))
                {
                    client.Flush();
                    Thread.Yield();
                }
            });

            var senders = new Task[threads];

            try
            {
                for (var t = 0; t < threads; t++)
                {
                    var thread = t;
                    senders[t] = Task.Factory.StartNew(() =>
                    {
                        ready.Signal();
                        start.Wait(Bounded);

                        for (var s = 0; s < perThread; s++)
                        {
                            client.Send(OwnFrame(thread, s), true);
                        }
                    }, TaskCreationOptions.LongRunning);
                }

                ready.Wait(Bounded).Should().BeTrue();
                start.Set();
                Task.WaitAll(senders, Bounded).Should().BeTrue();
            }
            finally
            {
                start.Set();
                Volatile.Write(ref flushing, false);
                flusher.Wait(Bounded).Should().BeTrue();
            }

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
            var total = 0;

            for (var s = 0; s < 100; s++)
            {
                var f = Frame(0, s, 100);
                total += f.Length;
                client.Send(f, true);
            }

            Thread reset = null;
            var wasBlocked = false;
            socket.OnFirstSend = () =>
            {
                reset = new Thread(() => client.EnableCompression()) { IsBackground = true };
                reset.Start();
                wasBlocked = WaitUntilBlocked(reset); // the reset has started and is waiting for the lock we hold
            };

            try
            {
                client.Flush();
            }
            finally
            {
                if (reset != null)
                {
                    Join(reset);
                }
            }

            wasBlocked.Should().BeTrue("the reset must be waiting on the send lock while the flush runs");
            socket.Bytes.Count.Should().Be(total, "the flush that was running finished before the reset cleared anything");
        }

        [Fact]
        public void Resets_During_Concurrent_Sends_Leave_Only_Whole_Frames()
        {
            const int threads = 4, perThread = 4000, tail = 50;
            var (client, socket) = Make(EncryptionType.NONE);
            using var start = new ManualResetEventSlim();
            using var ready = new CountdownEvent(threads + 2);
            var running = true;
            var resets = 0;

            var flusher = Task.Run(() =>
            {
                ready.Signal();
                start.Wait(Bounded);

                while (Volatile.Read(ref running))
                {
                    client.Flush();
                    Thread.Yield();
                }
            });

            var resetter = Task.Run(() =>
            {
                ready.Signal();
                start.Wait(Bounded);

                while (Volatile.Read(ref running))
                {
                    client.EnableCompression(); // clears the queue
                    resets++;
                    Thread.Yield();
                }
            });

            var senders = new Task[threads];

            try
            {
                for (var t = 0; t < threads; t++)
                {
                    var thread = t;
                    senders[t] = Task.Factory.StartNew(() =>
                    {
                        ready.Signal();
                        start.Wait(Bounded);

                        for (var s = 0; s < perThread; s++)
                        {
                            client.Send(OwnFrame(thread, s), true);
                        }
                    }, TaskCreationOptions.LongRunning);
                }

                ready.Wait(Bounded).Should().BeTrue();
                start.Set();
                Task.WaitAll(senders, Bounded).Should().BeTrue();
            }
            finally
            {
                start.Set();
                Volatile.Write(ref running, false);
                Task.WaitAll(new[] { flusher, resetter }, Bounded).Should().BeTrue();
            }

            resets.Should().BeGreaterThan(0);

            // a quiet tail after the storm: these frames must all arrive, so the stream can't be vacuously empty
            client.Flush();
            socket.Bytes.Clear();

            for (var s = perThread; s < perThread + tail; s++)
            {
                client.Send(OwnFrame(0, s), true);
            }

            client.Flush();
            AssertFramesIntact(socket.Bytes.ToArray(), threads, perThread, allowDropped: true).Should().Be(tail);

            // and the frames delivered during the storm were whole too (checked as they were captured)
            // nothing stale: after a final reset, a flush sends nothing
            client.EnableCompression();
            var before = socket.Bytes.Count;
            client.Flush();
            socket.Bytes.Count.Should().Be(before);
        }

        [Fact]
        public void Resets_During_Concurrent_Sends_Never_Tear_A_Frame()
        {
            // same storm, but checks the whole captured stream (frames delivered while the resets ran), not only the tail
            const int threads = 4, perThread = 3000;
            var (client, socket) = Make(EncryptionType.NONE);
            using var start = new ManualResetEventSlim();
            using var ready = new CountdownEvent(threads + 2);
            var running = true;

            var flusher = Task.Run(() =>
            {
                ready.Signal();
                start.Wait(Bounded);

                while (Volatile.Read(ref running))
                {
                    client.Flush();
                    Thread.Yield();
                }
            });

            var resetter = Task.Run(() =>
            {
                ready.Signal();
                start.Wait(Bounded);

                while (Volatile.Read(ref running))
                {
                    client.EnableCompression();
                    Thread.Sleep(0);
                }
            });

            var senders = new Task[threads];

            try
            {
                for (var t = 0; t < threads; t++)
                {
                    var thread = t;
                    senders[t] = Task.Factory.StartNew(() =>
                    {
                        ready.Signal();
                        start.Wait(Bounded);

                        for (var s = 0; s < perThread; s++)
                        {
                            client.Send(OwnFrame(thread, s), true);
                        }
                    }, TaskCreationOptions.LongRunning);
                }

                ready.Wait(Bounded).Should().BeTrue();
                start.Set();
                Task.WaitAll(senders, Bounded).Should().BeTrue();
            }
            finally
            {
                start.Set();
                Volatile.Write(ref running, false);
                Task.WaitAll(new[] { flusher, resetter }, Bounded).Should().BeTrue();
            }

            client.Flush();
            AssertFramesIntact(socket.Bytes.ToArray(), threads, perThread, allowDropped: true);
        }

        // ---- connection identity and handshake ordering ----

        [Fact]
        public void A_Send_Started_On_The_Old_Connection_Is_Dropped_After_Disconnect_And_Reconnect()
        {
            var client = MakeClient(EncryptionType.NONE);
            var oldSocket = new CaptureSocket();
            client.AttachSocket(oldSocket);
            client.EndHandshake(client.BeginLoginHandshake(1));

            using var arrived = new SemaphoreSlim(0);
            using var proceed = new SemaphoreSlim(0);
            Thread pluginThread = null;

            client.BeforeSendLock = () =>
            {
                if (Thread.CurrentThread == pluginThread)
                {
                    arrived.Release();
                    proceed.Wait(Bounded).Should().BeTrue();
                }
            };

            pluginThread = new Thread(() => client.Send(new byte[] { 0xEE, 0xEE, 0xEE }, true)) { IsBackground = true };
            pluginThread.Start();

            try
            {
                arrived.Wait(Bounded).Should().BeTrue("the plugin send must be paused between its hooks and the lock");

                // the connection ends and the next one starts: reset, a new socket, its handshake and seed
                client.Disconnect();
                var newSocket = new CaptureSocket();
                client.AttachSocket(newSocket);
                var handshake = client.BeginLoginHandshake(2);
                client.Send(new byte[] { 1, 2, 3, 4 }, true, true); // the seed
                client.EndHandshake(handshake);

                proceed.Release();
                Join(pluginThread);
                client.Flush();

                newSocket.Bytes.Should().Equal(new byte[] { 1, 2, 3, 4 }, "the stale send must not reach the new connection, before or after the seed");
                oldSocket.Bytes.Should().BeEmpty();
            }
            finally
            {
                proceed.Release();
                Join(pluginThread);
            }
        }

        [Fact]
        public void Nothing_Is_Queued_Ahead_Of_The_Seed_During_The_Game_Handshake()
        {
            var client = MakeClient(EncryptionType.TWOFISH_MD5);
            client.AttachSocket(new CaptureSocket());
            client.Disconnect(); // sends are closed from a reset until a handshake ends
            var socket = new CaptureSocket();
            client.AttachSocket(socket);

            // a plugin thread sends before the handshake starts, and again between EnableCompression-equivalent and the seed
            void PluginSend(byte marker)
            {
                var t = new Thread(() => client.Send(new byte[] { marker, marker, marker }, true)) { IsBackground = true };
                t.Start();
                Join(t);
            }

            PluginSend(0xA1);
            var handshake = client.BeginGameHandshake(Seed);
            PluginSend(0xA2); // game cipher initialised, seed not queued yet

            var seed = new byte[] { 0x7F, 0x00, 0x00, 0x01 };
            client.Send(seed, true, true);
            client.Send(OwnFrame(0, 0), true); // the handshake thread's own follow-up
            PluginSend(0xA3); // still inside the handshake

            client.EndHandshake(handshake);
            var after = OwnFrame(1, 0);
            var t2 = new Thread(() => client.Send(after, true)) { IsBackground = true };
            t2.Start();
            Join(t2);

            client.Flush();

            var bytes = socket.Bytes.ToArray();
            bytes.AsSpan(0, 4).ToArray().Should().Equal(seed, "the seed is the first thing on the connection, as plaintext");

            var rx = new TwofishEncryption();
            rx.Initialize(Seed, true);
            var plain = new byte[bytes.Length - 4];
            rx.Encrypt(bytes.AsSpan(4), plain, plain.Length);

            // exactly the handshake thread's frame, then the post-handshake frame
            var first = OwnFrame(0, 0);
            plain.AsSpan(0, first.Length).ToArray().Should().Equal(first);
            plain.AsSpan(first.Length).ToArray().Should().Equal(OwnFrame(1, 0)); // (Send encrypts its buffer in place)
        }

        [Fact]
        public void A_Reset_Closes_Sends_Until_The_Next_Handshake_Ends()
        {
            var (client, socket) = Make(EncryptionType.NONE);
            client.Disconnect();

            var socket2 = new CaptureSocket();
            client.AttachSocket(socket2);
            client.Send(new byte[] { 9, 9, 9 }, true);
            client.Flush();
            socket2.Bytes.Should().BeEmpty("a connection's sends are closed until its handshake has been queued");

            client.EndHandshake(client.BeginLoginHandshake(3));
            client.Send(new byte[] { 7, 7, 7 }, true);
            client.Flush();
            socket2.Bytes.Should().Equal(7, 7, 7);

            // a handshake token from a connection that has been replaced does not reopen the new one
            var stale = client.BeginLoginHandshake(4);
            client.Disconnect();
            client.EndHandshake(stale);
            var socket3 = new CaptureSocket();
            client.AttachSocket(socket3);
            client.Send(new byte[] { 8, 8, 8 }, true);
            client.Flush();
            socket3.Bytes.Should().BeEmpty();
            socket.Bytes.Should().BeEmpty();
        }

        [Fact]
        public void Send_Statistics_Count_Every_Accepted_Send_And_Reset_With_The_Connection()
        {
            var (client, _) = Make(EncryptionType.NONE);
            const int threads = 4, per = 2000;
            var tasks = new Task[threads];

            for (var t = 0; t < threads; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    for (var i = 0; i < per; i++)
                    {
                        client.Send(new byte[] { 1, 2 }, true);
                    }
                });
            }

            Task.WaitAll(tasks, Bounded).Should().BeTrue();
            client.Statistics.TotalPacketsSent.Should().Be(threads * per);
            client.Statistics.TotalBytesSent.Should().Be(threads * per * 2);

            client.Disconnect();
            client.Statistics.TotalPacketsSent.Should().Be(0);
        }

        // ---- ordered async writer (the WebSocket send path) ----

        [Fact]
        public async Task Ordered_Writer_Writes_One_At_A_Time_In_Order()
        {
            var inFlight = 0;
            var maxInFlight = 0;
            var written = new List<byte>();
            var writer = new OrderedAsyncWriter(async (data, ct) =>
            {
                var now = Interlocked.Increment(ref inFlight);
                maxInFlight = Math.Max(maxInFlight, now);
                await Task.Yield();
                written.AddRange(data.ToArray());
                Interlocked.Decrement(ref inFlight);
            }, _ => { }, CancellationToken.None);

            const int threads = 4, per = 200;
            var tasks = new Task[threads];

            for (var t = 0; t < threads; t++)
            {
                var thread = t;
                tasks[t] = Task.Run(() =>
                {
                    for (var i = 0; i < per; i++)
                    {
                        writer.TryWrite(new[] { (byte)thread, (byte)i, (byte)(i >> 8) }, 0, 3).Should().BeTrue();
                    }
                });
            }

            Task.WaitAll(tasks, Bounded).Should().BeTrue();
            writer.Complete();
            (await Task.WhenAny(writer.Completion, Task.Delay(Bounded))).Should().BeSameAs(writer.Completion);

            maxInFlight.Should().Be(1, "SendAsync calls on one ClientWebSocket must never overlap");
            written.Count.Should().Be(threads * per * 3);

            var next = new int[threads];

            for (var p = 0; p < written.Count; p += 3)
            {
                var thread = written[p];
                var i = written[p + 1] | (written[p + 2] << 8);
                i.Should().Be(next[thread]++, "writes of one thread stay in order");
            }
        }

        [Fact]
        public async Task Ordered_Writer_Reports_A_Failure_Once_And_Drops_The_Rest()
        {
            var failures = new List<Exception>();
            var written = new List<byte>();
            var writes = 0;
            var writer = new OrderedAsyncWriter((data, ct) =>
            {
                if (++writes == 3)
                {
                    throw new InvalidOperationException("socket closed");
                }

                written.Add(data.Span[0]);

                return ValueTask.CompletedTask;
            }, e => failures.Add(e), CancellationToken.None);

            for (byte i = 1; i <= 10; i++)
            {
                writer.TryWrite(new[] { i }, 0, 1);
            }

            writer.Complete();
            (await Task.WhenAny(writer.Completion, Task.Delay(Bounded))).Should().BeSameAs(writer.Completion);

            failures.Should().ContainSingle().Which.Message.Should().Be("socket closed");
            written.Should().Equal(new byte[] { 1, 2 }, "nothing after the failed write is written");
            writer.Failed.Should().BeTrue();
            writer.TryWrite(new byte[] { 99 }, 0, 1).Should().BeFalse();
        }
    }
}
