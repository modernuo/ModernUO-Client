// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Network.Encryption;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Threading;
using ClassicUO.Network.Socket;

namespace ClassicUO.Network
{
    internal sealed class NetClient
    {
        private const int BUFF_SIZE = 0x10000;

        private readonly byte[] _compressedBuffer = new byte[4096];
        private readonly byte[] _uncompressedBuffer = new byte[BUFF_SIZE];
        private readonly byte[] _sendingBuffer = new byte[4096];
        private readonly Huffman _huffman = new Huffman();
        private bool _isCompressionEnabled;
        private uint? _localIP;
        private readonly CircularBuffer _sendStream;

        // net_ring (NetRings): the receive ring (main thread only) and the bounded send ring (under _sendLock), mapped on the
        // first connection; null with the switch off or when the platform cannot map them (the CircularBuffer path then).
        private NetRing _recvRing, _sendRing;
        private bool _ringsTried;
        private int _ringGeneration;
        private int _lastRingSlice;
        private bool _sendExhausted;
        private bool _ringFullLogged;

        // The send lock. It guards _sendStream, _isCompressionEnabled as the send side reads it, the send-side cipher
        // (Encryption.Encrypt and Initialize), the send admission state below, and the send statistics: a packet is
        // encrypted and queued in one critical section, so the order the stream cipher consumed keystream is the order
        // bytes reach the socket. ProcessSend holds it while it drains the queue to the socket, and every clear or mode
        // switch of the queue takes it. Nothing that calls out (events, the Disconnected handlers) ever runs
        // while it is held.
        //
        // Send admission (all under _sendLock): a send is queued only if it started in the current send epoch (the epoch
        // is bumped by every ResetProtocolState, i.e. when a connection starts, ends or is replaced, so a send another thread
        // began on the old connection cannot land in the new one's queue) and sends are open. They are closed from a reset
        // until the connection's handshake (cipher initialisation, seed, first login packet) has been queued in order by
        // the thread running it (BeginLoginHandshake / BeginGameHandshake ... EndHandshake); only that thread may send in
        // the meantime. The three cipher objects in Encryption.cs are static, so this protocol assumes the one NetClient
        // the client has (NetClient.Socket).
        private readonly object _sendLock = new object();
        private bool _sendOpen = true;  // a NetClient that never connected has no connection to protect
        private int _sendEpoch;
        private int _handshakeThread;   // managed thread id of the thread running the handshake, 0 = none

        /// <summary>Test seam, null in production: called by <see cref="Send"/> after the outgoing hooks, before the send lock.</summary>
        internal Action BeforeSendLock;

        private SocketWrapper _socket = null;


        public NetClient()
        {
            Statistics = new NetStatistics(this);
            _sendStream = new CircularBuffer();
        }

        public static NetClient Socket { get; private set; } = new();

        public EncryptionType Load(ClientVersion clientVersion, EncryptionType encryption)
        {
            PacketsTable = new PacketsTable(clientVersion);
#if CUO_DEVTOOLS
            if (Harness.NetCapture.On) Harness.NetCapture.Load((uint)clientVersion);
#endif

            if (encryption != 0)
            {
                Encryption = new EncryptionHelper(clientVersion);
                Log.Trace("Calculating encryption by client version...");
                Log.Trace($"encryption: {Encryption.EncryptionType}");

                if (Encryption.EncryptionType != encryption)
                {
                    Log.Warn($"Encryption found: {Encryption.EncryptionType}");
                    encryption = Encryption.EncryptionType;
                }
            }

            return encryption;
        }


        public bool IsConnected => _socket != null && _socket.IsConnected;
        public NetStatistics Statistics { get; }
        public EncryptionHelper? Encryption { get; private set; }
        public PacketsTable PacketsTable { get; private set; }

        public uint LocalIP
        {
            get
            {
                if (!_localIP.HasValue)
                {
                    try
                    {
                        byte[] addressBytes = (_socket?.LocalEndPoint as IPEndPoint)?.Address.MapToIPv4().GetAddressBytes();

                        if (addressBytes != null && addressBytes.Length != 0)
                        {
                            _localIP = (uint)(addressBytes[0] | (addressBytes[1] << 8) | (addressBytes[2] << 16) | (addressBytes[3] << 24));
                        }

                        if (!_localIP.HasValue || _localIP == 0)
                        {
                            _localIP = 0x100007f;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"error while retriving local endpoint address: \n{ex}");

                        _localIP = 0x100007f;
                    }
                }

                return _localIP.Value;
            }
        }


