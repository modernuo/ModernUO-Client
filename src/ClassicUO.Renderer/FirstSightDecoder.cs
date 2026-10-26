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

                Owner._ready.Enqueue(this);
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

        private readonly string _name;
        private readonly Func<Job, bool> _decode;
        private readonly Action<Job> _upload;
        private readonly ConcurrentQueue<Job> _ready = new ConcurrentQueue<Job>();
        private readonly Stack<Job> _free = new Stack<Job>();
        private readonly byte[] _requested;

        /// <param name="decode">Worker: decodes job.Index into job.Pixels (rented) and the size fields; false = nothing to upload.</param>
        /// <param name="upload">Main thread: uploads a finished job (Pixels null = the worker failed: decode synchronously).</param>
        public FirstSightDecoder(string name, int count, Func<Job, bool> decode, Action<Job> upload)
        {
            _name = name;
            _decode = decode;
            _upload = upload;
            _requested = new byte[Math.Max(0, count)];
        }

        /// <summary>Decodes requested and not uploaded yet (main thread).</summary>
        public int Pending { get; private set; }

        /// <summary>Decodes uploaded so far (main thread): a waiting caller only needs to look again when this changed.</summary>
        public int Completed { get; private set; }

        public bool IsRequested(uint idx) => idx < (uint)_requested.Length && _requested[idx] != 0;

        /// <summary>Queues a worker decode of sprite <paramref name="idx"/> unless one is already queued (main thread).</summary>
        public void Request(uint idx)
        {
            if (idx >= (uint)_requested.Length || _requested[idx] != 0)
            {
                return;
            }

            _requested[idx] = 1;
            Pending++;

            var job = _free.Count > 0 ? _free.Pop() : new Job { Owner = this };
            job.Index = idx;
            job.Pixels = null;
            ThreadPool.UnsafeQueueUserWorkItem(job, preferLocal: false);
        }

        /// <summary>Uploads finished decodes until <paramref name="deadline"/> (Stopwatch ticks), at least one (main thread).</summary>
        public void Drain(long deadline)
        {
            bool first = true;

            while ((first || Stopwatch.GetTimestamp() < deadline) && _ready.TryDequeue(out var job))
            {
                first = false;
                _requested[job.Index] = 0;
                Pending--;

                try
                {
                    _upload(job);
                }
                finally
                {
                    job.ReturnPixels();
                    Completed++;
                    _free.Push(job);
                }
            }
        }

        public bool HasReady => !_ready.IsEmpty;

        /// <summary>Finished decodes waiting for <see cref="Drain"/>.</summary>
        public int ReadyCount => _ready.Count;
    }
}
