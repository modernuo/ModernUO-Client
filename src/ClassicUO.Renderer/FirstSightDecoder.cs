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
    /// worker's result is then dropped by the upload callback. A decode that has finished can be taken instead
    /// (<see cref="TryTakeReady"/>).
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

            // the worker's result is complete (set before Publish; read by TryTakeReady on the main thread)
            internal volatile bool Finished;
            // main thread: TryTakeReady uploaded it; the drain or cancel that later dequeues it only recycles it
            internal bool Taken;

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

                Finished = true;
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
        // requested sprites and their jobs, for TryTakeReady (main thread)
        private readonly Dictionary<uint, Job> _inFlight = new Dictionary<uint, Job>();

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
            job.Finished = false;
            job.Taken = false;
            _inFlight[idx] = job;
            _queue.Submit(job, _bytesOf?.Invoke(idx) ?? 0, counted: _bytesOf != null);
        }

        /// <summary>
        /// The owner's synchronous lookup needs sprite <paramref name="idx"/> now: when its worker decode has finished (ready,
        /// not drained yet), uploads that result instead of a second decode, releases its bytes and buffer, and returns true.
        /// A decode still running, failed or cancelled: false, nothing changes (the lookup decodes as before; no wait). The job
        /// stays in the ready queue until a drain or cancel dequeues it, and is only recycled there. The upload time is charged
        /// to <paramref name="allowance"/> (a no-op while it is inactive); the take itself is never refused for it. Main thread.
        /// </summary>
        public bool TryTakeReady(uint idx, FrameAllowance allowance = null)
        {
            if (!IsRequested(idx) || !_inFlight.TryGetValue(idx, out var job) || !job.Finished || !_queue.IsCurrent(job)
                || job.Pixels == null)
            {
                return false;
            }

            long t0 = Stopwatch.GetTimestamp();
            _inFlight.Remove(idx);
            job.Taken = true;

            try
            {
                _state[idx] = StateNone;
                _upload(job);
            }
            finally
            {
                Pending--;
                job.ReturnPixels();
                _queue.Release(job);
                Completed++;
                TakenReady++;
                long spent = Stopwatch.GetTimestamp() - t0;
                DrainTicks += spent;
                // main-thread art time like a drain's: while active, the frame's later prefetches and drains see it spent
                allowance?.Spend(spent);
            }

            return true;
        }

        /// <summary>
        /// Sprite <paramref name="idx"/>'s worker decode has finished (decoded, failed or cancelled) and is not taken yet: a
        /// caller waiting for it can stop. A failed one stays <see cref="IsRequested"/> until a drain marks it failed. Main thread.
        /// </summary>
        public bool HasFinished(uint idx) => IsRequested(idx) && _inFlight.TryGetValue(idx, out var job) && job.Finished;

        /// <summary>Worker publishes so far (DecodeQueue.PublishedCount): read before checking a batch, then
        /// <see cref="WaitPublishedSince"/>.</summary>
        public int PublishedCount => _queue.PublishedCount;

        /// <summary>
        /// Blocks the main thread until a decode finishes after <paramref name="seen"/> or <paramref name="timeoutMs"/> passes;
        /// finished decodes already waiting for a drain don't end it. True when one finished.
        /// </summary>
        public bool WaitPublishedSince(int seen, int timeoutMs) => _queue.WaitPublishedSince(seen, timeoutMs);

        /// <summary>Finished decodes uploaded by <see cref="TryTakeReady"/> so far (main thread; never reset).</summary>
        public int TakenReady { get; private set; }

        private void Forget(Job job)
        {
            if (_inFlight.TryGetValue(job.Index, out var current) && current == job)
            {
                _inFlight.Remove(job.Index);
            }
        }

        /// <summary>Uploads finished decodes until <paramref name="deadline"/> (Stopwatch ticks), at least one (main thread).</summary>
        public void Drain(long deadline) => Drain(deadline, atLeastOne: true);

        /// <summary>
        /// Requests the sprites of <paramref name="batch"/> and waits for them, uploading finished decodes, until none is
        /// pending, <paramref name="deadline"/> (Stopwatch ticks; no upload starts after it), or nothing finishes for
        /// <paramref name="stallMs"/>. Finished decodes are uploaded first (within the deadline); after that it does not
        /// wait when decodes still wait for budget room (the bytes may be held by owners only this thread drains). The
        /// requests stay queued either way. Main thread. Returns how many of the batch are still pending.
        /// </summary>
        public int RequestAndWait(ReadOnlySpan<uint> batch, long deadline, int stallMs)
        {
            foreach (uint idx in batch)
            {
                Request(idx);
            }

            // the batch's finished decodes may be ready already; uploading them also returns their bytes, which can admit
            // what waits for room, so admission is judged after this
            if (_queue.HasReady && Stopwatch.GetTimestamp() < deadline)
            {
                Drain(deadline, atLeastOne: false);
            }

            if (_bytesOf != null && _queue.Budget.Waiting > 0)
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

        /// <summary>Main-thread time spent uploading finished decodes so far (Stopwatch ticks; never reset).</summary>
        public long DrainTicks { get; private set; }

        /// <summary>As <see cref="Drain(long)"/>; <paramref name="atLeastOne"/> false: no upload starts after the deadline.</summary>
        public void Drain(long deadline, bool atLeastOne)
        {
            long t0 = Stopwatch.GetTimestamp();
            bool first = atLeastOne;

            while ((first || Stopwatch.GetTimestamp() < deadline) && _queue.TryDequeue(out var job))
            {
                if (job.Taken)
                {
                    // uploaded and accounted by TryTakeReady: recycle only (and it doesn't count as this drain's upload)
                    job.Taken = false;
                    _free.Push(job);

                    continue;
                }

                first = false;
                Forget(job);

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

            DrainTicks += Stopwatch.GetTimestamp() - t0;
        }

        /// <summary>
        /// Scene teardown (main thread): drops every finished decode and returns its buffer, takes the requests still waiting
        /// for budget room out of it; workers still running return their buffers and bytes when they finish (their empty jobs
        /// are dropped by a later Drain). The sprites may be requested again afterwards.
        /// </summary>
        public void Cancel() => _queue.Cancel(_retire);

        private void Retire(Job job)
        {
            if (job.Taken)
            {
                job.Taken = false;
                _free.Push(job);

                return;
            }

            Forget(job);
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