        public event EventHandler Connected;
        public event EventHandler<SocketError> Disconnected;


        internal void AttachSocket(SocketWrapper socket)
        {
            EnsureRings();
            _socket?.Dispose();
            _socket = socket;
            TransportVersion++;

            _socket.OnConnected += (o, e) =>
            {
                ResetStatistics();
                Connected?.Invoke(this, EventArgs.Empty);
            };

            // A close or error may be reported from any thread (a transport's own receive thread). The callbacks only record it
            // against the connection they belong to; Drain, on the update thread, reads that connection's data first and only
            // then runs Terminate. A connection already ended or replaced (Retired) ignores late callbacks, and the first
            // close wins, so two reports of one close (an exception and then the end of the receive loop) are one Terminate.
            var conn = new Connection();
            _connection?.Retire();
            _connection = conn;

            _socket.OnDisconnected += (_, _) => conn.Report(SocketError.Success);
            _socket.OnError += (_, e) => conn.Report(e);
        }

        private sealed class Connection
        {
            private int _closed;
            private volatile bool _retired;
            private SocketError _error;

            public bool Retired => _retired;

            /// <summary>Set once a close or error was reported (and not retired).</summary>
            public bool Closed => !_retired && Volatile.Read(ref _closed) == 2;

            public SocketError Error => _error;

            public void Retire() => _retired = true;

            public void Report(SocketError error)
            {
                if (_retired)
                    return;

                if (Interlocked.CompareExchange(ref _closed, 1, 0) == 0)
                {
                    _error = error;
                    Volatile.Write(ref _closed, 2);
                }
            }
        }

        private Connection _connection;

        /// <summary>event loop: wakes the main loop's wait (null with the legacy loop).</summary>
        internal static Action SendQueuedWake;

        /// <summary>Bumped whenever a new socket is attached (the event loop binds each one to its wait once).</summary>
        internal int TransportVersion { get; private set; }

        /// <summary>event loop (Windows): the current socket signals <paramref name="evt"/> when readable or closed.</summary>
        internal bool TryEventSelect(IntPtr evt) => _socket != null && _socket.TryEventSelect(evt);

        /// <summary>event loop (Linux epoll, macOS kqueue): the connected socket's fd; false when there is none or on Windows.</summary>
        internal bool TryGetPollFd(out int fd)
        {
            fd = -1;

            return _socket != null && _socket.TryGetPollFd(out fd);
        }

        /// <summary>Bumped whenever a connection starts or ends; the packet parser drops its partial data when it changes.</summary>
        public int ConnectionGeneration => Volatile.Read(ref _generation);

        private int _generation;

        /// <summary>
        /// Puts the protocol back to its start state: login-server mode (no compression, so no game-server encryption mode), a
        /// fresh Huffman state, nothing queued to send, and a new connection generation.
        /// </summary>
        private void ResetProtocolState()
        {
            lock (_sendLock)
            {
                _isCompressionEnabled = false;
                _sendStream.Clear();
                _sendRing?.DiscardReadable();
                _sendExhausted = false;
#if CUO_DEVTOOLS
                if (Diagnostics.On) _diagWritten = _diagQueued;
#endif
                _sendOpen = false;
                _handshakeThread = 0;
                _deferred.Clear();
                _sendEpoch++;
            }

            _huffman.Reset();
            Interlocked.Increment(ref _generation);
        }

        /// <summary>
        /// The one place a connection ends from the receive or send side (peer close, reset, socket error, a bad stream):
        /// protocol state is reset first, so whatever the Disconnected handlers do next (a reconnect to the login server)
        /// starts clean, then the socket is closed and the handlers are told.
        /// </summary>
        private void Terminate(SocketError error, Connection only = null)
        {
            // Idempotent per connection: a connection that already ended (a malformed-header ProtocolError inside the parser
            // pump, then the Drain that saw the close) or was replaced (a Disconnected handler reconnected) is not ended again.
            var current = _connection;

            if (current != null && (current.Retired || (only != null && !ReferenceEquals(only, current))))
            {
                return;
            }

            current?.Retire(); // late callbacks of this connection, and its unread data, are void from here on
            ResetProtocolState();
            ResetStatistics();
            _socket?.Disconnect();
            Disconnected?.Invoke(this, error);
        }

