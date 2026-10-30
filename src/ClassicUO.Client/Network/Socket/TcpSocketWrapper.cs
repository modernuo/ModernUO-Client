using System;
using System.Net;
using System.Net.Sockets;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network.Socket;

sealed class TcpSocketWrapper : SocketWrapper
{
    private TcpClient _socket;

    public override bool IsConnected => _socket?.Client?.Connected ?? false;

    public override EndPoint LocalEndPoint => _socket?.Client?.LocalEndPoint;


    public override void Connect(Uri uri)
    {
        if (IsConnected)
            return;

        _socket = new TcpClient();
        _socket.NoDelay = true;

        try
        {
            _socket.Connect(uri.Host, uri.Port);

            if (!IsConnected)
            {
                InvokeOnError(SocketError.NotConnected);

                return;
            }

            InvokeOnConnected();
        }
        catch (SocketException socketEx)
        {
            Log.Error($"error while connecting {socketEx}");
            InvokeOnError(socketEx.SocketErrorCode);
        }
        catch (Exception ex)
        {
            Log.Error($"error while connecting {ex}");
            InvokeOnError(SocketError.SocketError);
        }
    }

    public override void Send(byte[] buffer, int offset, int count)
    {
        var stream = _socket.GetStream();
        stream.Write(buffer, offset, count);
        stream.Flush();
    }

    public override int Available
    {
        get
        {
            try
            {
                return _socket?.Client?.Available ?? 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    public override int Read(byte[] buffer)
    {
        var client = _socket?.Client;

        if (client == null || !client.Connected)
            return 0;

        try
        {
            // Readable means data, or a FIN/RST waiting to be read. Checking the data length alone (the old
            // `Available`) never noticed a peer close while the connection was idle.
            if (!client.Poll(0, SelectMode.SelectRead))
                return 0;

            var read = client.Receive(buffer, 0, buffer.Length, SocketFlags.None, out var error);

            if (error == SocketError.WouldBlock || error == SocketError.Interrupted)
                return 0;

            if (error != SocketError.Success)
            {
                InvokeOnError(error);
                Disconnect();

                return 0;
            }

            if (read <= 0)
            {
                InvokeOnDisconnected();
                Disconnect();

                return 0;
            }

            return read;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    public override void Disconnect()
    {
        _socket?.Close();
        Dispose();
    }

    public override void Dispose()
    {
        _socket?.Dispose();
    }
}