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

    // Never blocks. The socket stays in blocking mode because sends go through NetworkStream, which rejects a non-blocking
    // socket, so a read is either bounded by what Available says is queued (cannot block), or, when nothing is queued but the
    // socket reports readable (a FIN or an error, or a spurious wake-up), a one-byte probe with the socket switched to
    // non-blocking for that call only.
    public override int Read(byte[] buffer)
    {
        var client = _socket?.Client;

        if (client == null || !client.Connected)
            return 0;

        try
        {
            var queued = client.Available;

            if (queued > 0)
            {
                var read = client.Receive(buffer, 0, Math.Min(buffer.Length, queued), SocketFlags.None, out var error);

                return Finish(read, error);
            }

            if (!client.Poll(0, SelectMode.SelectRead))
                return 0;

            client.Blocking = false;

            try
            {
                var read = client.Receive(buffer, 0, 1, SocketFlags.None, out var error);

                return Finish(read, error);
            }
            finally
            {
                client.Blocking = true;
            }
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    private int Finish(int read, SocketError error)
    {
        if (error == SocketError.WouldBlock || error == SocketError.Interrupted)
            return 0; // nothing there after all (a spurious readiness)

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