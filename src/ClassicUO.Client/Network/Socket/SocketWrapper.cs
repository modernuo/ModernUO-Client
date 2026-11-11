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

    /// <summary>Bytes ready to read (diagnostics only).</summary>
    public virtual int Available => 0;

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