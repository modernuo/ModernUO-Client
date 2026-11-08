using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// One owner's worker decodes through a <see cref="DecodeBudget"/>: submission, the worker's publish, the main thread's
    /// drain, and cancellation by epoch (FirstSightDecoder for art/texmaps/gumps, Animations for directions).
    /// <list type="bullet">
    /// <item><see cref="Submit"/> (main thread) stamps the current epoch and submits counted items to the budget, or starts
    /// uncounted ones at once (they never wait behind counted ones).</item>
    /// <item><see cref="Publish"/> is the worker's last touch of an item. A current item goes to the ready queue with its
    /// buffers; a cancelled one (stale epoch) first returns its buffers and releases its bytes on the worker, then goes to
    /// the ready queue empty, so the owner's next drain only does the bookkeeping.</item>
    /// <item><see cref="Release"/> (main thread, after the upload or drop) is exactly once per submission
    /// (<see cref="DecodeBudget.Release"/>), so a drain releasing an item the worker already released does nothing.</item>
    /// <item><see cref="Cancel"/> (main thread, teardown) bumps the epoch, takes the owner's waiting items out of the budget
    /// and retires them, and retires the finished items: no bytes stay charged for this owner without a later drain except
    /// those of workers still running, which give theirs back when they publish.</item>
    /// </list>
    /// </summary>
    public sealed class DecodeQueue<T> where T : DecodeBudget.Item
    {
        private readonly DecodeBudget _budget;
        private readonly Action<T> _returnBuffers;
        private readonly ConcurrentQueue<T> _ready = new ConcurrentQueue<T>();
        private readonly object _publishLock = new object();
        private readonly List<DecodeBudget.Item> _removed = new List<DecodeBudget.Item>();
        private int _epoch;
        // a main-thread caller blocked in WaitPublished; workers only touch the event while one is (no cost otherwise)
        private readonly ManualResetEventSlim _published = new ManualResetEventSlim(false);
        private int _publishWaiter;
        // every publish (current or cancelled), for a waiter that needs a new one while older items still sit in the queue
        private int _publishCount;

        /// <param name="returnBuffers">Returns the item's pooled buffers and clears them; must tolerate an item that has none.
        /// Runs on the worker (cancelled publish) or the main thread (cancel), never both for one item.</param>
        public DecodeQueue(DecodeBudget budget, Action<T> returnBuffers)
        {
            _budget = budget ?? DecodeBudget.Shared;
            _returnBuffers = returnBuffers;
        }

        public DecodeBudget Budget => _budget;

        /// <summary>Tests only: runs on the worker inside the publish step, before its epoch check.</summary>
        public Action BeforePublishForTests;

        /// <summary>False once the item's submission was cancelled (any thread).</summary>
        public bool IsCurrent(T item) => item.Epoch == Volatile.Read(ref _epoch);

        public bool HasReady => !_ready.IsEmpty;

        public int ReadyCount => _ready.Count;

        /// <summary>Main thread.</summary>
        public void Submit(T item, long bytes, bool counted)
        {
            item.Epoch = _epoch;

            if (counted)
            {
                _budget.Submit(item, bytes, this);
            }
            else
            {
                _budget.StartUncounted(item);
            }
        }

        /// <summary>Worker: the last touch of <paramref name="item"/> (the main thread may reuse it right after).</summary>
        public void Publish(T item)
        {
            // the epoch check and the enqueue are one step against Cancel: otherwise a Cancel between them (it bumps the
            // epoch and drains the queue) would leave this item's buffers and bytes held until the owner's next drain
            lock (_publishLock)
            {
                BeforePublishForTests?.Invoke();

                if (item.Epoch == _epoch)
                {
                    _ready.Enqueue(item);
                    SignalPublished();

                    return;
                }
            }

            // cancelled: hand the buffers and the bytes back here (nobody may drain this for a long time), before the
            // enqueue, which ends this thread's use of the item
            _returnBuffers(item);
            _budget.Release(item);
            _ready.Enqueue(item);
            SignalPublished();
        }

        /// <summary>Main thread.</summary>
        public bool TryDequeue(out T item) => _ready.TryDequeue(out item);

        private void SignalPublished()
        {
            // a full fence before the waiter check, paired with the waiter's flag exchange before its count read
            Interlocked.Increment(ref _publishCount);

            if (Volatile.Read(ref _publishWaiter) != 0)
            {
                _published.Set();
            }
        }

        /// <summary>
        /// Blocks the main thread until an item is ready or <paramref name="timeoutMs"/> passes (a signaled wait: no spinning
        /// against the workers). True when an item is ready.
        /// </summary>
        public bool WaitPublished(int timeoutMs)
        {
            _published.Reset();
            Interlocked.Exchange(ref _publishWaiter, 1);

            try
            {
                // an item published before the flag was visible is seen here; one published after it sets the event
                return !_ready.IsEmpty || (timeoutMs > 0 && WaitSignal(timeoutMs)) || !_ready.IsEmpty;
            }
            finally
            {
                Volatile.Write(ref _publishWaiter, 0);
            }
        }

        /// <summary>
        /// The main thread blocks above normal priority (CUO_DECODE_WAIT_BOOST=0: at its own). When the machine has more
        /// runnable normal-priority threads than cores (other clients, builds), a thread at normal priority whose wait ends
        /// queues behind them for its next quantum: RequestAndWait with a 2 ms stall slice measured a 380-500 ms median
        /// under load, and a kernel event or Thread.Sleep the same. Above normal, the timeout or the worker's Set preempts
        /// them at once (median 3.4-4 ms). The thread is blocked meanwhile, so the raised priority costs the workers nothing.
        /// No help when the whole process runs below normal (agentnice'd tests): the thread stays below the load.
        /// </summary>
        public static bool BoostWaits = Environment.GetEnvironmentVariable("CUO_DECODE_WAIT_BOOST") != "0";

        private bool WaitSignal(int timeoutMs)
        {
            var thread = Thread.CurrentThread;
            var priority = thread.Priority;

            if (!BoostWaits || priority >= ThreadPriority.AboveNormal)
            {
                return _published.Wait(timeoutMs);
            }

            thread.Priority = ThreadPriority.AboveNormal;

            try
            {
                return _published.Wait(timeoutMs);
            }
            finally
            {
                thread.Priority = priority;
            }
        }

        /// <summary>Publishes so far (any thread; wraps). Read before checking for finished items, then pass to
        /// <see cref="WaitPublishedSince"/>.</summary>
        public int PublishedCount => Volatile.Read(ref _publishCount);

        /// <summary>
        /// As <see cref="WaitPublished"/>, but for a publish after <paramref name="seen"/> (a <see cref="PublishedCount"/>):
        /// items already in the queue, such as finished decodes left for a later drain, do not end the wait. Main thread.
        /// </summary>
        public bool WaitPublishedSince(int seen, int timeoutMs)
        {
            _published.Reset();
            Interlocked.Exchange(ref _publishWaiter, 1);

            try
            {
                return PublishedCount != seen || (timeoutMs > 0 && WaitSignal(timeoutMs)) || PublishedCount != seen;
            }
            finally
            {
                Volatile.Write(ref _publishWaiter, 0);
            }
        }

        /// <summary>Gives the item's bytes back (exactly once per submission; uncounted items: nothing). Main thread.</summary>
        public void Release(T item) => _budget.Release(item);

        /// <summary>
        /// Teardown (main thread): later publishes of earlier submissions return their buffers and bytes themselves.
        /// <paramref name="retire"/> runs for every waiting item taken out of the budget (never started) and every finished
        /// item drained here (buffers already returned, bytes released), for the owner's bookkeeping.
        /// </summary>
        public void Cancel(Action<T> retire)
        {
            lock (_publishLock)
            {
                _epoch++;
            }

            // after the bump no current item can be published, so this drains every item published before it
            _removed.Clear();
            _budget.CancelWaiting(this, _removed);

            foreach (var item in _removed)
            {
                retire((T)item);
            }

            _removed.Clear();

            while (_ready.TryDequeue(out var item))
            {
                _returnBuffers(item);
                _budget.Release(item);
                retire(item);
            }
        }
    }
}
