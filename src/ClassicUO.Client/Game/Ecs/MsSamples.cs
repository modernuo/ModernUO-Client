// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;

namespace ClassicUO.Game
{
    /// <summary>CUO_LOOK_PREFETCH_CHECK (CLEANUP(release)): durations in ms since the last summary (the first
    /// <see cref="Capacity"/> kept for the percentiles), with the count that reached their bound. Main thread; no allocation
    /// after construction.</summary>
    internal sealed class MsSamples
    {
        public const int Capacity = 4096;
        private readonly uint[] _ms = new uint[Capacity];
        private int _count;
        private long _total, _capped, _nonZero;

        public void Add(uint ms, bool capped)
        {
            _total++;

            if (capped)
            {
                _capped++;
            }

            if (ms != 0)
            {
                _nonZero++;
            }

            if (_count < Capacity)
            {
                _ms[_count++] = ms;
            }
        }

        /// <summary>"n N waited W p50 P p95 P p99 P max M cap_hits C" since the last call; then empty.</summary>
        public string Summary()
        {
            Array.Sort(_ms, 0, _count);
            string r = $"n {_total} waited {_nonZero} p50 {P(50)} p95 {P(95)} p99 {P(99)} max {(_count == 0 ? 0 : _ms[_count - 1])} cap_hits {_capped}";
            _count = 0;
            _total = _capped = _nonZero = 0;

            return r;
        }

        private uint P(int pct) => _count == 0 ? 0 : _ms[Math.Min(_count - 1, _count * pct / 100)];
    }
}
#endif