        /// <summary>The stream can't be parsed any more (a malformed packet header): drop the connection.</summary>
        internal void ProtocolError()
        {
            Terminate(SocketError.ProtocolNotSupported);
        }

        /// <summary>
        /// Why the client dropped the last connection, for the player (span_reader: a packet a handler could not read), or
        /// null. Set before the Disconnected handlers run; cleared by the next Connect.
        /// </summary>
        public string DisconnectReason { get; private set; }

        /// <summary>A packet could not be read (span_reader): drop the connection with a reason shown to the player.</summary>
        internal void ProtocolError(string reason)
        {
            DisconnectReason = reason;
            Terminate(SocketError.ProtocolNotSupported);
        }

        public void Connect(string ip, ushort port)
        {
            DisconnectReason = null;
            ResetProtocolState();
            ResetStatistics();

            if (string.IsNullOrEmpty(ip))
                throw new ArgumentNullException(nameof(ip));

            var addr = $"tcp://{ip}:{port}";

            if (!Uri.TryCreate(addr, UriKind.RelativeOrAbsolute, out var uri))
                throw new UriFormatException($"NetClient::Connect() invalid Uri {addr}");

            Log.Trace($"Connecting to {uri}");
#if CUO_DEVTOOLS
            if (Harness.NetCapture.On) Harness.NetCapture.Connect();

            if (Harness.NetReplay.Active)
            {
                // net-rework harness: the n-th connection reads the capture's n-th stream
                AttachSocket(Harness.NetReplay.NextSocket());
                _socket.Connect(uri);

                return;
            }
#endif

            AttachSocket(new TcpSocketWrapper());
            _socket.Connect(uri);
        }

        public void Disconnect()
        {
#if CUO_DEVTOOLS
            if (Harness.NetCapture.On) Harness.NetCapture.Disconnect();
#endif
            _connection?.Retire();
            ResetProtocolState();
            ResetStatistics();
            _socket?.Disconnect();
        }

#if CUO_DEVTOOLS
        /// <summary>net-rework harness (headless replay): a new connection on <paramref name="socket"/>, as Connect starts one.</summary>
        internal void AttachReplaySocket(SocketWrapper socket)
        {
            ResetProtocolState();
            ResetStatistics();
            AttachSocket(socket);
        }
#endif

        private void ResetStatistics()
        {
            lock (_sendLock)
            {
                Statistics.Reset();
            }
        }

        /// <summary>
        /// Starts the login-server handshake for the current connection: initialises the login cipher (under the send lock)
        /// and makes the calling thread the only one allowed to send until <see cref="EndHandshake"/>. Returns the token for it.
        /// </summary>
        public int BeginLoginHandshake(uint address)
        {
            lock (_sendLock)
            {
                Encryption?.Initialize(true, address);
                _handshakeThread = Environment.CurrentManagedThreadId;

                return _sendEpoch;
            }
        }

        /// <summary>
        /// Starts the game-server handshake: initialises the game cipher, switches to game mode (what
        /// <see cref="EnableCompression"/> does) and makes the calling thread the only one allowed to send until
        /// <see cref="EndHandshake"/>, so nothing can be queued ahead of the seed.
        /// </summary>
        public int BeginGameHandshake(uint seed)
        {
            int token;

            lock (_sendLock)
            {
                Encryption?.Initialize(false, seed);
                _isCompressionEnabled = true;
                _sendStream.Clear();
                _sendRing?.DiscardReadable();
#if CUO_DEVTOOLS
                if (Diagnostics.On) _diagWritten = _diagQueued;
#endif
                _handshakeThread = Environment.CurrentManagedThreadId;
                token = _sendEpoch;
            }

            _huffman.Reset();

            return token;
        }

