using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClassicUO
{
    internal enum SlowFrameKind : byte
    {
        Play,
        Held,
        Login
    }

    /// <summary>
    /// The session timeline's frames over 8 ms, kept in memory: a count, a histogram and a fixed ring of the worst ones. A slow
    /// frame used to log a line (a string.Format and a synchronous console write on the game thread, which made the frame slower
    /// and allocated); now <see cref="Record"/> only writes into fixed arrays and the text is built when a window report, a
    /// session end or the process exit flushes it. Game thread only, except <see cref="Flush"/> at exit.
    /// </summary>
    internal static class SlowFrameStats
    {
        public const double ThresholdMs = 8;
        public const int RingSize = 32;

        // 8-12, 12-16, 16-25, 25-50, 50-100, over 100 ms
        private static readonly double[] _edges = { 12, 16, 25, 50, 100 };
        private static readonly string[] _bucketNames = { "8-12", "12-16", "16-25", "25-50", "50-100", ">100" };

        internal struct Entry
        {
            public double Time;
            public float Ms;
            public SlowFrameKind Kind;
            public int Tag;
        }

        private static readonly Entry[] _ring = new Entry[RingSize];
        private static readonly int[] _hist = new int[6];
        private static readonly int[] _kindCount = new int[3];
        private static readonly double[] _kindMax = new double[3];
        private static int _ringCount, _total, _afterReport;
        private static readonly List<string> _tags = new() { "-" };

        /// <summary>The id of a window tag, for <see cref="Record"/> (allocates the first time a tag is seen; call it when the tag is set).</summary>
        public static int TagId(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return 0;
            }

            lock (_tags)
            {
                int i = _tags.IndexOf(tag);

                if (i < 0)
                {
                    _tags.Add(tag);
                    i = _tags.Count - 1;
                }

                return i;
            }
        }

        public static int Total => _total;

        public static int Bucket(double ms)
        {
            int b = 0;

            while (b < _edges.Length && ms >= _edges[b])
            {
                b++;
            }

            return b;
        }

        /// <summary>A frame after a window report: not counted (the report's own cost), only noted.</summary>
        public static void NoteAfterReport() => _afterReport++;

        /// <summary>Records one slow frame: no allocation, no formatting.</summary>
        public static void Record(double time, double ms, SlowFrameKind kind, int tag)
        {
            _total++;
            _hist[Bucket(ms)]++;
            int k = (int)kind;
            _kindCount[k]++;

            if (ms > _kindMax[k])
            {
                _kindMax[k] = ms;
            }

            if (_ringCount < RingSize)
            {
                _ring[_ringCount++] = new Entry { Time = time, Ms = (float)ms, Kind = kind, Tag = tag };

                return;
            }

            int min = 0;

            for (int i = 1; i < RingSize; i++)
            {
                if (_ring[i].Ms < _ring[min].Ms)
                {
                    min = i;
                }
            }

            if (ms > _ring[min].Ms)
            {
                _ring[min] = new Entry { Time = time, Ms = (float)ms, Kind = kind, Tag = tag };
            }
        }

        /// <summary>
        /// Appends the report (a header with the counts and histogram, then the worst frames in time order, in the old
        /// per-frame line text) and clears the stats. Returns false, appending nothing, when nothing was recorded.
        /// </summary>
        public static bool Flush(StringBuilder sb, string scope)
        {
            if (_total == 0 && _afterReport == 0)
            {
                return false;
            }

            var inv = CultureInfo.InvariantCulture;
            sb.Append(inv, $"[gcs] slow frames {scope}: {_total} over {ThresholdMs:0} ms: play {_kindCount[0]} (max {_kindMax[0]:0.0} ms), held {_kindCount[1]} (max {_kindMax[1]:0.0} ms), login {_kindCount[2]} (max {_kindMax[2]:0.0} ms), after a window report (not counted) {_afterReport}; histogram ms");

            for (int b = 0; b < _hist.Length; b++)
            {
                sb.Append(inv, $" {_bucketNames[b]}: {_hist[b]}");
                sb.Append(b < _hist.Length - 1 ? "," : "");
            }

            sb.Append(inv, $"; worst {_ringCount}");
            Array.Sort(_ring, 0, _ringCount, TimeOrder.Instance);

            for (int i = 0; i < _ringCount; i++)
            {
                ref Entry e = ref _ring[i];
                string tag = e.Tag > 0 && e.Tag < _tags.Count ? _tags[e.Tag] : "-";
                string kind = e.Kind == SlowFrameKind.Play ? "play" : e.Kind == SlowFrameKind.Held ? "held" : "login";
                sb.Append(inv, $"\n[gcs] +{e.Time:0.00}s slow frame {e.Ms:0.00} ms, {kind}, tag {tag}");
            }

            Reset();

            return true;
        }

        public static void Reset()
        {
            _ringCount = _total = _afterReport = 0;
            Array.Clear(_hist);
            Array.Clear(_kindCount);
            Array.Clear(_kindMax);
        }

        internal static int HistogramCount(int bucket) => _hist[bucket];

        internal static int RingCount => _ringCount;

        internal static float RingMin()
        {
            float m = float.MaxValue;

            for (int i = 0; i < _ringCount; i++)
            {
                m = Math.Min(m, _ring[i].Ms);
            }

            return m;
        }

        private sealed class TimeOrder : IComparer<Entry>
        {
            public static readonly TimeOrder Instance = new();

            public int Compare(Entry a, Entry b) => a.Time.CompareTo(b.Time);
        }
    }
}
