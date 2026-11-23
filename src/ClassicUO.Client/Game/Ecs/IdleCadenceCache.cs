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
    /// entry is overwritten. Preallocated <see cref="Capacity"/> x 16 B = 64 KiB per World; no allocation after construction.
    /// Cleared on map change, logout and world clear. Main thread. Counters: <see cref="HighWater"/> (most entries younger than
    /// <see cref="ExpiryMs"/> seen at a write) and <see cref="UnexpiredOverwrites"/>.
    /// </summary>
    internal sealed class IdleCadenceCache
    {
        public const int Capacity = 4096;
        public const int ExpiryMs = 60000;

        private struct Entry
        {
            public uint Serial;
            public ushort Body;
            public ushort Pad;
            public uint IdleDue;
            public uint LastSeen;
        }

        private readonly Entry[] _entries = new Entry[Capacity];
        private int _count;

        public int Count => _count;
        public int HighWater { get; private set; }
        public int UnexpiredOverwrites { get; private set; }
        public int Hits { get; private set; }
        public int Misses { get; private set; }
        public int Writes { get; private set; }

        /// <summary>The mobile is removed or leaves range: remember its idle due time at <paramref name="now"/>.</summary>
        public void Save(uint serial, ushort body, long idleDue, uint now)
        {
            if (serial == 0)
            {
                return;
            }

            Writes++;
            int same = -1, oldest = 0, young = 0;
            uint oldestAge = 0;

            for (int i = 0; i < _count; i++)
            {
                uint age = now - _entries[i].LastSeen;

                if (age <= ExpiryMs)
                {
                    young++;
                }

                if (_entries[i].Serial == serial)
                {
                    same = i;
                }

                if (age >= oldestAge)
                {
                    oldestAge = age;
                    oldest = i;
                }
            }

            int slot;

            if (same >= 0)
            {
                slot = same;
            }
            else if (_count < Capacity)
            {
                slot = _count++;
                young++;
            }
            else
            {
                slot = oldest;

                if (oldestAge <= ExpiryMs)
                {
                    UnexpiredOverwrites++;
                }
                else
                {
                    young++;
                }
            }

            if (same >= 0 && now - _entries[slot].LastSeen > ExpiryMs)
            {
                young++;
            }

            if (young > HighWater)
            {
                HighWater = young;
            }

            ref Entry e = ref _entries[slot];
            e.Serial = serial;
            e.Body = body;
            e.IdleDue = (uint)Math.Max(0, idleDue);
            e.LastSeen = now;
        }

        /// <summary>The serial is created with <paramref name="body"/>: the restored idle due time (in the future), or false
        /// (no entry, expired, or another body: a fresh draw). A hit removes the entry; an expired one is dropped too.</summary>
        public bool TryRestore(uint serial, ushort body, uint now, out long idleDue)
        {
            idleDue = 0;

            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].Serial != serial)
                {
                    continue;
                }

                Entry e = _entries[i];

                if (now - e.LastSeen > ExpiryMs)
                {
                    RemoveAt(i);
                    Misses++;

                    return false;
                }

                if (e.Body != body)
                {
                    Misses++;

                    return false;
                }

                RemoveAt(i);
                Hits++;
                long due = e.IdleDue;

                while (due <= now)
                {
                    due += Mobile.IdleStepMs();
                }

                idleDue = due;

                return true;
            }

            Misses++;

            return false;
        }

        private void RemoveAt(int i)
        {
            _entries[i] = _entries[--_count];
            _entries[_count] = default;
        }

        /// <summary>Map change, logout, world clear.</summary>
        public void Clear()
        {
            if (Writes != 0 || Hits != 0 || Misses != 0)
            {
                Log.Info(TakeSummary());
            }

            Array.Clear(_entries, 0, _count);
            _count = 0;
        }

        /// <summary>The counters since the last call, reset.</summary>
        public string TakeSummary()
        {
            string s = FormattableString.Invariant(
                $"[idle-cadence] entries {_count} young_hw {HighWater}/{Capacity} unexpired_overwrites {UnexpiredOverwrites} writes {Writes} hits {Hits} misses {Misses}");
            HighWater = _count;
            UnexpiredOverwrites = Writes = Hits = Misses = 0;

            return s;
        }
    }
}
