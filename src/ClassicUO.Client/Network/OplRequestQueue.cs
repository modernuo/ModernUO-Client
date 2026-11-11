// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ClassicUO.Configuration;

namespace ClassicUO.Network
{
    /// <summary>Why an object property list is wanted: a revision the client does not hold (0xDC, 0xBF.10), a hovered object
    /// without properties (OPL.Contains), or an itemproperty entry of a gump. Hover and gump requests jump the queue.</summary>
    internal enum OplRequestKind : byte
    {
        Revision,
        Hover,
        Gump
    }

    internal delegate void OplBatchSink(ReadOnlySpan<uint> serials);

    /// <summary>
    /// <c>opl_request_budget</c>: the property-list requests (0xD6, or 0xBF.10 before 5.0.9) leave at a budget in serials per
    /// second instead of 15 per frame from one list. Arriving in a busy area used to queue ~21k requests at once (one per
    /// 0xDC), which the server answered with several MB on top of the world packets.
    /// <list type="bullet">
    /// <item>Order: hovered and gump requests first (up to one packet per frame, outside the budget), then by the object's
    /// distance from the player when it was queued (0..17 tiles, contained items by their root), then everything else.</item>
    /// <item>A serial is pending at most once. A sent serial is not sent again for a hover or gump request until
    /// <see cref="RetryMs"/> passed without an answer; a revision request for a revision other than the one last requested
    /// queues it again.</item>
    /// <item>World's removal of the object (deleted, out of range) drops its request; an answer ends the in-flight state.</item>
    /// </list>
    /// The rings and the entry table grow by doubling and are kept; draining allocates nothing. CUO_OPL_SPREAD_CHECK=1
    /// (developer builds, <see cref="OplSpreadCheck"/>) checks the sends against the requests.
    /// </summary>
    internal sealed class OplRequestQueue
    {
        public const int MaxPerPacket = 15;
        public const int NearBuckets = 18;
        public const int UrgentBucket = 0;
        public const int FarBucket = NearBuckets + 1;
        public const int BucketCount = NearBuckets + 2;
        public const uint RetryMs = 10000;

        private struct Entry
        {
            public uint Revision;
            public uint SentTick;
            public byte Bucket;
            public bool Pending;
            public bool HasRevision;
        }

        private readonly Dictionary<uint, Entry> _entries = new Dictionary<uint, Entry>(1024);
        private readonly uint[][] _rings = new uint[BucketCount][];
        private readonly int[] _head = new int[BucketCount];
        private readonly int[] _count = new int[BucketCount];
        private readonly uint[] _batch = new uint[MaxPerPacket];
        private double _tokens = MaxPerPacket;
        private uint _lastTick;
        private bool _started;
        private int _pending;

        /// <summary>The object's distance from the player in tiles, or -1 when unknown (bound once by PacketHandlers).</summary>
        public Func<uint, int> DistanceOf;

        /// <summary>Session counts: requests queued, merged into a pending or in-flight one, serials sent, packets handed to
        /// the sink, pending requests dropped by a removal, stale ring slots skipped.</summary>
        public long Queued, Merged, Sent, Packets, Dropped, Skipped;

        public OplRequestQueue()
        {
            for (int i = 0; i < BucketCount; i++)
            {
                _rings[i] = new uint[i == FarBucket ? 1024 : 64];
            }
        }

        public int PendingCount => _pending;

        public bool IsPending(uint serial) => _entries.TryGetValue(serial, out Entry e) && e.Pending;

        public bool IsInFlight(uint serial) => _entries.TryGetValue(serial, out Entry e) && !e.Pending;

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_OPL_REQUEST_BUDGET=<serials per second>, 0 = off)
        private static readonly int? s_env = int.TryParse(Environment.GetEnvironmentVariable("CUO_OPL_REQUEST_BUDGET"), out int v) && v >= 0 ? v : null;
#else
        private static readonly int? s_env = null;
#endif
        private static readonly int s_budget = Math.Max(0, s_env ?? Settings.GlobalSettings?.OplRequestBudget ?? 0);

        /// <summary>Tests: the budget (null: the setting, read once at startup).</summary>
        internal static int? BudgetOverride;

        /// <summary>Serials per second; 0 = off (the old list, 15 per frame).</summary>
        public static int Budget => BudgetOverride ?? s_budget;

        /// <summary>Queues <paramref name="serial"/>; false when it merged into a pending or in-flight request.</summary>
        public bool Request(uint serial, OplRequestKind kind, uint revision, uint now)
        {
            ref Entry e = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, serial, out bool exists);
            bool urgent = kind != OplRequestKind.Revision;

            if (exists)
            {
                if (e.Pending)
                {
                    if (kind == OplRequestKind.Revision)
                    {
                        e.Revision = revision;
                        e.HasRevision = true;
                    }

                    if (urgent && e.Bucket != UrgentBucket)
                    {
                        // the old slot stays in its ring and is skipped there (its bucket no longer matches)
                        e.Bucket = UrgentBucket;
                        Push(UrgentBucket, serial);
                    }

                    Merged++;

                    return false;
                }

                if (kind == OplRequestKind.Revision ? e.HasRevision && e.Revision == revision : now - e.SentTick < RetryMs)
                {
                    Merged++;

                    return false;
                }
            }

