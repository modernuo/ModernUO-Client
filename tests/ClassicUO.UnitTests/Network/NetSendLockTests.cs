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
    // Send-side lock: encrypt + enqueue is one critical section, and every clear of the send queue takes the same lock.
    // The game-server stream cipher (Twofish/MD5 keystream) is a process-wide static, so these tests must not run in parallel
    // with each other.
    [Collection("NetSendLock")]
    public class NetSendLockTests
    {
        private const uint Seed = 0x7F000001;

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

        private static (NetClient client, CaptureSocket socket) Make(EncryptionType type)
        {
            var client = new NetClient();
            client.Load(ClientVersion.CV_70130, type);
            var socket = new CaptureSocket();
            client.AttachSocket(socket);

            if (type != EncryptionType.NONE)
            {
                client.Encryption!.Initialize(false, Seed);
            }

            client.EnableCompression(); // game-server mode: the keystream cipher, not the login cipher

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

        private static void AssertFramesIntact(byte[] stream, int threads, int perThread, bool allowDropped)
        {
            var next = new int[threads];
            var pos = 0;

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
            }

            if (!allowDropped)
            {
                next.Should().AllBeEquivalentTo(perThread);
            }
        }

        [Fact]
        public void Concurrent_Sends_Reach_The_Receiver_Intact_And_In_Cipher_Order()
        {
            const int threads = 6, perThread = 3000;
            var (client, socket) = Make(EncryptionType.TWOFISH_MD5);

            // receiver: the same keystream, from its own cipher instance (XOR, so "encrypt" decrypts)
            var rx = new TwofishEncryption();
            rx.Initialize(Seed, true);

            using var start = new ManualResetEventSlim();
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

            for (var t = 0; t < threads; t++)
            {
                var thread = t;
                senders[t] = Task.Factory.StartNew(() =>
                {
                    start.Wait();

                    for (var s = 0; s < perThread; s++)
                    {
                        client.Send(Frame(thread, s, FrameLen(thread, s)), true);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            start.Set();
            Task.WaitAll(senders);
            Volatile.Write(ref flushing, false);
            flusher.Wait();
            client.Flush();

            var cipher = socket.Bytes.ToArray();
            var plain = new byte[cipher.Length];
            rx.Encrypt(cipher, plain, cipher.Length);

            AssertFramesIntact(plain, threads, perThread, allowDropped: false);
        }

        [Fact]
        public void Reset_During_A_Flush_Does_Not_Tear_The_Queue()
        {
            // 100 frames of 100 bytes: more than one 4096-byte send chunk. A reset requested while the flush is between
            // chunks must wait for it (the lock), not clear the rest of the queue under it.
            var (client, socket) = Make(EncryptionType.NONE);
            var total = 0;

            for (var s = 0; s < 100; s++)
            {
                var f = Frame(0, s, 100);
                f[0] = 100;
                total += f.Length;
                client.Send(f, true);
            }

            Task reset = null;
            socket.OnFirstSend = () =>
            {
                reset = Task.Run(() => client.EnableCompression());
                reset.Wait(300); // with the lock it can't finish yet; without it, it clears the queue now
            };

            client.Flush();
            reset.Wait();

            socket.Bytes.Count.Should().Be(total, "the flush that was running finished before the reset cleared anything");
        }

        [Fact]
        public void Resets_During_Concurrent_Sends_Leave_Only_Whole_Frames()
        {
            const int threads = 4, perThread = 4000;
            var (client, socket) = Make(EncryptionType.NONE);
            using var start = new ManualResetEventSlim();
            var running = true;

            var flusher = Task.Run(() =>
            {
                while (Volatile.Read(ref running))
                {
                    client.Flush();
                    Thread.Yield();
                }
            });

            var resetter = Task.Run(() =>
            {
                start.Wait();

                while (Volatile.Read(ref running))
                {
                    client.EnableCompression(); // clears the queue
                    Thread.Sleep(0);
                }
            });

            var senders = new Task[threads];

            for (var t = 0; t < threads; t++)
            {
                var thread = t;
                senders[t] = Task.Factory.StartNew(() =>
                {
                    start.Wait();

                    for (var s = 0; s < perThread; s++)
                    {
                        client.Send(Frame(thread, s, FrameLen(thread, s)), true);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            start.Set();
            Task.WaitAll(senders);
            Volatile.Write(ref running, false);
            Task.WaitAll(flusher, resetter);
            client.Flush();

            // frames may be dropped by a reset, never torn, duplicated or reordered
            AssertFramesIntact(socket.Bytes.ToArray(), threads, perThread, allowDropped: true);

            // and nothing stale: after a final reset, a flush sends nothing
            client.EnableCompression();
            var before = socket.Bytes.Count;
            client.Flush();
            socket.Bytes.Count.Should().Be(before);
        }
    }
}