        /// <summary>
        /// The handshake packets are queued in order: ordinary sends (other threads) are admitted again. Does nothing
        /// if the connection was reset or replaced since <see cref="BeginLoginHandshake"/> / <see cref="BeginGameHandshake"/>.
        /// </summary>
        public void EndHandshake(int token)
        {
            lock (_sendLock)
            {
                if (token != _sendEpoch)
                {
                    return;
                }

                _sendOpen = true;

                if (_handshakeThread == Environment.CurrentManagedThreadId)
                {
                    _handshakeThread = 0;
                }

                // sends other threads made during the handshake follow it, in order
                foreach (var (data, skipEncryption) in _deferred)
                {
                    EnqueueLocked(data, skipEncryption);
                }

                _deferred.Clear();
            }
        }

        private const int MaxDeferredSends = 256;
        private readonly List<(byte[] Data, bool SkipEncryption)> _deferred = new List<(byte[], bool)>();

        /// <summary>Sends dropped because their connection had ended or been replaced (a stale epoch).</summary>
        public int DroppedStaleSends => Volatile.Read(ref _droppedStale);

        /// <summary>Sends made during a handshake that were held back and queued after it.</summary>
        public int DeferredSends => Volatile.Read(ref _deferredTotal);

        /// <summary>Sends dropped because too many were held back during one handshake.</summary>
        public int DroppedDeferredOverflow => Volatile.Read(ref _droppedOverflow);

        private int _droppedStale, _deferredTotal, _droppedOverflow;
        private long _lastDropLog;

        private void NoteDrop(string kind, byte id)
        {
            // outside _sendLock; at most one line per second
            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _lastDropLog);

            if (now - last < Stopwatch.Frequency || Interlocked.CompareExchange(ref _lastDropLog, now, last) != last)
            {
                return;
            }

            Log.Warn($"Send dropped ({kind}), packet 0x{id:X2}; stale={DroppedStaleSends} deferred={DeferredSends} overflow={DroppedDeferredOverflow}");
        }

        // Under _sendLock: log (when the packet log is on), encrypt, queue and count one admitted packet.
#if CUO_DEVTOOLS
        private long _diagQueued, _diagWritten;
        internal long DiagReadTimestamp;
#endif
        private void EnqueueLocked(Span<byte> message, bool skipEncryption)
        {
#if CUO_DEVTOOLS
            if (Diagnostics.On)
            {
                _diagQueued += message.Length;
                if (message.Length == 2 && message[0] == 0x73)
                    Diagnostics.PingQueued(Statistics, message[1], _diagQueued);
            }
#endif
            PacketLogger.Default?.Log(message, true);
#if CUO_DEVTOOLS
            if (Harness.NetHarness.On)
            {
                if (Harness.NetCapture.On) Harness.NetCapture.Outbound(message);
                if (Harness.NetReplay.Active) Harness.NetReplay.Sent(message[0]);
            }
#endif

            if (!skipEncryption)
            {
                Encryption?.Encrypt(!_isCompressionEnabled, message, message, message.Length);
            }

            if (_sendRing != null)
            {
                if (!EnqueueRingLocked(message))
                {
                    return;
                }
            }
            else
            {
                _sendStream.Enqueue(message);
            }

            Statistics.TotalBytesSent += (uint)message.Length;
            Statistics.TotalPacketsSent++;
        }

        public void EnableCompression()
        {
            lock (_sendLock)
            {
                _isCompressionEnabled = true;
                _sendStream.Clear();
                _sendRing?.DiscardReadable();
#if CUO_DEVTOOLS
                if (Diagnostics.On) _diagWritten = _diagQueued;
#endif
            }

            _huffman.Reset();
        }


        /// <summary>Raw bytes read per slice. Huffman output for one slice stays under the 64 KB decompression buffer.</summary>
        public const int SliceSize = 4096;

        /// <summary>Default most raw bytes drained in one frame (about 7.7 MB/s at 60 Hz).</summary>
        public const int DefaultFrameByteCap = 128 * 1024;

        /// <summary>Default most packets dispatched in one frame; checked between slices.</summary>
        public const int DefaultFramePacketCap = 1500;

        /// <summary>Default time budget for one frame's drain, in ms: the drain stops after the slice that passes it (0 = none).</summary>
        public const double DefaultFrameMs = 4;

        /// <summary>Also read the socket's available byte count in each drain (CUO_NET_TRACE; FrameStats does it anyway).</summary>
        public static bool TrackAvailable { get; set; }