            if (kind == OplRequestKind.Revision)
            {
                e.Revision = revision;
                e.HasRevision = true;
            }
            else if (!exists)
            {
                e.HasRevision = false;
            }

            e.Pending = true;
            e.Bucket = (byte) (urgent ? UrgentBucket : BucketFor(serial));
            Push(e.Bucket, serial);
            _pending++;
            Queued++;

            return true;
        }

        /// <summary>The server answered for <paramref name="serial"/>: an in-flight request ends (a pending one stays).</summary>
        public void Answered(uint serial)
        {
            ref Entry e = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, serial);

            if (!Unsafe.IsNullRef(ref e) && !e.Pending)
            {
                _entries.Remove(serial);
            }
        }

        /// <summary>World removed the object: its request is dropped.</summary>
        public void Remove(uint serial)
        {
            if (_entries.Remove(serial, out Entry e) && e.Pending)
            {
                _pending--;
                Dropped++;
            }
        }

        public void Clear()
        {
            _entries.Clear();
            Array.Clear(_head);
            Array.Clear(_count);
            _pending = 0;
            _tokens = MaxPerPacket;
            _started = false;
        }

        /// <summary>
        /// Sends this frame's share to <paramref name="sink"/> in packets of up to <see cref="MaxPerPacket"/>: the urgent
        /// requests (one packet), then the rest while the budget (<paramref name="perSecond"/>, at most a tenth of a second
        /// of it saved up) allows a full packet, or all that is left when fewer remain. Returns the serials sent.
        /// </summary>
        public int Drain(uint now, int perSecond, OplBatchSink sink, out int packets)
        {
            if (!_started)
            {
                _started = true;
                _lastTick = now;
            }

            uint dt = now - _lastTick;
            _lastTick = now;
            double cap = Math.Max(MaxPerPacket, perSecond / 10.0);
            _tokens = Math.Min(cap, _tokens + perSecond * (dt / 1000.0));

            packets = 0;
            int n = 0;
            int sent = 0;

            while (n < MaxPerPacket && TryPop(UrgentBucket, now, out uint serial))
            {
                _batch[n++] = serial;
            }

            if (n != 0)
            {
                sent += Flush(sink, ref n, ref packets);
            }

            // whole packets only, unless what is left fits in the allowance
            int allowance = (int) _tokens;

            if (allowance < _pending)
            {
                allowance -= allowance % MaxPerPacket;
            }

            if (_pending != 0 && allowance != 0)
            {
                int taken = 0;

                for (int b = 1; b < BucketCount && taken < allowance; b++)
                {
                    while (taken < allowance && TryPop(b, now, out uint serial))
                    {
                        _batch[n++] = serial;
                        taken++;

                        if (n == MaxPerPacket)
                        {
                            sent += Flush(sink, ref n, ref packets);
                        }
                    }
                }

                if (n != 0)
                {
                    sent += Flush(sink, ref n, ref packets);
                }

                _tokens -= taken;
            }

            return sent;
        }

        private int Flush(OplBatchSink sink, ref int n, ref int packets)
        {
            int count = n;
            sink(new ReadOnlySpan<uint>(_batch, 0, count));
            n = 0;
            packets++;
            Packets++;
            Sent += count;

            return count;
        }

        private bool TryPop(int bucket, uint now, out uint serial)
        {
            uint[] ring = _rings[bucket];
            int mask = ring.Length - 1;

            while (_count[bucket] != 0)
            {
                serial = ring[_head[bucket]];
                _head[bucket] = (_head[bucket] + 1) & mask;
                _count[bucket]--;

                ref Entry e = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, serial);

                if (!Unsafe.IsNullRef(ref e) && e.Pending && e.Bucket == bucket)
                {
                    e.Pending = false;
                    e.SentTick = now;
                    _pending--;

                    return true;
                }

                Skipped++;
            }

            serial = 0;

            return false;
        }

        private void Push(int bucket, uint serial)
        {
            uint[] ring = _rings[bucket];

            if (_count[bucket] == ring.Length)
            {
                uint[] bigger = new uint[ring.Length * 2];

                for (int i = 0; i < _count[bucket]; i++)
                {
                    bigger[i] = ring[(_head[bucket] + i) & (ring.Length - 1)];
                }

                _rings[bucket] = ring = bigger;
                _head[bucket] = 0;
            }

            ring[(_head[bucket] + _count[bucket]) & (ring.Length - 1)] = serial;
            _count[bucket]++;
        }

        private int BucketFor(uint serial)
        {
            int d = DistanceOf?.Invoke(serial) ?? -1;

            return d < 0 || d >= NearBuckets ? FarBucket : 1 + d;
        }
    }
}
