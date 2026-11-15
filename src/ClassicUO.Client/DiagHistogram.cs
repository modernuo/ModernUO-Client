// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
using System;

namespace ClassicUO
{
    // Exact integer microseconds through 8191; larger samples share an open bucket.
    // The report labels percentiles as upper bounds, using the observed max for that bucket.
    internal sealed class DiagHistogram
    {
        private readonly int[] _bins = new int[8193];
        public long Count { get; private set; }
        public long Max { get; private set; }
        public void Add(long us)
        {
            us = Math.Max(0, us);
            _bins[(int)Math.Min(8192, us)]++;
            Count++;
            Max = Math.Max(Max, us);
        }
        public long Percentile(double fraction)
        {
            long target = Math.Max(1, (long)Math.Ceiling(Count * fraction)), seen = 0;
            if (Count == 0) return 0;
            for (int i = 0; i < _bins.Length; i++)
            {
                seen += _bins[i];
                if (seen >= target) return i == 8192 ? Max : i;
            }
            return Max;
        }
        public void Clear()
        {
            Array.Clear(_bins);
            Count = Max = 0;
        }
    }
}
#endif
