// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ClassicUO.Network
{
    /// <summary>
    /// The property-list requests while <c>opl_request_budget</c> is 0: first in, first out, 15 serials per 0xD6 each frame.
    /// It debounces like <see cref="OplRequestQueue"/>: a serial is pending at most once, and a sent serial is not sent
    /// again while its answer is outstanding, unless a 0xDC names a revision other than the one requested (a hover or gump
    /// request retries after <see cref="OplRequestQueue.RetryMs"/>). ModernUO repeats 0xDC with an unchanged revision on
    /// every Mobile.ProcessDelta to each viewer (a step, a hits change), so without this a moving mobile was re-requested on
    /// each step until its answer arrived. World's removal of the object and an answer that satisfies a pending request
    /// drop it; their slots stay in the list and are skipped when they come up (a slot is live only with the
    /// request's sequence number, so a request made again keeps its new place). CUO_OPL_DEBOUNCE_CHECK=1 counts the result.
    /// </summary>
    internal sealed class OplRequestList
    {
        private struct Entry
        {
            public uint Revision;
            public uint SentTick;
            public uint Seq;
            public bool Pending;
            public bool HasRevision;
        }

        private readonly Dictionary<uint, Entry> _entries = new Dictionary<uint, Entry>(256);
        private struct Slot
        {
            public uint Serial;
            public uint Seq;
        }

        private readonly List<Slot> _order = new List<Slot>(256);
        private int _head;
        private int _pending;
        private uint _seq;

        public int PendingCount => _pending;

        public bool IsPending(uint serial) => _entries.TryGetValue(serial, out Entry e) && e.Pending;

        public bool IsInFlight(uint serial) => _entries.TryGetValue(serial, out Entry e) && !e.Pending;

        /// <summary>Queues <paramref name="serial"/>; false when it merged into a pending or in-flight request.</summary>
        public bool Request(uint serial, OplRequestKind kind, uint revision, uint now)
        {
            ref Entry e = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, serial, out bool exists);

            if (exists)
            {
                if (e.Pending)
                {
                    if (kind == OplRequestKind.Revision)
                    {
                        e.Revision = revision;
                        e.HasRevision = true;
                    }

                    return false;
                }

                if (kind == OplRequestKind.Revision ? e.HasRevision && OplRequestQueue.SameRevision(e.Revision, revision) : now - e.SentTick < OplRequestQueue.RetryMs)
                {
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
            e.Seq = ++_seq;
            _pending++;
            _order.Add(new Slot { Serial = serial, Seq = e.Seq });

            return true;
        }

        /// <summary>Takes up to <c>batch.Length</c> pending serials in request order and marks them sent.</summary>
        public int Take(Span<uint> batch, uint now)
        {
            int n = 0;

            while (n < batch.Length && _head < _order.Count)
            {
                Slot slot = _order[_head++];
                uint serial = slot.Serial;
                ref Entry e = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, serial);

                // a slot left by a removed or answered request is stale even when the serial was requested again
                if (Unsafe.IsNullRef(ref e) || !e.Pending || e.Seq != slot.Seq)
                {
                    continue;
                }

                e.Pending = false;
                e.SentTick = now;
                _pending--;
                batch[n++] = serial;
            }

            if (_head == _order.Count)
            {
                _order.Clear();
                _head = 0;
            }
            else if (_head >= 4096 && _head * 2 >= _order.Count)
            {
                _order.RemoveRange(0, _head);
                _head = 0;
            }

            return n;
        }

        /// <summary>An answer: ends an in-flight request, and cancels a pending one it satisfies (a hover or gump request,
        /// or one for that revision).</summary>
        public void Answered(uint serial, uint revision)
        {
            ref Entry e = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, serial);

            if (Unsafe.IsNullRef(ref e))
            {
                return;
            }

            if (!e.Pending)
            {
                _entries.Remove(serial);
            }
            else if (!e.HasRevision || OplRequestQueue.SameRevision(e.Revision, revision))
            {
                _entries.Remove(serial);
                _pending--;
            }
        }

        /// <summary>World removed the object: its request is dropped.</summary>
        public void Remove(uint serial)
        {
            if (_entries.Remove(serial, out Entry e) && e.Pending)
            {
                _pending--;
            }
        }

        public void Clear()
        {
            _entries.Clear();
            _order.Clear();
            _head = 0;
            _pending = 0;
        }
    }
}
