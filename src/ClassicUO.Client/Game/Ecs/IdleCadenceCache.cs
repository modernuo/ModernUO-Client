// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    /// <summary>
    /// idle cadence cache (mount-fix item 5, owner 2026-11-23; only with idle_on_spawn off). A mobile that is removed or leaves
    /// range remembers its idle due time; when the same serial is created again with the same body within
    /// <see cref="ExpiryMs"/>, the old cadence is restored (rolled forward in 30-60 s idle steps if it passed meanwhile), so a
    /// mobile that walks in and out of range does not draw a new idle phase at every return.
    /// Lazy: written only by WorldWriter.RemoveMobile / SweepMobile, never while the mobile is in view. A hit removes its entry.
    /// An entry older than <see cref="ExpiryMs"/> is a miss when looked up (no sweeping); when the table is full the oldest
    /// entry is overwritten. O(1) per call: an open-addressing hash by serial (linear probing, backward-shift delete) over a
    /// preallocated entry array threaded on a list in write order (head = oldest, so eviction and the count of expired
    /// entries need no scan). Preallocated: <see cref="Capacity"/> entries x 24 B + 8,192 slots x 2 B = 112 KiB per World; no
    /// allocation after construction. Cleared on map change, logout and world clear. Main thread. Counters:
    /// <see cref="HighWater"/> (most entries younger than <see cref="ExpiryMs"/> seen at a write) and
    /// <see cref="UnexpiredOverwrites"/>.
    /// </summary>
    internal sealed class IdleCadenceCache
    {
        public const int Capacity = 4096;
        public const int ExpiryMs = 60000;
        private const int Slots = 2 * Capacity;
        private const int Mask = Slots - 1;
        private const int None = -1;

        private struct Entry
        {
            public uint Serial;
            public ushort Body;
            public bool Expired;
            public uint IdleDue;
            public uint LastSeen;
            public int Prev, Next; // write order; free entries are chained through Next
        }

        private readonly Entry[] _entries = new Entry[Capacity];
        private readonly ushort[] _slots = new ushort[Slots]; // entry index + 1, 0 = empty
        private int _count, _head = None, _tail = None, _free, _firstYoung = None, _expired;

        public IdleCadenceCache()
        {
            Reset();
        }

        public int Count => _count;
        public int HighWater { get; private set; }
        public int UnexpiredOverwrites { get; private set; }
        public int Hits { get; private set; }
        public int Misses { get; private set; }
        public int Writes { get; private set; }

        /// <summary>Tests: the hash slot a serial starts probing at.</summary>
        internal static int Home(uint serial) => (int)((serial * 0x9E3779B1u) >> 19) & Mask;

        private void Reset()
        {
            for (int i = 0; i < Capacity; i++)
            {
                _entries[i].Next = i + 1 < Capacity ? i + 1 : None;
            }

            _free = 0;
            _count = _expired = 0;
            _head = _tail = _firstYoung = None;
        }

        // the slot holding serial's entry, or -1
        private int Find(uint serial)
        {
            for (int s = Home(serial); ; s = (s + 1) & Mask)
            {
                int e = _slots[s] - 1;

                if (e < 0)
                {
                    return -1;
                }

                if (_entries[e].Serial == serial)
                {
                    return s;
                }
            }
        }

        private void HashInsert(int entry)
        {
            int s = Home(_entries[entry].Serial);

            while (_slots[s] != 0)
            {
                s = (s + 1) & Mask;
            }

            _slots[s] = (ushort)(entry + 1);
        }

        private void HashRemoveSlot(int slot)
        {
            int hole = slot;

            for (int s = (slot + 1) & Mask; _slots[s] != 0; s = (s + 1) & Mask)
            {
                int home = Home(_entries[_slots[s] - 1].Serial);

                // the entry at s moves into the hole when its home is cyclically at or before the hole
                if (((s - home) & Mask) >= ((s - hole) & Mask))
                {
                    _slots[hole] = _slots[s];
                    hole = s;
                }
            }

            _slots[hole] = 0;
        }

        private void Unlink(int e)
        {
            ref Entry en = ref _entries[e];

            if (en.Expired)
            {
                _expired--;
            }

            if (_firstYoung == e)
            {
                _firstYoung = en.Next;
            }

            if (en.Prev != None) _entries[en.Prev].Next = en.Next; else _head = en.Next;
            if (en.Next != None) _entries[en.Next].Prev = en.Prev; else _tail = en.Prev;
        }

        // the entry leaves the table (its hash slot already found)
        private void Release(int e, int slot)
        {
            HashRemoveSlot(slot);
            Unlink(e);
            _entries[e] = default;
            _entries[e].Next = _free;
            _free = e;
            _count--;
        }

        // entries are in write order, so the expired ones are a prefix: advance the pointer over what has expired by now
        private void AdvanceExpired(uint now)
        {
            if (_firstYoung == None && _expired == 0)
            {
                _firstYoung = _head;
            }

            while (_firstYoung != None && now - _entries[_firstYoung].LastSeen > ExpiryMs)
            {
                _entries[_firstYoung].Expired = true;
                _expired++;
                _firstYoung = _entries[_firstYoung].Next;
            }
        }

        /// <summary>The mobile is removed or leaves range: remember its idle due time at <paramref name="now"/>.</summary>
        public void Save(uint serial, ushort body, long idleDue, uint now)
        {
            if (serial == 0)
            {
                return;
            }

            Writes++;
            AdvanceExpired(now);
            int slot = Find(serial);

            if (slot >= 0)
            {
                Release(_slots[slot] - 1, slot);
            }
            else if (_count == Capacity)
            {
                int oldest = _head;

                if (!_entries[oldest].Expired)
                {
                    UnexpiredOverwrites++;
                }

                Release(oldest, Find(_entries[oldest].Serial));
            }

            int e = _free;
            _free = _entries[e].Next;
            _entries[e] = new Entry
            {
                Serial = serial,
                Body = body,
                IdleDue = (uint)Math.Max(0, idleDue),
                LastSeen = now,
                Prev = _tail,
                Next = None
            };

            if (_tail != None) _entries[_tail].Next = e; else _head = e;
            _tail = e;

            if (_firstYoung == None && _count == _expired)
            {
                _firstYoung = e;
            }

            _count++;
            HashInsert(e);

            if (_count - _expired > HighWater)
            {
                HighWater = _count - _expired;
            }
        }

        /// <summary>The serial is created with <paramref name="body"/>: the restored idle due time (in the future), or false
        /// (no entry, expired, or another body: a fresh draw). A hit removes the entry; an expired one is dropped too.</summary>
        public bool TryRestore(uint serial, ushort body, uint now, out long idleDue)
        {
            idleDue = 0;
            int slot = _count == 0 ? -1 : Find(serial);

            if (slot < 0)
            {
                Misses++;

                return false;
            }

            int e = _slots[slot] - 1;
            Entry en = _entries[e];

            if (now - en.LastSeen > ExpiryMs)
            {
                Release(e, slot);
                Misses++;

                return false;
            }

            if (en.Body != body)
            {
                Misses++;

                return false;
            }

            Release(e, slot);
            Hits++;
            long due = en.IdleDue;

            while (due <= now)
            {
                due += Mobile.IdleStepMs();
            }

            idleDue = due;

            return true;
        }

        /// <summary>Map change, logout, world clear.</summary>
        public void Clear()
        {
            if (Writes != 0 || Hits != 0 || Misses != 0)
            {
                Log.Info(TakeSummary());
            }

            if (_count != 0)
            {
                Array.Clear(_slots);
                Array.Clear(_entries);
                Reset();
            }
        }

        /// <summary>The counters since the last call, reset.</summary>
        public string TakeSummary()
        {
            string s = FormattableString.Invariant(
                $"[idle-cadence] entries {_count} young_hw {HighWater}/{Capacity} unexpired_overwrites {UnexpiredOverwrites} writes {Writes} hits {Hits} misses {Misses}");
            HighWater = _count - _expired;
            UnexpiredOverwrites = Writes = Hits = Misses = 0;

            return s;
        }
    }
}
