using System;
using System.Net;
using System.Net.Sockets;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network.Socket;

abstract class SocketWrapper : IDisposable
{
    public abstract bool IsConnected { get; }

    public abstract EndPoint LocalEndPoint { get; }


    public event EventHandler OnConnected, OnDisconnected;
    public event EventHandler<SocketError> OnError;


    public abstract void Connect(Uri uri);
    public abstract void Send(byte[] buffer, int offset, int count);

    /// <summary>
    /// Reads whatever is ready, up to buffer.Length, without blocking. Returns 0 when nothing is ready. A peer close or a
    /// socket error raises OnDisconnected / OnError (and closes) instead of returning data.
    /// </summary>
    public abstract int Read(byte[] buffer);

    private byte[] _limitScratch, _sendScratch;

    /// <summary>
    /// Reads at most <paramref name="count"/> bytes into <paramref name="buffer"/> (net_ring: what still fits in the receive
    /// ring). The base version goes through an exact-size scratch array; TcpSocketWrapper reads in place.
    /// </summary>
    public virtual int Read(byte[] buffer, int count)
    {
        if (count >= buffer.Length)
        {
            return Read(buffer);
        }

        if (_limitScratch == null || _limitScratch.Length != count)
        {
            _limitScratch = new byte[count];
        }

        int n = Read(_limitScratch);
        Array.Copy(_limitScratch, buffer, n);

        return n;
    }

    /// <summary>Sends every byte of <paramref name="data"/> (net_ring: straight from the send ring). The base version copies through a 4 KB array.</summary>
    public virtual void Send(ReadOnlySpan<byte> data)
    {
        _sendScratch ??= new byte[4096];

        while (!data.IsEmpty)
        {
            int n = Math.Min(data.Length, _sendScratch.Length);
            data.Slice(0, n).CopyTo(_sendScratch);
            Send(_sendScratch, 0, n);
            data = data.Slice(n);
        }
    }

    /// <summary>Bytes ready to read (diagnostics only).</summary>
    public virtual int Available => 0;

    /// <summary>
    /// event loop (Windows): signal <paramref name="evt"/> when the connected socket becomes readable or closes
    /// (WSAEventSelect). False when not connected or not supported; the caller retries on a later frame.
    /// </summary>
    public virtual bool TryEventSelect(IntPtr evt) => false;

    /// <summary>event loop (Linux epoll, macOS kqueue): the connected socket's file descriptor. False on Windows or when not connected.</summary>
    public virtual bool TryGetPollFd(out int fd)
    {
        fd = -1;

        return false;
    }

    public abstract void Disconnect();

    public abstract void Dispose();

    protected virtual void InvokeOnConnected()
    {
        OnConnected?.Invoke(this, EventArgs.Empty);
    }

    protected virtual void InvokeOnDisconnected()
    {
        OnDisconnected?.Invoke(this, EventArgs.Empty);
    }

    protected virtual void InvokeOnError(SocketError e)
    {
        OnError?.Invoke(this, e);
    }
}