using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Xna.Framework;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// First-sight decodes of single sprites (art, texmaps) on worker threads, with the atlas uploads left to the main
    /// thread under a time budget (the animation path's pattern, Animations.LoadDirection/DrainReady).
    /// <list type="bullet">
    /// <item><see cref="Request"/> (main thread) queues a decode of a sprite that has no texture yet, once.</item>
    /// <item>The worker decodes into a pooled buffer (and computes what it can from the pixels alone); it touches only the
    /// loader's immutable sprite table, the thread-safe pack pages and ArrayPool.</item>
    /// <item><see cref="Drain"/> (main thread) hands finished decodes to the owner's upload callback (atlas, picker,
    /// bounds) until the deadline, at least one per call.</item>
    /// <item>A decode that failed (or had nothing to upload) is <see cref="IsFailed"/> for good: requests are speculative,
    /// so nothing is retried here; the owner's synchronous lookup handles the sprite as before when something really
    /// needs it.</item>
    /// <item><see cref="Cancel"/> (scene teardown) returns the finished buffers, takes the waiting requests out of the decode
    /// budget, and makes running workers return their buffers and bytes when they finish (<see cref="DecodeQueue{T}"/>).</item>
    /// </list>
    /// The owner's synchronous lookup still decodes on the spot when it needs a sprite whose decode is in flight; the
    /// worker's result is then dropped by the upload callback.
    /// </summary>
    public sealed class FirstSightDecoder
    {
        public sealed class Job : DecodeBudget.Item
        {
            public FirstSightDecoder Owner;
            public uint Index;

            // filled by the worker; Pixels is rented from ArrayPool<uint>.Shared (returned by Drain), null if it failed
            public uint[] Pixels;
            public int Width, Height, TrimX, TrimY, TrimW, TrimH;
            public Rectangle Bounds;

            public override void Execute()
            {
                try
                {
                    if (!Owner._queue.IsCurrent(this))
                    {
                        // cancelled while it waited in the budget or the pool queue: nothing to decode
                    }
                    else if (!Owner._decode(this))
                    {
                        ReturnPixels();
                    }
                }
                catch (Exception ex)
                {
                    ReturnPixels();
                    ClassicUO.Utility.Logging.Log.Warn($"{Owner._name} {Index}: worker decode failed ({ex.Message})");
                }

                Owner._queue.Publish(this); // cancelled meanwhile: returns the buffer and the bytes here
            }

            public void ReturnPixels()
            {
                if (Pixels != null)
                {
                    System.Buffers.ArrayPool<uint>.Shared.Return(Pixels);
                    Pixels = null;
                }
            }
        }

        private const byte StateNone = 0, StateRequested = 1, StateFailed = 2;

        private readonly string _name;
        private readonly Func<Job, bool> _decode;
        private readonly Action<Job> _upload;
        private readonly DecodeQueue<Job> _queue;
        private readonly Stack<Job> _free = new Stack<Job>();
        private readonly byte[] _state;
        private readonly Func<uint, long> _bytesOf;
        private readonly Action<Job> _retire;

        /// <param name="decode">Worker: decodes job.Index into job.Pixels (rented) and the size fields; false = nothing to upload.</param>
        /// <param name="upload">Main thread: uploads a finished job (always with pixels).</param>
        /// <param name="bytesOf">Main thread: the pooled bytes a decode of a sprite holds (<see cref="DecodeBudget.PooledBytes"/> of
        /// its untrimmed pixel count); null = not counted: the requests bypass the budget entirely (gumps).</param>
        /// <param name="budget">The in-flight byte budget (default: <see cref="DecodeBudget.Shared"/>).</param>
        public FirstSightDecoder(string name, int count, Func<Job, bool> decode, Action<Job> upload, Func<uint, long> bytesOf = null,
            DecodeBudget budget = null)
        {
            _name = name;
            _bytesOf = bytesOf;
            _queue = new DecodeQueue<Job>(budget, job => job.ReturnPixels());
            _retire = Retire;
            _decode = decode;
            _upload = upload;
            _state = new byte[Math.Max(0, count)];
        }

        /// <summary>Decodes requested and not uploaded yet (main thread).</summary>
        public int Pending { get; private set; }

        /// <summary>Decodes uploaded so far (main thread): a waiting caller only needs to look again when this changed.</summary>
        public int Completed { get; private set; }

        public bool IsRequested(uint idx) => idx < (uint)_state.Length && _state[idx] == StateRequested;

        /// <summary>The worker could not decode sprite <paramref name="idx"/>: it is never requested again.</summary>
        public bool IsFailed(uint idx) => idx < (uint)_state.Length && _state[idx] == StateFailed;

        /// <summary>Queues a worker decode of sprite <paramref name="idx"/> unless one is queued or failed (main thread).</summary>
        public void Request(uint idx)
        {
            if (idx >= (uint)_state.Length || _state[idx] != StateNone)
            {
                return;
            }

            _state[idx] = StateRequested;
            Pending++;

            var job = _free.Count > 0 ? _free.Pop() : new Job { Owner = this };
            job.Index = idx;
            job.Pixels = null;
            _queue.Submit(job, _bytesOf?.Invoke(idx) ?? 0, counted: _bytesOf != null);
        }

        /// <summary>Uploads finished decodes until <paramref name="deadline"/> (Stopwatch ticks), at least one (main thread).</summary>
        public void Drain(long deadline) => Drain(deadline, atLeastOne: true);

        /// <summary>
        /// Requests the sprites of <paramref name="batch"/> and waits for them, uploading finished decodes, until none is
        /// pending, <paramref name="deadline"/> (Stopwatch ticks; no upload starts after it), or nothing finishes for
        /// <paramref name="stallMs"/>. It does not wait at all when the batch can't start now: other decodes wait for budget
        /// room, or the batch doesn't fit the room left (the bytes may be held by owners only this thread drains). The
        /// requests stay queued either way. Main thread. Returns how many of the batch are still pending.
        /// </summary>
        public int RequestAndWait(ReadOnlySpan<uint> batch, long deadline, int stallMs)
        {
            bool blocked = false;

            if (_bytesOf != null)
            {
                long bytes = 0;

                foreach (uint idx in batch)
                {
                    if (idx < (uint)_state.Length && _state[idx] == StateNone)
                    {
                        bytes += _bytesOf(idx);
                    }
                }

                var budget = _queue.Budget;
                long inFlight = budget.InFlightBytes;
                blocked = budget.Waiting > 0 || (bytes > 0 && budget.CapBytes > 0 && inFlight > 0 && inFlight + bytes > budget.CapBytes);
            }

            foreach (uint idx in batch)
            {
                Request(idx);
            }

            if (blocked)
            {
                return CountPending(batch);
            }

            while (true)
            {
                int pending = CountPending(batch);

                if (pending == 0)
                {
                    return 0;
                }

                long now = Stopwatch.GetTimestamp();

                if (now >= deadline)
                {
                    return pending;
                }

                if (_queue.HasReady)
                {
                    Drain(deadline, atLeastOne: false);

                    continue;
                }

                long leftMs = (deadline - now) * 1000 / Stopwatch.Frequency;
                int sliceMs = (int)Math.Max(1, Math.Min(stallMs, leftMs));

                // nothing finished within a slice: the workers are busy elsewhere, waiting longer only adds latency
                if (!_queue.WaitPublished(sliceMs))
                {
                    return CountPending(batch);
                }
            }
        }

        private int CountPending(ReadOnlySpan<uint> batch)
        {
            int n = 0;

            foreach (uint idx in batch)
            {
                if (IsRequested(idx))
                {
                    n++;
                }
            }

            return n;
        }

        private void Drain(long deadline, bool atLeastOne)
        {
            bool first = atLeastOne;

            while ((first || Stopwatch.GetTimestamp() < deadline) && _queue.TryDequeue(out var job))
            {
                first = false;

                try
                {
                    if (!_queue.IsCurrent(job))
                    {
                        _state[job.Index] = StateNone; // requested before a Cancel: may be requested again
                    }
                    else if (job.Pixels == null)
                    {
                        _state[job.Index] = StateFailed;
                    }
                    else
                    {
                        _state[job.Index] = StateNone;
                        _upload(job);
                    }
                }
                finally
                {
                    Pending--;
                    job.ReturnPixels();
                    _queue.Release(job); // exactly once: nothing if the cancelled worker already released it
                    Completed++;
                    _free.Push(job);
                }
            }
        }

        /// <summary>
        /// Scene teardown (main thread): drops every finished decode and returns its buffer, takes the requests still waiting
        /// for budget room out of it; workers still running return their buffers and bytes when they finish (their empty jobs
        /// are dropped by a later Drain). The sprites may be requested again afterwards.
        /// </summary>
        public void Cancel() => _queue.Cancel(_retire);

        private void Retire(Job job)
        {
            _state[job.Index] = StateNone;
            Pending--;
            _free.Push(job);
        }

        /// <summary>Tests only: runs on the worker inside the publish step, before its epoch check.</summary>
        public Action BeforePublishForTests
        {
            get => _queue.BeforePublishForTests;
            set => _queue.BeforePublishForTests = value;
        }

        public bool HasReady => _queue.HasReady;

        /// <summary>Finished decodes waiting for <see cref="Drain"/>.</summary>
        public int ReadyCount => _queue.ReadyCount;
    }
}
