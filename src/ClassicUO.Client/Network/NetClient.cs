﻿#region license

// Copyright (c) 2024, andreakarasho
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 1. Redistributions of source code must retain the above copyright
//    notice, this list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright
//    notice, this list of conditions and the following disclaimer in the
//    documentation and/or other materials provided with the distribution.
// 3. All advertising materials mentioning features or use of this software
//    must display the following acknowledgement:
//    This product includes software developed by andreakarasho - https://github.com/andreakarasho
// 4. Neither the name of the copyright holder nor the
//    names of its contributors may be used to endorse or promote products
//    derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS ''AS IS'' AND ANY
// EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#endregion

using ClassicUO.Network.Encryption;
using ClassicUO.Utility.Logging;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ClassicUO.Utility;

namespace ClassicUO.Network
{
    sealed class SocketWrapper : IDisposable
    {
        private TcpClient _socket;

        private readonly CircularBuffer readBuffer = new CircularBuffer();
        private CancellationTokenSource readCancellationTokenSource;
        private Task readTask;

        public bool IsConnected => _socket?.Client?.Connected ?? false;

        public EndPoint LocalEndPoint => _socket?.Client?.LocalEndPoint;


        public event EventHandler OnConnected, OnDisconnected;
        public event EventHandler<SocketError> OnError;


        public void Connect(string ip, int port)
        {
            if (IsConnected) return;

            _socket = new TcpClient();
            _socket.NoDelay = true;

            try
            {
                _socket.Connect(ip, port);

                if (!IsConnected)
                {
                    OnError?.Invoke(this, SocketError.NotConnected);
                    return;
                }

                OnConnected?.Invoke(this, EventArgs.Empty);

                readCancellationTokenSource = new CancellationTokenSource();
                readTask = ReadTask(_socket.GetStream(), readCancellationTokenSource.Token);
            }
            catch (SocketException socketEx)
            {
                Log.Error($"error while connecting {socketEx}");
                OnError?.Invoke(this, socketEx.SocketErrorCode);
            }
            catch (Exception ex)
            {
                Log.Error($"error while connecting {ex}");
                OnError?.Invoke(this, SocketError.SocketError);
            }
        }

        /**
         * Fills #readBuffer asynchronously with data received on the
         * TCP connection.  This data will be consumed by method Read().
         */
        private async Task ReadTask(NetworkStream stream, CancellationToken cancellationToken)
        {
            byte[] tempBuffer = new byte[4096];

            while (true)
            {
                int nbytes = await stream.ReadAsync(tempBuffer, cancellationToken);
                if (nbytes <= 0)
                    break;

                lock (readBuffer)
                {
                    readBuffer.Enqueue(tempBuffer.AsSpan(0, nbytes));
                }
            }
        }

        public void Send(byte[] buffer, int offset, int count)
        {
            var stream = _socket.GetStream();
            stream.Write(buffer, offset, count);
            stream.Flush();
        }

        public int Read(byte[] buffer)
        {
            if (_socket == null) return 0;

            if (!IsConnected)
            {
                OnDisconnected?.Invoke(this, EventArgs.Empty);
                Disconnect();
                return 0;
            }

            lock (readBuffer)
            {
                int nbytes = readBuffer.Dequeue(buffer, 0, buffer.Length);
                if (nbytes > 0)
                    return nbytes;
            }

            if (readTask.IsCompleted)
            {
                /* the readTask finishes only if the connection was
                   closed (by the peer) */
                OnDisconnected?.Invoke(this, EventArgs.Empty);
                Disconnect();
                return 0;
            }

            /* no data in the buffer (yet) */
            return 0;
        }

        public void Disconnect()
        {
            readCancellationTokenSource?.Cancel();
            _socket?.Close();
            Dispose();
        }

        public void Dispose()
        {
            _socket?.Dispose();
            _socket = null;
            readCancellationTokenSource = null;
            readBuffer.Clear();
        }
    }

