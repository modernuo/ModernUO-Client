// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Network.Encryption;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
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
        private SocketWrapper _socket = null;
        private SocketWrapperType? _socketType;


        public NetClient()
        {
            Statistics = new NetStatistics(this);
            _sendStream = new CircularBuffer();
        }

        public static NetClient Socket { get; private set; } = new();

        public EncryptionType Load(ClientVersion clientVersion, EncryptionType encryption)
        {
            PacketsTable = new PacketsTable(clientVersion);

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

        private void SetupSocket(SocketWrapperType wrapperType)
        {
            AttachSocket(wrapperType switch
            {
                SocketWrapperType.TcpSocket => new TcpSocketWrapper(),
                SocketWrapperType.WebSocket => new WebSocketWrapper(),
                _ => throw new ArgumentOutOfRangeException(nameof(wrapperType), wrapperType, null)
            });
        }

        internal void AttachSocket(SocketWrapper socket)
        {
            _socket?.Dispose();
            _socket = socket;

            _socket.OnConnected += (o, e) =>
            {
                Statistics.Reset();
                Connected?.Invoke(this, EventArgs.Empty);
            };

            // A close or error may be reported from any thread (a WebSocket's receive task). The callbacks only record it
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

        /// <summary>Bumped whenever a connection starts or ends; the packet parser drops its partial data when it changes.</summary>
        public int ConnectionGeneration { get; private set; }

        /// <summary>
        /// Puts the protocol back to its start state: login-server mode (no compression, so no game-server encryption mode), a
        /// fresh Huffman state, nothing queued to send, and a new connection generation.
        /// </summary>
        private void ResetProtocolState()
        {
            _isCompressionEnabled = false;
            _huffman.Reset();
            _sendStream.Clear();
            ConnectionGeneration++;
        }

        /// <summary>
        /// The one place a connection ends from the receive or send side (peer close, reset, socket error, a bad stream):
        /// protocol state is reset first, so whatever the Disconnected handlers do next (a reconnect to the login server)
        /// starts clean, then the socket is closed and the handlers are told.
        /// </summary>
        private void Terminate(SocketError error)
        {
            _connection?.Retire(); // late callbacks of this connection, and its unread data, are void from here on
            ResetProtocolState();
            Statistics.Reset();
            _socket?.Disconnect();
            Disconnected?.Invoke(this, error);
        }

        /// <summary>The stream can't be parsed any more (a malformed packet header): drop the connection.</summary>
        internal void ProtocolError()
        {
            Terminate(SocketError.ProtocolNotSupported);
        }

        public void Connect(string ip, ushort port)
        {
            ResetProtocolState();
            Statistics.Reset();

            if (string.IsNullOrEmpty(ip))
                throw new ArgumentNullException(nameof(ip));

            var isWebsocketAddress = ip.ToLowerInvariant().Substring(0, 2) is "ws" or "wss";
            var addr = $"{(isWebsocketAddress ? "" : "tcp://")}{ip}:{port}";

            if (!Uri.TryCreate(addr, UriKind.RelativeOrAbsolute, out var uri))
                throw new UriFormatException($"NetClient::Connect() invalid Uri {addr}");

            Log.Trace($"Connecting to {uri}");

            // First connected socket sets the type for any future sockets.
            // This prevents the client from swapping from WS -> TCP on game server login
            SetupSocket(_socketType ??= isWebsocketAddress ? SocketWrapperType.WebSocket : SocketWrapperType.TcpSocket);
            _socket.Connect(uri);
        }

        public void Disconnect()
        {
            _connection?.Retire();
            ResetProtocolState();
            Statistics.Reset();
            _socket?.Disconnect();
        }

        public void EnableCompression()
        {
            _isCompressionEnabled = true;
            _huffman.Reset();
            _sendStream.Clear();
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

            while (_socket != null && LastDrainBytes < maxBytes && packets < maxPackets)
            {
                // always one slice, then stop once the frame's time budget is spent
                if (LastDrainSlices > 0 && maxMs > 0 && Stopwatch.GetElapsedTime(start).TotalMilliseconds >= maxMs)
                {
                    break;
                }

                if (!ReadSlice(out var raw, out var data))
                {
                    emptied = true;

                    break;
                }

                LastDrainBytes += raw;
                LastDrainSlices++;

                if (data.Count > 0)
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

                if (ReferenceEquals(closing, _connection))
                {
                    Terminate(closing.Error);
                }
            }

            if (!sinkCalled)
            {
                // The parser also owns the plugin queue (packets plugins inject), which has to be worked every frame whether
                // or not the socket had anything: pump it with an empty slice. This is inside the frame's time budget too.
                packets += sink(ArraySegment<byte>.Empty);
            }

            return packets;
        }

        /// <summary>One slice, for callers that do not drain (kept for compatibility). Empty when nothing is ready.</summary>
        public ArraySegment<byte> CollectAvailableData()
        {
            return ReadSlice(out _, out var data) ? data : ArraySegment<byte>.Empty;
        }

        // False when nothing was read (empty socket, close, error); true with the decoded slice otherwise (possibly empty).
        private bool ReadSlice(out int rawBytes, out ArraySegment<byte> data)
        {
            rawBytes = 0;
            data = ArraySegment<byte>.Empty;

            if (_socket == null || _connection is { Retired: true })
            {
                return false;
            }

            try
            {
                var size = _socket.Read(_compressedBuffer);

                if (size <= 0)
                {
                    return false;
                }

                rawBytes = size;
                Statistics.TotalBytesReceived += (uint)size;

                var segment = new ArraySegment<byte>(_compressedBuffer, 0, size);
                var span = _compressedBuffer.AsSpan(0, size);

                ProcessEncryption(span);

                data = DecompressBuffer(segment);

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
            Statistics.Update();
        }

        public void Send(Span<byte> message, bool ignorePlugin = false, bool skipEncryption = false)
        {
            if (!IsConnected || message.IsEmpty)
            {
                return;
            }

            if (!ignorePlugin && !Plugin.ProcessSendPacket(ref message))
            {
                return;
            }

            if (message.IsEmpty)
                return;

            PacketLogger.Default?.Log(message, true);

            if (!skipEncryption)
            {
                Encryption?.Encrypt(!_isCompressionEnabled, message, message, message.Length);
            }

            lock (_sendStream)
            {
                //_socket.Send(data, 0, length);
                _sendStream.Enqueue(message);
            }

            Statistics.TotalBytesSent += (uint)message.Length;
            Statistics.TotalPacketsSent++;
        }

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
                lock (_sendStream)
                {
                    while (_sendStream.Length > 0)
                    {
                        var read = _sendStream.Dequeue(_sendingBuffer, 0, _sendingBuffer.Length);

                        if (read <= 0)
                        {
                            break;
                        }

                        _socket.Send(_sendingBuffer, 0, read);
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