        /// <summary>Bytes the socket had ready (the kernel receive queue) when the drain started (FrameStats runs only).</summary>
        public int LastDrainAvailable { get; private set; }

        /// <summary>Raw bytes read by the last <see cref="Drain"/>.</summary>
        public int LastDrainBytes { get; private set; }

        /// <summary>Slices read by the last <see cref="Drain"/>.</summary>
        public int LastDrainSlices { get; private set; }

        /// <summary>
        /// Reads what the socket has ready in <see cref="SliceSize"/> slices, in order, handing each decoded slice to
        /// <paramref name="sink"/> (which parses it and returns the packets it dispatched). Stops when the socket is empty, a
        /// close or error was raised, or the byte, packet or time cap (maxMs, 0 = none) is reached (the rest stays queued for the next frame, so a
        /// burst cannot stall one). The caps are checked between slices, so a frame can go over by up to one slice.
        /// </summary>
        public int Drain(Func<ArraySegment<byte>, int> sink, int maxBytes = DefaultFrameByteCap, int maxPackets = DefaultFramePacketCap, double maxMs = 0)
        {
#if CUO_DEVTOOLS
            long diagStart = Diagnostics.On ? Stopwatch.GetTimestamp() : 0;
            bool diagTimeCap = false;
#endif
#if CUO_DEVTOOLS
            if (Harness.NetReplay.Active)
            {
                // net-rework harness: a replay frame reads everything released for it, so the packets each frame dispatches
                // do not depend on how the reads were cut or how long they took
                maxBytes = int.MaxValue;
                maxPackets = int.MaxValue;
                maxMs = 0;
            }
#endif
            LastDrainAvailable = 0;
            LastDrainBytes = 0;
            LastDrainSlices = 0;

            var packets = 0;
            var start = maxMs > 0 ? Stopwatch.GetTimestamp() : 0;
            var sinkCalled = false;

            if (_socket != null && (TrackAvailable || FrameStats.On))
            {
                LastDrainAvailable = _socket.Available;
            }

            var emptied = false;
            Connection closeSeen = null; // the connection whose close was seen

            while (_socket != null && LastDrainBytes < maxBytes && packets < maxPackets)
            {
                // always one slice, then stop once the frame's time budget is spent
                if (LastDrainSlices > 0 && maxMs > 0 && Stopwatch.GetElapsedTime(start).TotalMilliseconds >= maxMs)
                {
#if CUO_DEVTOOLS
                    diagTimeCap = true;
#endif
                    break;
                }

                if (!ReadSlice(out var raw, out var data, out var decoded))
                {
                    // An empty read only ends the connection if it was made after the close had been seen: a receive task
                    // enqueues its last frame and then reports the close, so a read that was empty a moment earlier can
                    // be followed by that frame. Seeing the close sends us round once more; the caps still apply, and a
                    // capped frame carries on next time.
                    if (closeSeen != null && ReferenceEquals(closeSeen, _connection))
                    {
                        emptied = true;

                        break;
                    }

                    if (_connection is { Closed: true } seen)
                    {
                        closeSeen = seen;

                        continue;
                    }

                    break;
                }

                LastDrainBytes += raw;
                LastDrainSlices++;

                if (decoded > 0)
                {
                    packets += sink(data);
                    sinkCalled = true;
                }
            }

            if (emptied && _connection is { Closed: true } closing)
            {
                // everything the connection delivered before it closed has been read and handed to the parser: now end it
                if (!sinkCalled)
                {
                    packets += sink(ArraySegment<byte>.Empty);
                    sinkCalled = true;
                }

                Terminate(closing.Error, closing);
            }

            if (!sinkCalled)
            {
                // Pumped with an empty slice even when the socket had nothing, so the parser drops a stale stream as soon as
                // the connection changes. This is inside the frame's time budget too.
                packets += sink(ArraySegment<byte>.Empty);
            }

#if CUO_DEVTOOLS
            if (Diagnostics.On)
            {
                int stop = _socket == null || emptied ? 4 : LastDrainBytes >= maxBytes ? 1 : packets >= maxPackets ? 2 : diagTimeCap ? 3 : 0;
                Diagnostics.Drain(diagStart, LastDrainBytes, packets, _socket?.Available ?? 0, stop);
            }
#endif
            return packets;
        }

