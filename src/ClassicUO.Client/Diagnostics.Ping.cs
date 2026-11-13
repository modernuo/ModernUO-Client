// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
using System;
using System.Diagnostics;
using ClassicUO.Network;

namespace ClassicUO
{
    internal static partial class Diagnostics
    {
        private static bool IsGameThread => _state != null && !System.Threading.Volatile.Read(ref _state.Stop) && _state.ThreadId == Environment.CurrentManagedThreadId;
        internal readonly record struct PingDispatch(NetStatistics Owner, int Index, long Read, long Dequeued);
        [ThreadStatic] private static PingDispatch _dispatch;
        public static PingDispatch EnterDispatch(NetStatistics owner, int index, long read, long dequeued)
        {
            var previous = _dispatch;
            _dispatch = new PingDispatch(owner, index, read, dequeued);
            return previous;
        }
        public static void LeaveDispatch(PingDispatch previous) => _dispatch = previous;

        private struct Ping
        {
            public NetStatistics Owner;
            public int Connection, Index, DrainStop, Backlog, DrainBytes;
            public long Sequence, Created, Queued, SendStart, SendEnd, Read, Dequeued, Handler, QueueEnd;
            public long MaxInterval, MaxUpdate, MaxDraw, MaxPresent;
            public int Gc0, Gc1, Gc2;
            public double PauseMs;
            public uint RawMs, DisplayMs;
            public string Status;
            public bool Ambiguous;
        }
        public static void PingCreated(NetStatistics owner, int connection, byte index)
        {
            var s = _state;
            if (!IsGameThread) return;
            for (int i = 0; i < s.ActivePings.Length; i++)
            {
                ref var old = ref s.ActivePings[i];
                if (old.Owner == owner && (old.Connection != connection || old.Index == index)) Complete(ref old, "replaced_or_timeout");
            }
            for (int i = 0; i < s.ActivePings.Length; i++)
            {
                ref var p = ref s.ActivePings[i];
                if (p.Owner != null) continue;
                p = new Ping { Owner = owner, Connection = connection, Index = index, Sequence = ++s.Sequence,
                    DrainStop = -1, Created = Stopwatch.GetTimestamp(), Gc0 = GC.CollectionCount(0), Gc1 = GC.CollectionCount(1), Gc2 = GC.CollectionCount(2), PauseMs = GC.GetTotalPauseDuration().TotalMilliseconds };
                return;
            }
            s.Current.PingDropped++;
        }
        public static void PingQueued(NetStatistics owner, byte index, long queueEnd)
        {
            if (!IsGameThread)
            {
                if (_state != null) System.Threading.Interlocked.Increment(ref _state.ForeignPingSends);
                return;
            }
            foreach (ref var p in _state.ActivePings.AsSpan())
                if (p.Owner == owner && p.Index == index)
                {
                    if (p.Queued != 0) p.Ambiguous = true;
                    else { p.Queued = Stopwatch.GetTimestamp(); p.QueueEnd = queueEnd; }
                }
        }
        // Called under NetClient's send lock. Byte offsets identify the write containing the ping, even with encrypted,
        // coalesced or split sends. A send failure leaves socket_end unobserved.
        public static void PingWrite(NetStatistics owner, long first, long end, long start, long finish)
        {
            if (!IsGameThread) return;
            foreach (ref var p in _state.ActivePings.AsSpan())
                if (p.Owner == owner && p.Queued != 0 && p.QueueEnd > first && p.QueueEnd - 2 < end)
                {
                    if (p.SendStart == 0) p.SendStart = start;
                    if (p.QueueEnd <= end) p.SendEnd = finish;
                }
        }
        public static void PingHandled(NetStatistics owner, byte index, uint raw, uint display)
        {
            if (!IsGameThread) return;
            foreach (ref var p in _state.ActivePings.AsSpan())
                if (p.Owner == owner && p.Index == index)
                {
                    if (_dispatch.Owner != owner || _dispatch.Index != index)
                    {
                        var injected = p;
                        injected.Read = injected.Dequeued = 0;
                        injected.Handler = Stopwatch.GetTimestamp(); injected.RawMs = raw; injected.DisplayMs = display;
                        Complete(ref injected, "handler_without_network_provenance");
                        return;
                    }
                    p.Read = _dispatch.Read; p.Dequeued = _dispatch.Dequeued;
                    p.Handler = Stopwatch.GetTimestamp(); p.RawMs = raw; p.DisplayMs = display;
                    p.MaxInterval = Math.Max(p.MaxInterval, p.Handler - _state.LastUpdate);
                    p.MaxUpdate = Math.Max(p.MaxUpdate, p.Handler - _state.LastUpdate);
                    Complete(ref p, p.Ambiguous ? "reply_ambiguous_duplicate_send" : "reply");
                    return;
                }
        }
        public static void PingReset(NetStatistics owner)
        {
            if (!IsGameThread) return;
            foreach (ref var p in _state.ActivePings.AsSpan())
                if (p.Owner == owner) Complete(ref p, "connection_reset");
        }
        private static void Complete(ref Ping p, string status)
        {
            var w = _state.Current;
            p.Gc0 = GC.CollectionCount(0) - p.Gc0; p.Gc1 = GC.CollectionCount(1) - p.Gc1; p.Gc2 = GC.CollectionCount(2) - p.Gc2;
            p.PauseMs = GC.GetTotalPauseDuration().TotalMilliseconds - p.PauseMs;
            p.Status = status;
            if (w.PingCount < w.Pings.Length) w.Pings[w.PingCount++] = p;
            else w.PingDropped++;
            p = default;
        }
    }

    // Provenance for bytes retained by the packet parser: a partial packet's final byte can arrive in a later slice.
    internal sealed class DiagReceiveTimeline
    {
        private readonly long[] _ends = new long[8192], _reads = new long[8192];
        private int _head, _count;
        private long _enqueued, _dequeued, _lostThrough;
        public void Clear() { _head = _count = 0; _enqueued = _dequeued = _lostThrough = 0; }
        public void Enqueue(int bytes, long read)
        {
            if (bytes == 0) return;
            _enqueued += bytes;
            if (_count == _ends.Length) { _lostThrough = _ends[_head]; _head = (_head + 1) % _ends.Length; _count--; }
            int slot = (_head + _count++) % _ends.Length;
            _ends[slot] = _enqueued; _reads[slot] = read;
        }
        public long Dequeue(int bytes)
        {
            _dequeued += bytes;
            if (_dequeued <= _lostThrough) return 0;
            while (_count > 0 && _ends[_head] < _dequeued) { _head = (_head + 1) % _ends.Length; _count--; }
            if (_count == 0) return 0;
            long read = _reads[_head];
            if (_ends[_head] == _dequeued) { _head = (_head + 1) % _ends.Length; _count--; }
            return read;
        }
    }
}
#endif
