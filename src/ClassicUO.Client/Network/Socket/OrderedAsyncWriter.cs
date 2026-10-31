// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ClassicUO.Network.Socket;

/// <summary>
/// One ordered asynchronous writer per connection, for transports that must not have overlapping writes (a
/// <c>ClientWebSocket</c> allows one SendAsync at a time). <see cref="TryWrite"/> copies the bytes and returns at once; a
/// single loop awaits each write in turn. The first failure (a write that throws, or more than the byte cap queued: the
/// bytes are an encrypted stream, so they are never silently dropped) closes the writer to new writes, is reported once
/// through the failure callback (the socket's error path, so the connection ends through <c>NetClient.Terminate</c>), the
/// rest of the queue is discarded and <see cref="Completion"/> finishes on its own.
/// </summary>
internal sealed class OrderedAsyncWriter : IDisposable
{
    public const int DefaultMaxQueuedBytes = 4 * 1024 * 1024;

    private readonly Channel<(byte[] Buffer, int Count)> _channel = Channel.CreateUnbounded<(byte[], int)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _write;
    private readonly Action<Exception> _onFailure;
    private readonly CancellationToken _token;
    private readonly int _maxQueuedBytes;
    private readonly Task _loop;
    private int _failed;
    private long _queuedBytes;

    public OrderedAsyncWriter(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write, Action<Exception> onFailure, CancellationToken token, int maxQueuedBytes = DefaultMaxQueuedBytes)
    {
        _write = write;
        _onFailure = onFailure;
        _token = token;
        _maxQueuedBytes = maxQueuedBytes;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Completes when the writer has finished: after <see cref="Complete"/> and the queue is written, or on a failure or cancellation.</summary>
    public Task Completion => _loop;

    public bool Failed => Volatile.Read(ref _failed) != 0;

    /// <summary>Bytes accepted and not yet written (or discarded).</summary>
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

    public bool TryWrite(byte[] buffer, int offset, int count)
    {
        if (Failed)
        {
            return false;
        }

        if (Interlocked.Add(ref _queuedBytes, count) > _maxQueuedBytes)
        {
            Interlocked.Add(ref _queuedBytes, -count);
            Fail(new InvalidOperationException($"WebSocket send queue over {_maxQueuedBytes} bytes"));

            return false;
        }

        var copy = ArrayPool<byte>.Shared.Rent(count);
        Buffer.BlockCopy(buffer, offset, copy, 0, count);

        if (!_channel.Writer.TryWrite((copy, count)))
        {
            ArrayPool<byte>.Shared.Return(copy);
            Interlocked.Add(ref _queuedBytes, -count);

            return false;
        }

        return true;
    }

    /// <summary>No more writes are accepted; what is queued is still written.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    public void Dispose() => Complete();

    private async Task RunAsync()
    {
        var reader = _channel.Reader;

        try
        {
            while (await reader.WaitToReadAsync(_token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    try
                    {
                        if (!Failed)
                        {
                            await _write(item.Buffer.AsMemory(0, item.Count), _token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception e)
                    {
                        Fail(e);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(item.Buffer);
                        Interlocked.Add(ref _queuedBytes, -item.Count);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Fail(e);
        }
        finally
        {
            _channel.Writer.TryComplete();

            while (reader.TryRead(out var item))
            {
                ArrayPool<byte>.Shared.Return(item.Buffer);
                Interlocked.Add(ref _queuedBytes, -item.Count);
            }
        }
    }

    private void Fail(Exception e)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete(); // closes admission; the loop discards what is queued and finishes

        try
        {
            _onFailure(e);
        }
        catch
        {
            // the failure callback must not take the writer loop down
        }
    }
}