        /// <summary>One slice, for callers that do not drain (kept for compatibility). Empty when nothing is ready.</summary>
        public ArraySegment<byte> CollectAvailableData()
        {
            return ReadSlice(out _, out var data, out _) ? data : ArraySegment<byte>.Empty;
        }

        // False when nothing was read (empty socket, close, error); true with the decoded slice otherwise (possibly empty).
        // net_ring: data stays empty and the slice is in the receive ring (decoded = its length).
        private bool ReadSlice(out int rawBytes, out ArraySegment<byte> data, out int decoded)
        {
            rawBytes = 0;
            decoded = 0;
            data = ArraySegment<byte>.Empty;

            if (_socket == null || _connection is { Retired: true })
            {
                return false;
            }

            try
            {
                int limit = _compressedBuffer.Length;

                if (_recvRing != null)
                {
                    SyncRing();
                    int room = _recvRing.WritableBytes;
                    // a compressed byte decodes to at most 4 (NetRingTests): read only what is sure to fit
                    limit = Math.Min(limit, _isCompressionEnabled ? room / 4 : room);

                    if (limit <= 0)
                    {
                        if (!_ringFullLogged)
                        {
                            _ringFullLogged = true;
                            Log.Warn($"[net-ring] receive ring full ({_recvRing.ReadableBytes} bytes unread): reading stops for this frame");
                        }

                        return false;
                    }
                }

                var size = limit < _compressedBuffer.Length ? _socket.Read(_compressedBuffer, limit) : _socket.Read(_compressedBuffer);
#if CUO_DEVTOOLS
                if (Diagnostics.On && size > 0) DiagReadTimestamp = Stopwatch.GetTimestamp();
#endif

                if (size <= 0)
                {
                    return false;
                }

                rawBytes = size;
                Statistics.TotalBytesReceived += (uint)size;

                var segment = new ArraySegment<byte>(_compressedBuffer, 0, size);
                var span = _compressedBuffer.AsSpan(0, size);

                ProcessEncryption(span);
#if CUO_DEVTOOLS
                if (Harness.NetCapture.On) Harness.NetCapture.Inbound(span, _isCompressionEnabled);
#endif

                if (_recvRing != null)
                {
                    decoded = DecodeIntoRing(span);

                    return decoded >= 0;
                }

                data = DecompressBuffer(segment);
                decoded = data.Count;

                return true;
            }
            catch (SocketException ex)
            {
                Log.Error("socket error when receving:\n" + ex);

                Terminate(ex.SocketErrorCode);
            }
            catch (Exception ex)
            {
                if (ex.InnerException is SocketException socketEx)
                {
                    Log.Error("main exception:\n" + ex);
                    Log.Error("socket error when receving:\n" + socketEx);

                    Terminate(socketEx.SocketErrorCode);
                }
                else
                {
                    Log.Error("fatal error when receving:\n" + ex);

                    Terminate(SocketError.SocketError);

                    throw;
                }
            }

            return false;
        }

        public void Flush()
        {
            ProcessSend();
            ProcessSendExhausted();

            lock (_sendLock)
            {
                Statistics.Update(); // one consistent snapshot of the send counters
            }
        }

        public void Send(Span<byte> message, bool skipEncryption = false)
        {
            // The connection this send belongs to, taken first, before the connection check (see _sendLock), so a sender
            // paused anywhere up to the lock cannot adopt a replacement connection.
            var epoch = Volatile.Read(ref _sendEpoch);

            if (!IsConnected || message.IsEmpty)
            {
                return;
            }

            BeforeSendLock?.Invoke(); // test seam: where a sender can be paused between its epoch read and the lock

            var id = message[0];
            var dropped = false;
#if CUO_DEVTOOLS
            if (Loop.LoopIdleCheck.On) Loop.LoopIdleCheck.Send(id);
#endif

            // Encrypt and enqueue as one step (see _sendLock): with two senders (the main thread and another thread) the cipher
            // would otherwise advance in one order and the bytes be queued in another, and the server could not decrypt.
            lock (_sendLock)
            {
                if (epoch != _sendEpoch)
                {
                    _droppedStale++; // its connection ended or was replaced
                    dropped = true;
                }
                else if (_sendOpen || (_handshakeThread == Environment.CurrentManagedThreadId))
                {
                    EnqueueLocked(message, skipEncryption);
                }
                else if (_deferred.Count < MaxDeferredSends)
                {
                    // Ahead of the handshake's own packets (another thread, while the handshake thread sends the login
                    // packet): held back and queued in order when the handshake ends.
                    _deferred.Add((message.ToArray(), skipEncryption));
                    _deferredTotal++;
                }
                else
                {
                    _droppedOverflow++;
                    dropped = true;
                }
            }

            // event loop: sends after the end-of-Update flush (including Draw hooks on the main thread) must end the next
            // wait too. The auto-reset wake is sticky. Read once: OnExiting may clear it concurrently.
            var wake = Volatile.Read(ref SendQueuedWake);

            if (!dropped && wake != null)
            {
                wake();
            }

            if (dropped)
            {
                NoteDrop(epoch != Volatile.Read(ref _sendEpoch) ? "stale connection" : "handshake queue full", id);
            }
        }

