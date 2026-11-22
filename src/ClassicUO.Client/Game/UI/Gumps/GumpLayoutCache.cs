// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>
    /// gump_layout_cache (G4, default off, needs gump_layout_v2): the parsed layouts of the last <see cref="Capacity"/> server
    /// gumps, keyed by gump id and layout string (hash and length first, then an ordinal compare), least recently used out.
    /// A resend of the same layout (ModernUO refreshes: timers, paged lists, refresh buttons) skips the parse. Main thread
    /// only. Holds the layout strings and their token ranges: at most a few hundred KB for 32 large gumps.
    /// CUO_GUMP_LAYOUT_CACHE=1|0 overrides the setting in developer builds.
    /// </summary>
    internal static class GumpLayoutCache
    {
        public const int Capacity = 32;

#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_GUMP_LAYOUT_CACHE") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
#else
        private static readonly bool? _env = null;
#endif

        /// <summary>Tests: the switch on this thread (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => GumpLayoutSwitch.Enabled && (OverrideForTests ?? _env ?? Settings.GlobalSettings.GumpLayoutCache);

        private struct Slot
        {
            public uint Id;
            public int Hash;
            public GumpLayout Layout;
            public long LastUse;
        }

        private static readonly Slot[] _slots = new Slot[Capacity];
        private static long _clock;

        public static long Hits { get; private set; }
        public static long Misses { get; private set; }

        public static GumpLayout GetOrParse(uint gumpId, string layout)
        {
            int hash = layout.GetHashCode();
            int victim = 0;
            long oldest = long.MaxValue;

            for (int i = 0; i < _slots.Length; i++)
            {
                ref Slot slot = ref _slots[i];

                if (slot.Layout == null)
                {
                    if (oldest != long.MinValue)
                    {
                        victim = i;
                        oldest = long.MinValue;
                    }

                    continue;
                }

                if (
                    slot.Id == gumpId
                    && slot.Hash == hash
                    && slot.Layout.Source.Length == layout.Length
                    && string.Equals(slot.Layout.Source, layout, StringComparison.Ordinal)
                )
                {
                    slot.LastUse = ++_clock;
                    Hits++;

                    return slot.Layout;
                }

                if (slot.LastUse < oldest)
                {
                    victim = i;
                    oldest = slot.LastUse;
                }
            }

            GumpLayout parsed = GumpLayout.Parse(layout);
            Misses++;

            if (parsed.Entries.Length != 0)
            {
                _slots[victim] = new Slot { Id = gumpId, Hash = hash, Layout = parsed, LastUse = ++_clock };
            }

            return parsed;
        }

        public static int Count
        {
            get
            {
                int n = 0;

                foreach (Slot s in _slots)
                {
                    if (s.Layout != null)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        /// <summary>Managed bytes the cache holds (layout strings plus token ranges).</summary>
        public static long HeldBytes
        {
            get
            {
                long n = 0;

                foreach (Slot s in _slots)
                {
                    if (s.Layout != null)
                    {
                        n += 22 + s.Layout.Source.Length * 2L + s.Layout.HeldBytes;
                    }
                }

                return n;
            }
        }

        public static void Clear()
        {
            Array.Clear(_slots);
            Hits = 0;
            Misses = 0;
        }
    }
}
