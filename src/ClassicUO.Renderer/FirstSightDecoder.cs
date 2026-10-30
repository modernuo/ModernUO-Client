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
    /// <item><see cref="Cancel"/> (scene teardown) returns the finished buffers and makes running workers return theirs.</item>
    /// </list>
    /// The owner's synchronous lookup still decodes on the spot when it needs a sprite whose decode is in flight; the
    /// worker's result is then dropped by the upload callback.
    /// </summary>
    public sealed class FirstSightDecoder
    {
        public sealed class Job : IThreadPoolWorkItem
        {
            public FirstSightDecoder Owner;
            public uint Index;
            internal int Epoch;

            // filled by the worker; Pixels is rented from ArrayPool<uint>.Shared (returned by Drain), null if it failed
            public uint[] Pixels;
            public int Width, Height, TrimX, TrimY, TrimW, TrimH;
            public Rectangle Bounds;

            public void Execute()
            {
                try
                {
                    if (!Owner._decode(this))
                    {
                        ReturnPixels();
                    }
                }
                catch (Exception ex)
                {
                    ReturnPixels();
                    ClassicUO.Utility.Logging.Log.Warn($"{Owner._name} {Index}: worker decode failed ({ex.Message})");
                }

                // the epoch check and the publish are one step against Cancel: otherwise a Cancel between them (it bumps
                // the epoch and drains an empty queue) leaves this buffer in the queue until the next session's Drain
                lock (Owner._publishLock)
                {
                    Owner.BeforePublishForTests?.Invoke();

                    if (Epoch != Owner._epoch)
                    {
                        ReturnPixels(); // cancelled while decoding: nobody may drain this for a long time
                    }

                    Owner._ready.Enqueue(this);
                }
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
        private readonly ConcurrentQueue<Job> _ready = new ConcurrentQueue<Job>();
        private readonly Stack<Job> _free = new Stack<Job>();
        private readonly byte[] _state;
        private int _epoch;

        /// <param name="decode">Worker: decodes job.Index into job.Pixels (rented) and the size fields; false = nothing to upload.</param>
        /// <param name="upload">Main thread: uploads a finished job (always with pixels).</param>
        public FirstSightDecoder(string name, int count, Func<Job, bool> decode, Action<Job> upload)
        {
            _name = name;
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
            job.Epoch = _epoch;
            ThreadPool.UnsafeQueueUserWorkItem(job, preferLocal: false);
        }

        /// <summary>Uploads finished decodes until <paramref name="deadline"/> (Stopwatch ticks), at least one (main thread).</summary>
        public void Drain(long deadline)
        {
            bool first = true;

            while ((first || Stopwatch.GetTimestamp() < deadline) && _ready.TryDequeue(out var job))
            {
                first = false;

                try
                {
                    if (job.Epoch != _epoch)
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
                    Completed++;
                    _free.Push(job);
                }
            }
        }

        /// <summary>
        /// Scene teardown (main thread): drops every finished decode and returns its buffer; workers still running return
        /// theirs when they finish. Their sprites may be requested again afterwards.
        /// </summary>
        public void Cancel()
        {
            // under the publish lock: a worker publishes either before (drained here) or after the bump (it returns its
            // buffer itself, and its empty job is dropped by a later Drain)
            lock (_publishLock)
            {
                _epoch++;

                while (_ready.TryDequeue(out var job))
                {
                    _state[job.Index] = StateNone;
                    Pending--;
                    job.ReturnPixels();
                    _free.Push(job);
                }
            }
        }

        private readonly object _publishLock = new object();

        /// <summary>Tests only: runs on the worker inside the publish step, before its epoch check.</summary>
        public Action BeforePublishForTests;

        public bool HasReady => !_ready.IsEmpty;

        /// <summary>Finished decodes waiting for <see cref="Drain"/>.</summary>
        public int ReadyCount => _ready.Count;
    }
}