        /// <summary>net_ring: the receive ring (null: the CircularBuffer path).</summary>
        internal NetRing ReceiveRing => _recvRing;

        /// <summary>net_ring: the bytes the last read added to the receive ring (taken once, for the checker shadow).</summary>
        internal Span<byte> TakeRingSlice()
        {
            int n = _lastRingSlice;
            _lastRingSlice = 0;

            if (n == 0 || _recvRing == null)
            {
                return Span<byte>.Empty;
            }

            Span<byte> all = _recvRing.GetReadSpan();

            return all.Slice(Math.Max(0, all.Length - n));
        }

        /// <summary>net_ring: a new connection (or the end of one) drops what the ring still holds of the old stream.</summary>
        internal void SyncRing()
        {
            int generation = Volatile.Read(ref _generation);

            if (_ringGeneration != generation)
            {
                _ringGeneration = generation;
                _lastRingSlice = 0;
                _recvRing.DiscardReadable();
            }
        }

        private void EnsureRings()
        {
            if (_ringsTried || !NetRings.On)
            {
                return;
            }

            _ringsTried = true;
            NetRing recv = NetRings.Create(NetRings.ReceiveSizeOverride ?? NetRings.ReceiveSize, "receive");
            NetRing send = recv == null ? null : NetRings.Create(NetRings.SendSizeOverride ?? NetRings.SendSize, "send");

            if (send == null)
            {
                recv?.Dispose();

                return;
            }

            _recvRing = recv;
            _ringGeneration = Volatile.Read(ref _generation);

            lock (_sendLock)
            {
                _sendRing = send;
            }
        }

        // the decoded slice goes straight into the ring: -1 when the Huffman stream is broken (the connection is ended)
        private int DecodeIntoRing(Span<byte> raw)
        {
            Span<byte> dest = _recvRing.GetWriteSpan();
            int size = raw.Length;

            if (!_isCompressionEnabled)
            {
                raw.CopyTo(dest);
            }
            else
            {
                size = Math.Min(dest.Length, BUFF_SIZE);

                if (!_huffman.Decompress(raw, dest, ref size))
                {
                    Terminate(SocketError.SocketError);

                    return -1;
                }
            }

            _recvRing.CommitWrite(size);
            _lastRingSlice = size;

            return size;
        }

        // net_ring, under _sendLock: a packet that does not fit flushes the ring in place first; still no room (the socket
        // took nothing within its 5 s budget, or failed) marks the connection exhausted, which the next Flush ends.
        private bool EnqueueRingLocked(Span<byte> message)
        {
            if (_sendExhausted)
            {
                return false;
            }

            if (message.Length > _sendRing.WritableBytes)
            {
                try
                {
                    FlushRingLocked();
                }
                catch (Exception e)
                {
                    Log.Error($"[net] send failed while the send ring was full: {e.Message}");
                }
            }

            if (message.Length > _sendRing.WritableBytes)
            {
                _sendExhausted = true;

                return false;
            }

            message.CopyTo(_sendRing.GetWriteSpan());
            _sendRing.CommitWrite(message.Length);
#if CUO_DEVTOOLS
            if (NetRings.CheckOn) _sendStream.Enqueue(message);
#endif

            return true;
        }

