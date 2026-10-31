using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// A byte budget for worker decodes in flight (queued to a worker, decoding, or finished and waiting for its upload):
    /// each holds an untrimmed pooled buffer until the main thread uploads it. <see cref="Submit"/> and
    /// <see cref="Release"/> run on the main thread and never block: a request over the budget waits in a FIFO and is
    /// started by the <see cref="Release"/> that makes room (nothing is dropped). A single item larger than the cap is
    /// admitted when nothing else is in flight, so it cannot wait forever; the FIFO makes sure nothing behind the head
    /// overtakes it, so the in-flight bytes fall to zero and the head runs (no starvation).
    /// The owner releases each submitted item's bytes exactly once: after its upload, or when its result is dropped
    /// (failure, cancel, stale epoch).
    /// CUO_DECODE_CAP_MB sets the shared cap (0 = unlimited: the budget only counts).
    /// </summary>
    public sealed class DecodeBudget
    {
        public const long DefaultCapBytes = 24L << 20;

        private static long CapFromEnv()
        {
            string v = Environment.GetEnvironmentVariable("CUO_DECODE_CAP_MB");

            return v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double mb)
                ? (long)(mb * (1 << 20))
                : DefaultCapBytes;
        }

        /// <summary>The budget art, texmap, gump and animation decodes share.</summary>
        public static readonly DecodeBudget Shared = new DecodeBudget(CapFromEnv());

        private readonly struct Waiter
        {
            public readonly IThreadPoolWorkItem Item;
            public readonly long Bytes;

            public Waiter(IThreadPoolWorkItem item, long bytes)
            {
                Item = item;
                Bytes = bytes;
            }
        }

        private readonly object _lock = new object();
        private readonly Queue<Waiter> _waiting = new Queue<Waiter>();
        private readonly Action<IThreadPoolWorkItem> _start;
        private long _inFlight, _framePeak, _logPeak, _peak;
        private int _waitingCount;

        /// <param name="capBytes">0 or less = unlimited.</param>
        /// <param name="start">Starts an admitted item (default: the thread pool); tests pass their own.</param>
        public DecodeBudget(long capBytes, Action<IThreadPoolWorkItem> start = null)
        {
            CapBytes = capBytes;
            _start = start ?? (item => ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false));
        }

        public long CapBytes { get; }

        /// <summary>Bytes of admitted items not released yet.</summary>
        public long InFlightBytes => Volatile.Read(ref _inFlight);

        /// <summary>Items waiting for room.</summary>
        public int Waiting => Volatile.Read(ref _waitingCount);

        /// <summary>The most in-flight bytes seen since the start.</summary>
        public long PeakBytes => Volatile.Read(ref _peak);

        /// <summary>The most in-flight bytes since the last call (FrameStats), then restarts from the current level.</summary>
        public long TakeFramePeak()
        {
            lock (_lock)
            {
                long p = _framePeak;
                _framePeak = _inFlight;

                return p;
            }
        }

        /// <summary>The most in-flight bytes since the last call, then restarts from the current level (scripted runs log it per plan entry,
        /// holds included; FrameStats only sees the measured windows).</summary>
        public long TakeLogPeak()
        {
            lock (_lock)
            {
                long p = _logPeak;
                _logPeak = _inFlight;

                return p;
            }
        }

        private bool Fits(long bytes) => CapBytes <= 0 || _inFlight == 0 || _inFlight + bytes <= CapBytes;

        /// <summary>Starts <paramref name="item"/> now if it fits and nothing waits ahead of it, otherwise queues it in order.</summary>
        public void Submit(IThreadPoolWorkItem item, long bytes)
        {
            bytes = Math.Max(0, bytes);

            lock (_lock)
            {
                if (_waiting.Count != 0 || !Fits(bytes))
                {
                    _waiting.Enqueue(new Waiter(item, bytes));
                    _waitingCount = _waiting.Count;

                    return;
                }

                Admit(bytes);
            }

            _start(item);
        }

        /// <summary>Gives back the bytes of an item that has finished with its buffer; starts the waiters that now fit.</summary>
        public void Release(long bytes)
        {
            bytes = Math.Max(0, bytes);

            lock (_lock)
            {
                _inFlight -= bytes;

                while (_waiting.Count > 0 && Fits(_waiting.Peek().Bytes))
                {
                    var w = _waiting.Dequeue();
                    Admit(w.Bytes);
                    _start(w.Item);
                }

                _waitingCount = _waiting.Count;
            }
        }

        private void Admit(long bytes)
        {
            _inFlight += bytes;

            if (_inFlight > _framePeak)
            {
                _framePeak = _inFlight;
            }

            if (_inFlight > _logPeak)
            {
                _logPeak = _inFlight;
            }

            if (_inFlight > _peak)
            {
                _peak = _inFlight;
            }
        }
    }
}
