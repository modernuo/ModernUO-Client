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
/// single loop awaits each write in turn. The first failure is reported once through the failure callback (the socket's
/// error path, so the connection ends through <c>NetClient.Terminate</c>) and everything queued after it is discarded.
/// </summary>
internal sealed class OrderedAsyncWriter : IDisposable
{
    private readonly Channel<(byte[] Buffer, int Count)> _channel = Channel.CreateUnbounded<(byte[], int)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _write;
    private readonly Action<Exception> _onFailure;
    private readonly CancellationToken _token;
    private readonly Task _loop;
    private int _failed;

    public OrderedAsyncWriter(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write, Action<Exception> onFailure, CancellationToken token)
    {
        _write = write;
        _onFailure = onFailure;
        _token = token;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Completes when the writer has finished (after <see cref="Complete"/> and the queue is drained, or a failure).</summary>
    public Task Completion => _loop;

    public bool Failed => Volatile.Read(ref _failed) != 0;

    public bool TryWrite(byte[] buffer, int offset, int count)
    {
        if (Failed)
        {
            return false;
        }

        var copy = ArrayPool<byte>.Shared.Rent(count);
        Buffer.BlockCopy(buffer, offset, copy, 0, count);

        if (!_channel.Writer.TryWrite((copy, count)))
        {
            ArrayPool<byte>.Shared.Return(copy);

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
            }
        }
    }

    private void Fail(Exception e)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }

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
