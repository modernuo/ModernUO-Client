// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using System.Collections.Generic;
using System.Text;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>
    /// CUO_GUMP_LAYOUT_CHECK=1 (developer builds; gump_layout_v2 on): each server-gump open is also tokenized by the old
    /// two-pass tokenizer and built by the old control loop into a detached shadow gump; the entries (token by token) and the
    /// built trees (type, bounds, page, alpha, input flags, tooltip, serial and the type's key fields; the gump's flags) are
    /// compared. A cached layout (gump_layout_cache) is compared at every hit. Logs the gump id and the first mismatch, and a
    /// summary with the switch states every 50 checks.
    /// </summary>
    internal static class GumpLayoutCheck
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("CUO_GUMP_LAYOUT_CHECK") == "1";

        private static long _checks,
            _mismatches;

        public static long Checks => _checks;
        public static long Mismatches => _mismatches;

        /// <summary>Tests: count without the environment variable.</summary>
        [ThreadStatic] internal static bool ForceForTests;

        public static bool Active => Enabled || ForceForTests;

        public static void CompareParse(uint gumpId, GumpLayout parsed, string layout)
        {
            List<string>[] old = GumpFactory.TokenizeForCheck(layout);
            string mismatch = null;

            if (old.Length != parsed.Entries.Length)
            {
                mismatch = $"entries old={old.Length} new={parsed.Entries.Length}";
            }
            else
            {
                for (int i = 0; i < old.Length && mismatch == null; i++)
                {
                    GumpEntry e = parsed.Entries[i];
                    GumpArgs a = parsed.Args(e);

                    if (a.Count != old[i].Count)
                    {
                        mismatch = $"entry {i} tokens old={old[i].Count} new={a.Count}";

                        break;
                    }

                    for (int t = 0; t < a.Count; t++)
                    {
                        if (!a[t].SequenceEqual(old[i][t]))
                        {
                            mismatch = $"entry {i} token {t} old='{old[i][t]}' new='{a[t].ToString()}'";

                            break;
                        }
                    }
                }
            }

            Report(gumpId, "parse", mismatch);
        }

        public static void CompareBuild(World world, Gump gump, string layout, string[] lines)
        {
            var shadow = new Gump(world, 0, gump.ServerSerial)
            {
                CanMove = true,
                CanCloseWithRightClick = true,
                CanCloseWithEsc = true,
                InvalidateContents = false,
                IsFromServer = true
            };

            string mismatch;

            try
            {
                GumpFactory.BuildControls(world, shadow, GumpFactory.TokenizeForCheck(layout), lines, true);
                mismatch = FirstDifference(Signature(gump), Signature(shadow));
            }
            finally
            {
                shadow.Dispose();
            }

            Report(gump.ServerSerial, "build", mismatch);
        }

        internal static List<string> Signature(Gump gump)
        {
            var list = new List<string>
            {
                $"gump move={gump.CanMove} rclose={gump.CanCloseWithRightClick} esc={gump.CanCloseWithEsc} master={gump.MasterGumpSerial}"
            };

            foreach (Control c in gump.Children)
            {
                if (c.IsDisposed)
                {
                    continue;
                }

                var sb = new StringBuilder();
                sb.Append(c.GetType().Name).Append(' ').Append(c.Bounds).Append(" page").Append(c.Page)
                    .Append(" a").Append(c.Alpha).Append(" in").Append(c.AcceptMouseInput).Append(" pr").Append(c.Priority)
                    .Append(" s").Append(c.LocalSerial).Append(" tip'").Append(c.Tooltip).Append('\'');

                switch (c)
                {
                    case Button b:
                        sb.Append(" id").Append(b.ButtonID).Append(" act").Append(b.ButtonAction).Append(" to").Append(b.ToPage)
                            .Append(" g").Append(b.ButtonGraphicNormal).Append('/').Append(b.ButtonGraphicPressed);

                        break;
                    case Label l:
                        sb.Append(" '").Append(l.Text).Append("' hue").Append(l.Hue);

                        break;
                    case HtmlControl h:
                        sb.Append(" '").Append(h.Text).Append("' bg").Append(h.HasBackground).Append(" sb").Append(h.HasScrollbar)
                            .Append(h.UseFlagScrollbar);

                        break;
                    case RadioButton r:
                        sb.Append(" group").Append(r.GroupIndex).Append(" on").Append(r.IsChecked);

                        break;
                    case Checkbox k:
                        sb.Append(" on").Append(k.IsChecked);

                        break;
                    case StbTextBox t:
                        sb.Append(" '").Append(t.Text).Append("' hue").Append(t.Hue);

                        break;
                    case GumpPic g:
                        sb.Append(" g").Append(g.Graphic).Append(" hue").Append(g.Hue).Append(" partial").Append(g.IsPartialHue);

                        break;
                    case GumpPicInPic g:
                        sb.Append(" g").Append(g.Graphic).Append(" hue").Append(g.Hue).Append(" partial").Append(g.IsPartialHue);

                        break;
                    case GumpPicTiled g:
                        sb.Append(" g").Append(g.Graphic);

                        break;
                    case ResizePic r:
                        sb.Append(" g").Append(r.Graphic);

                        break;
                    case StaticPic s:
                        sb.Append(" g").Append(s.Graphic).Append(" hue").Append(s.Hue);

                        break;
                }

                list.Add(sb.ToString());
            }

            return list;
        }

        private static string FirstDifference(List<string> a, List<string> b)
        {
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                if (a[i] != b[i])
                {
                    return $"control {i - 1}: new '{a[i]}' old '{b[i]}'";
                }
            }

            return a.Count != b.Count ? $"controls new={a.Count - 1} old={b.Count - 1}" : null;
        }

        private static void Report(uint gumpId, string what, string mismatch)
        {
            _checks++;

            if (mismatch != null)
            {
                _mismatches++;
                Log.Warn($"[gump-layout-check] mismatch gump 0x{gumpId:X8} {what}: {mismatch}");
            }

            if (_checks % 50 == 0)
            {
                Log.Info(
                    $"[gump-layout-check] gump_layout_v2={GumpLayoutSwitch.Enabled} gump_layout_cache={GumpLayoutCache.Enabled} "
                        + $"checks={_checks} mismatches={_mismatches} cache hits={GumpLayoutCache.Hits} misses={GumpLayoutCache.Misses}"
                );
            }
        }
    }
}
#endif
