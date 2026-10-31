using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// A byte budget for worker decodes in flight (queued to a worker, decoding, or finished and waiting for its upload):
    /// each holds pooled buffers until the main thread uploads them. <see cref="Submit"/> runs on the main thread and never
    /// blocks: a request over the budget waits in a FIFO and is started by the <see cref="Release"/> that makes room
    /// (nothing is dropped). A single item larger than the cap is admitted when nothing else is in flight, so it cannot wait
    /// forever; the FIFO makes sure nothing behind the head overtakes it, so the in-flight bytes fall to zero and the head
    /// runs (no starvation).
    /// <para>Each submission carries an exactly-once release token (<see cref="Item"/>'s budget state): the first
    /// <see cref="Release"/> of an admitted item gives its bytes back, later ones do nothing, so the worker (a cancelled
    /// decode handing back its buffers) and the main thread (upload, drop, cancel) may both release it. An owner's waiting
    /// (never started) items are taken out with <see cref="CancelWaiting"/>.</para>
    /// <para>The bytes are the pooled capacity the item's buffers will hold (<see cref="PooledBytes"/>, what
    /// <c>ArrayPool&lt;uint&gt;.Shared.Rent</c> returns), not the logical pixel payload.</para>
    /// CUO_DECODE_CAP_MB sets the shared cap (0 = unlimited: the budget only counts).
    /// </summary>
    public sealed class DecodeBudget
    {
        // Safety net, not a throttle: the measured all-time peak in flight is far below it (see poc-log, "Decode in-flight byte cap").
        public const long DefaultCapBytes = 16L << 20;

        private static long CapFromEnv()
        {
            string v = Environment.GetEnvironmentVariable("CUO_DECODE_CAP_MB");

            return v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double mb)
                ? (long)(mb * (1 << 20))
                : DefaultCapBytes;
        }

        /// <summary>The budget art, texmap and animation decodes share (gumps are not counted).</summary>
        public static readonly DecodeBudget Shared = new DecodeBudget(CapFromEnv());

        /// <summary>
        /// The bytes <c>ArrayPool&lt;uint&gt;.Shared.Rent(elements)</c> hands out: the shared pool rounds a request up to a
        /// power of two (at least 16 elements) for every size it pools (up to 2^30 elements); a larger request is allocated
        /// exactly, so the power of two is still an upper bound there.
        /// </summary>
        public static long PooledBytes(long elements)
        {
            if (elements <= 0)
            {
                return 0;
            }

            return elements > 1L << 30 ? elements * 4 : (long)Math.Max(16UL, BitOperations.RoundUpToPowerOf2((ulong)elements)) * 4;
        }

        private const byte StateNone = 0, StateWaiting = 1, StateAdmitted = 2;

        /// <summary>A budgeted work item. Its budget state is the release token; one item is submitted once per lifetime
        /// (reuse it only after its release).</summary>
        public abstract class Item : IThreadPoolWorkItem
        {
            internal long BudgetBytes;
            internal byte BudgetState;
            internal object BudgetOwner;

            /// <summary>Set by the owner's <see cref="DecodeQueue{T}"/> at submission.</summary>
            internal int Epoch;

            public abstract void Execute();
        }

        private readonly object _lock = new object();
        private readonly Queue<Item> _waiting = new Queue<Item>();
        private readonly Action<IThreadPoolWorkItem> _start;
        private long _inFlight, _framePeak, _logPeak, _peak;
        private int _waitingCount;

        /// <param name="capBytes">0 or less = unlimited.</param>
        /// <param name="start">Starts an admitted item (default: the thread pool); tests pass their own. It is called under the
        /// budget lock, possibly from a worker thread, and must not run the item inline.</param>
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

        /// <summary>Starts <paramref name="item"/> now if it fits and nothing waits ahead of it, otherwise queues it in order
        /// (main thread). <paramref name="owner"/> tags it for <see cref="CancelWaiting"/>.</summary>
        public void Submit(Item item, long bytes, object owner = null)
        {
            bytes = Math.Max(0, bytes);

            lock (_lock)
            {
                if (item.BudgetState != StateNone)
                {
                    throw new InvalidOperationException("decode budget: item submitted twice");
                }

                item.BudgetBytes = bytes;
                item.BudgetOwner = owner;

                if (_waiting.Count != 0 || !Fits(bytes))
                {
                    item.BudgetState = StateWaiting;
                    _waiting.Enqueue(item);
                    _waitingCount = _waiting.Count;

                    return;
                }

                Admit(item);
            }

            _start(item);
        }

        /// <summary>Starts an item outside the budget: not counted, never waits, nothing to release (gumps).</summary>
        public void StartUncounted(IThreadPoolWorkItem item) => _start(item);

        /// <summary>
        /// Gives back the bytes of an admitted item that has finished with its buffers and starts the waiters that now fit.
        /// Exactly once per submission: releasing an item that is not admitted (already released, never counted) does
        /// nothing. Any thread.
        /// </summary>
        /// <returns>True when this call released the bytes.</returns>
        public bool Release(Item item)
        {
            lock (_lock)
            {
                if (item.BudgetState != StateAdmitted)
                {
                    return false;
                }

                item.BudgetState = StateNone;
                _inFlight -= item.BudgetBytes;
                item.BudgetBytes = 0;
                item.BudgetOwner = null;
                AdmitWaiters();

                return true;
            }
        }

        /// <summary>
        /// Takes <paramref name="owner"/>'s waiting (never started) items out of the queue, in order, into
        /// <paramref name="removed"/>, and starts the waiters that now fit (main thread). The removed items hold nothing.
        /// </summary>
        public void CancelWaiting(object owner, List<Item> removed)
        {
            lock (_lock)
            {
                int n = _waiting.Count;

                for (int i = 0; i < n; i++)
                {
                    var w = _waiting.Dequeue();

                    if (w.BudgetOwner == owner)
                    {
                        w.BudgetState = StateNone;
                        w.BudgetBytes = 0;
                        w.BudgetOwner = null;
                        removed.Add(w);
                    }
                    else
                    {
                        _waiting.Enqueue(w);
                    }
                }

                AdmitWaiters();
            }
        }

        private void AdmitWaiters()
        {
            while (_waiting.Count > 0 && Fits(_waiting.Peek().BudgetBytes))
            {
                var w = _waiting.Dequeue();
                Admit(w);
                _start(w);
            }

            _waitingCount = _waiting.Count;
        }

        private void Admit(Item item)
        {
            item.BudgetState = StateAdmitted;
            _inFlight += item.BudgetBytes;

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
