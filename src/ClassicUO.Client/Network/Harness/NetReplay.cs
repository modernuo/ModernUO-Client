// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using ClassicUO.Game;
using ClassicUO.Network.Encryption;
using ClassicUO.Network.Socket;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network.Harness
{
    /// <summary>How a replay cuts the released bytes into socket reads.</summary>
    internal enum ChunkMode
    {
        /// <summary>One read per recorded read.</summary>
        Recorded,
        /// <summary>One byte per read.</summary>
        Byte,
        /// <summary>Seeded random sizes from 1 to the read buffer.</summary>
        Random,
        /// <summary>Each read fills the read buffer across recorded boundaries.</summary>
        Max
    }

    /// <summary>
    /// A socket fed by a replay: bytes are released into it a frame group at a time and served to NetClient's reads cut by
    /// the <see cref="ChunkMode"/>. Sends go nowhere.
    /// </summary>
    internal sealed class ReplaySocket : SocketWrapper
    {
        private readonly Queue<byte[]> _chunks = new Queue<byte[]>();
        private readonly ChunkMode _mode;
        private readonly Random _rng;
        private byte[] _current = Array.Empty<byte>();
        private int _pos;
        private bool _connected;

        public ReplaySocket(ChunkMode mode, int seed)
        {
            _mode = mode;
            _rng = new Random(seed);
        }

        public int Pending { get; private set; }

        public override bool IsConnected => _connected;
        public override EndPoint LocalEndPoint => new IPEndPoint(IPAddress.Loopback, 0);
        public override int Available => Pending;

        public override void Connect(Uri uri)
        {
            _connected = true;
            InvokeOnConnected();
        }

        public void Release(byte[] chunk)
        {
            if (chunk.Length > 0)
            {
                _chunks.Enqueue(chunk);
                Pending += chunk.Length;
            }
        }

        public override void Send(byte[] buffer, int offset, int count) { }

        public override int Read(byte[] buffer)
        {
            if (!_connected || Pending == 0)
            {
                return 0;
            }

            int want = _mode switch
            {
                ChunkMode.Byte => 1,
                ChunkMode.Random => _rng.Next(1, buffer.Length + 1),
                _ => buffer.Length
            };

            int n = 0;

            while (n < want && Pending > 0)
            {
                if (_pos >= _current.Length)
                {
                    _current = _chunks.Dequeue();
                    _pos = 0;
                }

                int k = Math.Min(want - n, _current.Length - _pos);
                Array.Copy(_current, _pos, buffer, n, k);
                _pos += k;
                n += k;
                Pending -= k;

                // Recorded: a read ends at the recorded boundary
                if (_mode == ChunkMode.Recorded && _pos >= _current.Length)
                {
                    break;
                }
            }

            return n;
        }

        public override void Disconnect() => _connected = false;

        public override void Dispose() => _connected = false;
    }

    /// <summary>
    /// The replay of a capture (net-rework parity harness). Inbound records are grouped by the frame that read them; a group
    /// is released into its connection's <see cref="ReplaySocket"/> once the client has opened that connection, has sent as
    /// many flow packets (<see cref="NetCaptureFormat.IsGate"/>) on it as it had when the group was recorded, and the
    /// recorded time since the previous group has passed on the replay clock.
    /// </summary>
    internal sealed class ReplaySchedule
    {
        private readonly List<CaptureRecord> _records;
        private int _cursor;
        private uint _lastReleaseTicks;
        private uint _prevRecordedTicks;
        private bool _first = true;
        private uint _waitSince;
        private bool _waiting;

        /// <summary>Most replay-clock ms a group waits for its flow packets before it is released anyway (logged).</summary>
        public uint GateTimeoutMs = 30_000;

        /// <summary>Longest recorded gap reproduced, in ms (a longer idle is cut to this).</summary>
        public uint MaxGapMs = 60_000;

        public int GateTimeouts { get; private set; }

        public ReplaySchedule(Capture capture)
        {
            _records = capture.Records;
        }

        public bool Done => _cursor >= _records.Count;

        /// <summary>
        /// Releases what is due at <paramref name="now"/> (replay clock). <paramref name="socketFor"/> returns the socket of a
        /// connection the client has opened (null if it has not yet); <paramref name="gatesSent"/> the flow packets the client
        /// sent on it. Returns the bytes released.
        /// </summary>
        public int Advance(uint now, Func<int, ReplaySocket> socketFor, Func<int, int> gatesSent, Action<CaptureRecord> onConnect = null)
        {
            int released = 0;

            while (_cursor < _records.Count)
            {
                CaptureRecord rec = _records[_cursor];

                if (rec.Kind == CaptureKind.Connect)
                {
                    // the previous connection's released bytes are read before the next one opens
                    if (released > 0 || socketFor(rec.Conn) == null)
                    {
                        return released;
                    }

                    onConnect?.Invoke(rec);
                    _cursor++;

                    continue;
                }

                if (rec.Kind != CaptureKind.Inbound)
                {
                    _cursor++;

                    continue;
                }

                ReplaySocket socket = socketFor(rec.Conn);

                if (socket == null)
                {
                    return released;
                }

                if (released > 0)
                {
                    return released; // one group per call
                }

                if (!_first)
                {
                    uint gap = Math.Min(rec.Ticks - _prevRecordedTicks, MaxGapMs);

                    if (now - _lastReleaseTicks < gap)
                    {
                        return released;
                    }
                }

                if (gatesSent(rec.Conn) < rec.Gates)
                {
                    if (!_waiting)
                    {
                        _waiting = true;
                        _waitSince = now;
                    }

                    if (now - _waitSince < GateTimeoutMs)
                    {
                        return released;
                    }

                    GateTimeouts++;
                    Log.Warn($"[net-replay] group at record {_cursor} (conn {rec.Conn}) waited {GateTimeoutMs} ms for flow packet {rec.Gates}; the client sent {gatesSent(rec.Conn)}; released");
                }

                _waiting = false;
                _first = false;
                _prevRecordedTicks = rec.Ticks;
                _lastReleaseTicks = now;
                uint frame = rec.Frame;
                int conn = rec.Conn;

                while (_cursor < _records.Count)
                {
                    CaptureRecord r = _records[_cursor];

                    if (r.Kind == CaptureKind.Outbound || r.Kind == CaptureKind.Disconnect)
                    {
                        _cursor++;

                        continue;
                    }

                    if (r.Kind != CaptureKind.Inbound || r.Frame != frame || r.Conn != conn)
                    {
                        break;
                    }

                    socket.Release(r.Data);
                    released += r.Data.Length;
                    _cursor++;
                }
            }

            return released;
        }
    }

    /// <summary>
    /// Replays a capture through NetClient.Drain and PacketHandlers.ParsePackets with no client around them: no world, no
    /// handlers (unless a test adds some), no renderer. Each frame group is drained with the caps off. The trace has the framing columns only (the
    /// effect columns are o0). Used by the unit tests and by <c>CUO_NET_REPLAY_FILE</c> runs of the test project.
    /// </summary>
    internal static class HeadlessReplay
    {
        /// <param name="setup">Tests: registers handlers on the replay's (otherwise empty) handler table.</param>
        public static long Run(Capture capture, ChunkMode mode, TextWriter output, int seed = 1234, Action<PacketHandlers> setup = null)
        {
            var client = new NetClient();
            client.Load((ClientVersion)capture.ClientVersion, EncryptionType.NONE);
            var handlers = new PacketHandlers();
            setup?.Invoke(handlers);
            using var trace = new NetTrace(output, $"headless mode={mode}");
            handlers.Trace = trace;

            var sockets = new Dictionary<int, ReplaySocket>();
            var compressed = new HashSet<int>();
            var schedule = new ReplaySchedule(capture) { GateTimeoutMs = 0, MaxGapMs = 0 };
            int current = -1;
            var gameConns = FirstInboundCompressed(capture);
            int Sink(ArraySegment<byte> data) => handlers.ParsePackets(client, null, data);

            // the connection opens when its record comes up; every flow packet counts as sent (there is no client)
            ReplaySocket SocketFor(int conn)
            {
                if (!sockets.TryGetValue(conn, out ReplaySocket s))
                {
                    s = new ReplaySocket(mode, seed + conn);
                    sockets[conn] = s;
                    client.AttachReplaySocket(s);
                    s.Connect(null);
                    current = conn;
                }

                return s;
            }

            for (int frames = 0; !schedule.Done; frames++)
            {
                if (frames > 100_000_000)
                {
                    throw new InvalidOperationException("replay did not end");
                }

                schedule.Advance(0, SocketFor, _ => int.MaxValue);

                if (current >= 0 && !compressed.Contains(current) && sockets[current].Pending > 0 && gameConns.Contains(current))
                {
                    compressed.Add(current);
                    client.EnableCompression();
                }

                client.Drain(Sink, int.MaxValue, int.MaxValue, 0);
            }

            client.Drain(Sink, int.MaxValue, int.MaxValue, 0);
            trace.End(null);

            return trace.Packets;
        }

        // the connections whose first inbound slice was compressed (game server connections)
        private static HashSet<int> FirstInboundCompressed(Capture capture)
        {
            var seen = new HashSet<int>();
            var game = new HashSet<int>();

            foreach (CaptureRecord r in capture.Records)
            {
                if (r.Kind == CaptureKind.Inbound && seen.Add(r.Conn) && r.Compressed)
                {
                    game.Add(r.Conn);
                }
            }

            return game;
        }
    }

    /// <summary>
    /// CUO_NET_REPLAY=&lt;capture&gt;: the client runs a capture instead of a server. NetClient.Connect attaches a
    /// <see cref="ReplaySocket"/> for the n-th connection; the frame clock (Time.Ticks) advances a fixed step per Update
    /// (CUO_NET_REPLAY_STEP_MS, default 16), so what the client does between packets is the same in every replay; the drain
    /// caps are off. CUO_NET_REPLAY_CHUNKS=recorded|byte|random|max cuts the reads. The trace goes to CUO_NET_TRACE_OUT. When
    /// the capture is used up and 120 more frames have run, the trace ends and the client exits.
    /// </summary>
    internal static class NetReplay
    {
        public static readonly bool Active = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUO_NET_REPLAY"));

        private static ReplaySchedule _schedule;
        private static readonly List<ReplaySocket> _sockets = new List<ReplaySocket>();
        private static readonly List<int> _gates = new List<int>();
        private static ChunkMode _mode;
        private static uint _step = 16;
        private static uint _clock = 1_000_000;
        private static int _idleFrames = -1;
        private static bool _ended;

        private static void Init()
        {
            string path = Environment.GetEnvironmentVariable("CUO_NET_REPLAY");
            _schedule = new ReplaySchedule(Capture.Load(path));
            _mode = Enum.TryParse(Environment.GetEnvironmentVariable("CUO_NET_REPLAY_CHUNKS"), true, out ChunkMode m) ? m : ChunkMode.Recorded;

            if (uint.TryParse(Environment.GetEnvironmentVariable("CUO_NET_REPLAY_STEP_MS"), out uint step) && step > 0)
            {
                _step = step;
            }

            Log.Info($"[net-replay] {path}, chunks {_mode}, step {_step} ms");
        }

        /// <summary>NetClient.Connect: the socket of the next connection.</summary>
        public static SocketWrapper NextSocket()
        {
            var s = new ReplaySocket(_mode, 1234 + _sockets.Count);
            _sockets.Add(s);
            _gates.Add(0);

            return s;
        }

        /// <summary>A packet the client queued to send (any thread).</summary>
        public static void Sent(byte id)
        {
            if (NetCaptureFormat.IsGate(id))
            {
                lock (_gates)
                {
                    if (_gates.Count > 0)
                    {
                        _gates[_gates.Count - 1]++;
                    }
                }
            }
        }

        private static int GatesSent(int conn)
        {
            lock (_gates)
            {
                return conn < _gates.Count ? _gates[conn] : 0;
            }
        }

        /// <summary>Update, before the drain: the frame's clock, then what is due is released.</summary>
        public static uint Frame()
        {
            if (_schedule == null)
            {
                Init();
            }

            _clock += _step;

            if (_ended)
            {
                return _clock;
            }

            _schedule.Advance(_clock, c => c < _sockets.Count ? _sockets[c] : null, GatesSent);

            if (_schedule.Done)
            {
                if (_idleFrames < 0)
                {
                    _idleFrames = 0;
                    Log.Info($"[net-replay] capture used up at {_clock}; gate timeouts {_schedule.GateTimeouts}");
                }
                else if (++_idleFrames >= 120 && (_sockets.Count == 0 || _sockets[^1].Pending == 0))
                {
                    _ended = true;
                    long packets = NetHarness.TracePackets;
                    SpanBuffers.LogSummary(force: true);
                    NetHarness.EndTrace();
                    Log.Info($"[net-replay] done; packets {packets}; exiting");
                    Client.Game.Exit();
                }
            }

            return _clock;
        }
    }

    /// <summary>The harness's entry points in the client (all behind CUO_NET_CAPTURE, CUO_NET_REPLAY or CUO_NET_TRACE_OUT).</summary>
    internal static class NetHarness
    {
        private static readonly string _traceOut = Environment.GetEnvironmentVariable("CUO_NET_TRACE_OUT");

        public static readonly bool On = NetCapture.On || NetReplay.Active || !string.IsNullOrEmpty(_traceOut);

        private static NetTrace _trace;
        private static bool _started;

        public static long TracePackets => _trace?.Packets ?? 0;

        /// <summary>GameController.Update, before the drain: starts the trace, runs the replay clock.</summary>
        public static void Frame()
        {
            if (!_started)
            {
                _started = true;

                if (!string.IsNullOrEmpty(_traceOut))
                {
                    bool ops = Environment.GetEnvironmentVariable("CUO_NET_TRACE_OPS") == "1";
                    int every = int.TryParse(Environment.GetEnvironmentVariable("CUO_NET_TRACE_CHECKPOINT"), out int n) ? n : 500;
                    string what = NetReplay.Active
                        ? $"replay={Path.GetFileName(Environment.GetEnvironmentVariable("CUO_NET_REPLAY"))} chunks={Environment.GetEnvironmentVariable("CUO_NET_REPLAY_CHUNKS") ?? "recorded"}"
                        : "live";
                    _trace = new NetTrace(new StreamWriter(_traceOut, false, new System.Text.UTF8Encoding(false), 1 << 16), what, ops, every);
                    PacketHandlers.Handler.Trace = _trace;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => _trace?.Flush();
                }
            }

            if (NetReplay.Active)
            {
                Time.Ticks = NetReplay.Frame();
            }
        }

        public static void EndTrace()
        {
            if (_trace != null)
            {
                _trace.End(Client.Game?.UO?.World);
                PacketHandlers.Handler.Trace = null;
                _trace.Dispose();
                _trace = null;
            }
        }
    }
}
#endif