        private void FlushRingLocked()
        {
            while (_sendRing.ReadableBytes > 0)
            {
                Span<byte> pending = _sendRing.GetReadSpan();
#if CUO_DEVTOOLS
                long diagSendStart = Diagnostics.On ? Stopwatch.GetTimestamp() : 0;
#endif
                _socket.Send((ReadOnlySpan<byte>)pending);
#if CUO_DEVTOOLS
                if (Diagnostics.On)
                {
                    Diagnostics.PingWrite(Statistics, _diagWritten, _diagWritten + pending.Length, diagSendStart, Stopwatch.GetTimestamp());
                    _diagWritten += pending.Length;
                }

                if (NetRings.CheckOn)
                {
                    CheckSent(pending);
                }
#endif
                _sendRing.CommitRead(pending.Length);
            }
        }

        private void ProcessSendExhausted()
        {
            bool exhausted;

            lock (_sendLock)
            {
                exhausted = _sendExhausted;
            }

            if (exhausted)
            {
                Log.Error("[net] send buffer exhausted: the socket took no data within its 5 s budget; dropping the connection");
                DisconnectReason = "Send buffer exhausted";
                Terminate(SocketError.NoBufferSpaceAvailable);
            }
        }
#if CUO_DEVTOOLS

        // net_ring checker: the CircularBuffer queue as a shadow of the send ring; every flushed byte must match it
        private void CheckSent(ReadOnlySpan<byte> sent)
        {
            NetRings.CheckedSends++;

            while (!sent.IsEmpty)
            {
                int n = _sendStream.Dequeue(_sendingBuffer, 0, Math.Min(sent.Length, _sendingBuffer.Length));

                if (n <= 0 || !sent.Slice(0, n).SequenceEqual(_sendingBuffer.AsSpan(0, n)))
                {
                    NetRings.Mismatch($"send: {sent.Length} bytes flushed from the ring did not match the shadow queue ({_sendStream.Length} left)");
                    _sendStream.Clear();

                    return;
                }

                sent = sent.Slice(n);
            }
        }
#endif

        private void ProcessEncryption(Span<byte> buffer)
        {
            if (!_isCompressionEnabled)
                return;

            Encryption?.Decrypt(buffer, buffer, buffer.Length);
        }

        private void ProcessSend()
        {
            if (!IsConnected)
                return;

            try
            {
                lock (_sendLock)
                {
                    if (_sendRing != null)
                    {
                        FlushRingLocked();
                    }

                    while (_sendRing == null && _sendStream.Length > 0)
                    {
                        var read = _sendStream.Dequeue(_sendingBuffer, 0, _sendingBuffer.Length);

                        if (read <= 0)
                        {
                            break;
                        }

#if CUO_DEVTOOLS
                        long diagSendStart = Diagnostics.On ? Stopwatch.GetTimestamp() : 0;
#endif
                        _socket.Send(_sendingBuffer, 0, read);
#if CUO_DEVTOOLS
                        if (Diagnostics.On)
                        {
                            Diagnostics.PingWrite(Statistics, _diagWritten, _diagWritten + read, diagSendStart, Stopwatch.GetTimestamp());
                            _diagWritten += read;
                        }
#endif
                    }
                }
            }
            catch (SocketException ex)
            {
                Log.Error("socket error when sending:\n" + ex);

                Terminate(ex.SocketErrorCode);
            }
            catch (Exception ex)
            {
                if (ex.InnerException is SocketException socketEx)
                {
                    Log.Error("main exception:\n" + ex);
                    Log.Error("socket error when sending:\n" + socketEx);

                    Terminate(socketEx.SocketErrorCode);
                }
                else
                {
                    Log.Error("fatal error when sending:\n" + ex);

                    Terminate(SocketError.SocketError);

                    throw;
                }
            }
        }

        private ArraySegment<byte> DecompressBuffer(ArraySegment<byte> buffer)
        {
            if (!_isCompressionEnabled)
                return buffer;

            var size = 65536;

            if (!_huffman.Decompress(buffer, _uncompressedBuffer, ref size))
            {
                Terminate(SocketError.SocketError);

                return ArraySegment<byte>.Empty;
            }

            return new ArraySegment<byte>(_uncompressedBuffer, 0, size);
        }
    }
}
