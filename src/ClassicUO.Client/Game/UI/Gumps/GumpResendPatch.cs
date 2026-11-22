// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>
    /// gump_resend_patch (owner question Q1 (b), default off, needs gump_layout_v2): a server gump resent with the layout of
    /// the open one (same sender, id and layout string) keeps its controls; only the text controls whose line changed take
    /// the new line. Typed text, checkbox and radio states, html scroll positions, the page and keyboard focus survive the
    /// refresh; a text entry whose own line changed takes the server's new value. A changed line that cannot be patched (an
    /// html text going from or to empty) rebuilds the gump as before. Read at every open; CUO_GUMP_RESEND_PATCH=1|0
    /// overrides it in developer builds, CUO_GUMP_RESEND_CHECK=1 compares each patched gump with an old-path rebuild.
    /// </summary>
    internal static class GumpResendPatchSwitch
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_GUMP_RESEND_PATCH") switch
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

        public static bool Enabled => OverrideForTests ?? _env ?? Settings.GlobalSettings.GumpResendPatch;
    }

    /// <summary>What a server gump built with gump_resend_patch on needs to patch a resend: its layout, lines and text controls.</summary>
    internal sealed class ServerGumpPatch
    {
        private enum Kind : byte
        {
            Label,
            CroppedText,
            Html,
            TextEntry
        }

        private struct Bound
        {
            public Control Control;
            public int Line;
            public Kind Kind;
        }

        private readonly List<Bound> _bound = new List<Bound>();
        private string[] _lines;

        public ServerGumpPatch(string layout, string[] lines)
        {
            Layout = layout;
            _lines = lines;
        }

        public string Layout { get; }

        /// <summary>The text entries that took a new server line in the last patch (the check expects their text from the server).</summary>
        public List<StbTextBox> EntriesUpdated { get; } = new List<StbTextBox>();

        public void BindLabel(Label c, int line) => _bound.Add(new Bound { Control = c, Line = line, Kind = Kind.Label });

        public void BindCroppedText(CroppedText c, int line) => _bound.Add(new Bound { Control = c, Line = line, Kind = Kind.CroppedText });

        public void BindHtml(HtmlControl c, int line) => _bound.Add(new Bound { Control = c, Line = line, Kind = Kind.Html });

        public void BindTextEntry(StbTextBox c, int line) => _bound.Add(new Bound { Control = c, Line = line, Kind = Kind.TextEntry });

        private static string Line(string[] lines, int index) => index >= 0 && index < lines.Length ? lines[index] : string.Empty;

        /// <summary>
        /// Patches the controls when <paramref name="layout"/> is this gump's layout and every changed line can be patched;
        /// false (nothing changed): the caller rebuilds.
        /// </summary>
        public bool TryApply(string layout, string[] lines)
        {
            if (!string.Equals(layout, Layout, StringComparison.Ordinal))
            {
                return false;
            }

            for (int i = 0; i < _bound.Count; i++)
            {
                Bound b = _bound[i];

                if (b.Control.IsDisposed)
                {
                    return false;
                }

                if (b.Kind == Kind.Html)
                {
                    string before = Line(_lines, b.Line), after = Line(lines, b.Line);

                    if (!string.Equals(before, after, StringComparison.Ordinal) && (before.Length == 0 || after.Length == 0))
                    {
                        return false;
                    }
                }
            }

            EntriesUpdated.Clear();

            for (int i = 0; i < _bound.Count; i++)
            {
                Bound b = _bound[i];
                string after = Line(lines, b.Line);

                if (string.Equals(Line(_lines, b.Line), after, StringComparison.Ordinal))
                {
                    continue;
                }

                switch (b.Kind)
                {
                    case Kind.Label:
                        ((Label)b.Control).Text = after;

                        break;

                    case Kind.CroppedText:
                        ((CroppedText)b.Control).SetText(after);

                        break;

                    case Kind.Html:
                        ((HtmlControl)b.Control).SetServerText(after);

                        break;

                    case Kind.TextEntry:
                        var entry = (StbTextBox)b.Control;
                        entry.SetText(after);
                        EntriesUpdated.Add(entry);

                        break;
                }
            }

            _lines = lines;

            return true;
        }
    }

