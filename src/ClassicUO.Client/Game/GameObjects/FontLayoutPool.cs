// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    /// <summary>font_layout_pool (gump-system G5): RenderedText returns its layout to the font loader's node pool.</summary>
    internal static class FontLayoutPoolSwitch
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_FONT_LAYOUT_POOL") switch
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

        public static bool Enabled => OverrideForTests ?? _env ?? Settings.GlobalSettings?.FontLayoutPool ?? false;
    }

#if CUO_DEVTOOLS
    /// <summary>
    /// CUO_FONT_LAYOUT_CHECK=1 (developer builds): every layout a RenderedText takes is copied into a private, never pooled
    /// snapshot; at every 16th draw and when the text replaces or drops the layout, the live node chain is compared with the
    /// snapshot. With font_layout_pool on, a node that went back to the pool while someone still used it is rewritten by the
    /// next layout, which shows up here as a mismatch (logged with the text and the first differing field). A summary with
    /// the switch state, the check count and the pool size is logged every 2000 checks. One per client (UltimaOnline).
    /// </summary>
    internal sealed class FontLayoutCheck
    {
        public static readonly bool EnvEnabled = Environment.GetEnvironmentVariable("CUO_FONT_LAYOUT_CHECK") == "1";

        /// <summary>Tests: check without the environment variable.</summary>
        public bool ForceForTests;

        public bool Active => EnvEnabled || ForceForTests;

        public long Checks { get; private set; }

        public long Mismatches { get; private set; }

        public string LastMismatch { get; private set; }

        public sealed class Snapshot
        {
            public readonly List<Node> Nodes = new List<Node>();
        }

        public struct Node
        {
            public TEXT_ALIGN_TYPE Align;
            public int CharCount, CharStart, IndentionOffset, MaxHeight, Width;
            public MultilinesFontData[] Data;
            public List<string> WebLinks;
        }

        public Snapshot Take(MultilinesFontInfo info)
        {
            if (info == null)
            {
                return null;
            }

            var s = new Snapshot();

            for (MultilinesFontInfo p = info; p != null; p = p.Next)
            {
                s.Nodes.Add(
                    new Node
                    {
                        Align = p.Align,
                        CharCount = p.CharCount,
                        CharStart = p.CharStart,
                        IndentionOffset = p.IndentionOffset,
                        MaxHeight = p.MaxHeight,
                        Width = p.Width,
                        Data = p.Data.ToArray(),
                        WebLinks = p.WebLinks
                    }
                );
            }

            return s;
        }

        public void Compare(string text, MultilinesFontInfo info, Snapshot snapshot, string when)
        {
            // no snapshot: the layout was made before the check was on
            if (snapshot == null)
            {
                return;
            }

            Checks++;
            string mismatch = Diff(info, snapshot);

            if (mismatch != null)
            {
                Mismatches++;
                LastMismatch = mismatch;
                Log.Warn($"[font-layout-check] mismatch at {when} text='{text}': {mismatch}");
            }

            if (Checks % 2000 == 0)
            {
                Log.Info(
                    $"[font-layout-check] font_layout_pool={FontLayoutPoolSwitch.Enabled} checks={Checks} mismatches={Mismatches} "
                        + $"pooled nodes={Client.Game?.UO?.FileManager?.Fonts?.PooledLayoutNodes}"
                );
            }
        }

        private static string Diff(MultilinesFontInfo info, Snapshot snapshot)
        {
            int i = 0;
            MultilinesFontInfo p = info;

            for (; p != null && i < snapshot.Nodes.Count; p = p.Next, i++)
            {
                Node n = snapshot.Nodes[i];

                if (p.Align != n.Align || p.CharCount != n.CharCount || p.CharStart != n.CharStart
                    || p.IndentionOffset != n.IndentionOffset || p.MaxHeight != n.MaxHeight || p.Width != n.Width)
                {
                    return $"node {i}: align/count/start/indent/height/width {p.Align}/{p.CharCount}/{p.CharStart}/{p.IndentionOffset}/"
                        + $"{p.MaxHeight}/{p.Width}, expected {n.Align}/{n.CharCount}/{n.CharStart}/{n.IndentionOffset}/{n.MaxHeight}/{n.Width}";
                }

                if (!ReferenceEquals(p.WebLinks, n.WebLinks))
                {
                    return $"node {i}: web links list replaced";
                }

                if (p.Data.Count != n.Data.Length)
                {
                    return $"node {i}: {p.Data.Count} glyphs, expected {n.Data.Length}";
                }

                for (int c = 0; c < n.Data.Length; c++)
                {
                    MultilinesFontData a = p.Data[c], b = n.Data[c];

                    if (a.Item != b.Item || a.Color != b.Color || a.Flags != b.Flags || a.Font != b.Font || a.LinkID != b.LinkID)
                    {
                        return $"node {i} glyph {c}: '{a.Item}' expected '{b.Item}'";
                    }
                }
            }

            if (p != null || i != snapshot.Nodes.Count)
            {
                return $"node count differs (expected {snapshot.Nodes.Count})";
            }

            return null;
        }
    }
#endif
}