    internal sealed class NetClient
    {
        private const int BUFF_SIZE = 0x10000;

        private readonly byte[] _compressedBuffer = new byte[4096];
        private readonly byte[] _uncompressedBuffer = new byte[BUFF_SIZE];
        private readonly byte[] _sendingBuffer = new byte[4096];
        private readonly Huffman _huffman = new Huffman();
        private bool _isCompressionEnabled;
        private readonly SocketWrapper _socket;
        private uint? _localIP;
        private readonly CircularBuffer _sendStream;


        public NetClient()
        {
            Statistics = new NetStatistics(this);
            _sendStream = new CircularBuffer();

            _socket = new SocketWrapper();
            _socket.OnConnected += (o, e) =>
            {
                Statistics.Reset();
                Connected?.Invoke(this, EventArgs.Empty);
            };
            _socket.OnDisconnected += (o, e) => Disconnected?.Invoke(this, SocketError.Success);
            _socket.OnError += (o, e) => Disconnected?.Invoke(this, SocketError.SocketError);
        }


        public static NetClient Socket { get; private set; } = new NetClient();

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


        public void Connect(string ip, ushort port)
        {
            _sendStream.Clear();
            _huffman.Reset();
            Statistics.Reset();

            _socket.Connect(ip, port);
        }

        public void Disconnect()
        {
            _isCompressionEnabled = false;
            Statistics.Reset();
            _socket.Disconnect();
        }

        public void EnableCompression()
        {
            _isCompressionEnabled = true;
            _huffman.Reset();
            _sendStream.Clear();
        }

        public ArraySegment<byte> CollectAvailableData()
        {
            try
            {
                var size = _socket.Read(_compressedBuffer);

                if (size <= 0)
                {
                    return ArraySegment<byte>.Empty;
                }

                Statistics.TotalBytesReceived += (uint)size;

                var segment = new ArraySegment<byte>(_compressedBuffer, 0, size);
                var span = _compressedBuffer.AsSpan(0, size);

                ProcessEncryption(span);

                return DecompressBuffer(segment);
            }
            catch (SocketException ex)
            {
                Log.Error("socket error when receving:\n" + ex);

                Disconnect();
                Disconnected?.Invoke(this, ex.SocketErrorCode);
            }
            catch (Exception ex)
            {
                if (ex.InnerException is SocketException socketEx)
                {
                    Log.Error("main exception:\n" + ex);
                    Log.Error("socket error when receving:\n" + socketEx);

                    Disconnect();
                    Disconnected?.Invoke(this, socketEx.SocketErrorCode);
                }
                else
                {
                    Log.Error("fatal error when receving:\n" + ex);

                    Disconnect();
                    Disconnected?.Invoke(this, SocketError.SocketError);

                    throw;
                }
            }

            return ArraySegment<byte>.Empty;
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

            if (message.IsEmpty) return;

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
            if (!_isCompressionEnabled) return;

            Encryption?.Decrypt(buffer, buffer, buffer.Length);
        }

        private void ProcessSend()
        {
            if (!IsConnected) return;

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

                Disconnect();
                Disconnected?.Invoke(this, ex.SocketErrorCode);
            }
            catch (Exception ex)
            {
                if (ex.InnerException is SocketException socketEx)
                {
                    Log.Error("main exception:\n" + ex);
                    Log.Error("socket error when sending:\n" + socketEx);

                    Disconnect();
                    Disconnected?.Invoke(this, socketEx.SocketErrorCode);
                }
                else
                {
                    Log.Error("fatal error when sending:\n" + ex);

                    Disconnect();
                    Disconnected?.Invoke(this, SocketError.SocketError);

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
                Disconnect();
                Disconnected?.Invoke(this, SocketError.SocketError);

                return ArraySegment<byte>.Empty;
            }

            return new ArraySegment<byte>(_uncompressedBuffer, 0, size);
        }
    }
}