#if CUO_DEVTOOLS
    /// <summary>
    /// CUO_GUMP_RESEND_CHECK=1 (developer builds; gump_resend_patch on): every patched resend is also built by the old path
    /// (old tokenizer and control loop) into a detached shadow gump. The state the patch keeps on purpose (typed text of
    /// entries whose line did not change, checkbox and radio states) is copied from the live gump into the shadow, then the
    /// two trees are compared (GumpLayoutCheck.Signature). Logs the gump id and the first difference, and a summary with
    /// the switch states every 50 checks. One per client (UltimaOnline).
    /// </summary>
    internal sealed class GumpResendCheck
    {
        public static readonly bool EnvEnabled = Environment.GetEnvironmentVariable("CUO_GUMP_RESEND_CHECK") == "1";

        /// <summary>Tests: check without the environment variable.</summary>
        public bool ForceForTests;

        public bool Active => EnvEnabled || ForceForTests;

        public long Checks { get; private set; }

        public long Mismatches { get; private set; }

        public string LastMismatch { get; private set; }

        public long Patched { get; set; }

        public long Rebuilt { get; set; }

        public void Compare(World world, Gump gump, ServerGumpPatch patch, string[] lines)
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
                GumpFactory.BuildControls(world, shadow, GumpFactory.TokenizeForCheck(patch.Layout), lines, true);
                mismatch = CopyKeptState(gump, shadow, patch)
                    ?? FirstDifference(GumpLayoutCheck.Signature(gump), GumpLayoutCheck.Signature(shadow));
            }
            finally
            {
                shadow.Dispose();
            }

            Checks++;

            if (mismatch != null)
            {
                Mismatches++;
                LastMismatch = mismatch;
                Log.Warn($"[gump-resend-check] mismatch gump 0x{gump.ServerSerial:X8}: {mismatch}");
            }

            if (Checks % 50 == 0)
            {
                Log.Info(
                    $"[gump-resend-check] gump_resend_patch={GumpResendPatchSwitch.Enabled} gump_layout_v2={GumpLayoutSwitch.Enabled} "
                        + $"checks={Checks} mismatches={Mismatches} patched={Patched} rebuilt={Rebuilt}"
                );
            }
        }

        private static string CopyKeptState(Gump live, Gump shadow, ServerGumpPatch patch)
        {
            var a = new List<Control>();
            var b = new List<Control>();

            foreach (Control c in live.Children)
            {
                if (!c.IsDisposed)
                {
                    a.Add(c);
                }
            }

            foreach (Control c in shadow.Children)
            {
                if (!c.IsDisposed)
                {
                    b.Add(c);
                }
            }

            if (a.Count != b.Count)
            {
                return $"controls patched={a.Count} old={b.Count}";
            }

            for (int i = 0; i < a.Count; i++)
            {
                switch (a[i])
                {
                    case StbTextBox t when b[i] is StbTextBox s && !patch.EntriesUpdated.Contains(t):
                        s.SetText(t.Text);

                        break;

                    case RadioButton r when b[i] is RadioButton s:
                        s.IsChecked = r.IsChecked;

                        break;

                    case Checkbox k when b[i] is Checkbox s:
                        s.IsChecked = k.IsChecked;

                        break;
                }
            }

            return null;
        }

        private static string FirstDifference(List<string> a, List<string> b)
        {
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                if (a[i] != b[i])
                {
                    return $"control {i - 1}: patched '{a[i]}' old '{b[i]}'";
                }
            }

            return a.Count != b.Count ? $"controls patched={a.Count - 1} old={b.Count - 1}" : null;
        }
    }
#endif
}
