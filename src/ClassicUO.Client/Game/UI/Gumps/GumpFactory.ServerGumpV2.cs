// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Network;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>
    /// gump_layout_v2 (G3, default off): server gumps parse their layout once into a <see cref="GumpLayout"/> (token ranges of
    /// the layout string, no token strings) and build the controls by entry kind. Read at every open; CUO_GUMP_LAYOUT_V2=1|0
    /// overrides it in developer builds. CUO_GUMP_LAYOUT_CHECK=1 compares each parse with the old tokenizer and each built
    /// tree with the old builder's.
    /// </summary>
    internal static class GumpLayoutSwitch
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_GUMP_LAYOUT_V2") switch
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

        public static bool Enabled => OverrideForTests ?? _env ?? Settings.GlobalSettings.GumpLayoutV2;
    }

    internal static partial class GumpFactory
    {
        private static Gump OpenServerGumpV2(
            World world,
            uint sender,
            uint gumpID,
            int x,
            int y,
            string layout,
            string[] lines
        )
        {
            GumpLayout parsed = GumpLayoutCache.Enabled ? GumpLayoutCache.GetOrParse(gumpID, layout) : GumpLayout.Parse(layout);

#if CUO_DEVTOOLS
            if (GumpLayoutCheck.Active)
            {
                GumpLayoutCheck.CompareParse(gumpID, parsed, layout);
            }
#endif

            if (parsed.Entries.Length == 0)
            {
                return null;
            }

            Gump gump = FindOrCreateServerGump(world, sender, gumpID, x, y, out bool mustBeAdded);

            PrefetchLayoutGumps(parsed);
            BuildControls(world, gump, parsed, lines, false);

#if CUO_DEVTOOLS
            if (GumpLayoutCheck.Active)
            {
                GumpLayoutCheck.CompareBuild(world, gump, layout, lines);
            }
#endif

            return FinishServerGump(gump, mustBeAdded);
        }

        /// <summary>The gump pictures <paramref name="layout"/> names, decoded on workers in one batch (as the old token scan).</summary>
        internal static void PrefetchLayoutGumps(GumpLayout layout)
        {
            if (!Renderer.Gumps.Gump.AsyncRequests || !Client.Game.UO.FileManager.Gumps.CanDecodeOnWorker)
            {
                return;
            }

            _layoutGumps.Clear();

            GumpEntry[] entries = layout.Entries;

            for (int i = 0; i < entries.Length; i++)
            {
                ref readonly GumpEntry e = ref entries[i];
                GumpArgs p = layout.Args(e);

                switch (e.Kind)
                {
                    case GumpEntryKind.GumpPic:
                        AddLayoutGump(p, 3);

                        break;

                    case GumpEntryKind.Button:
                    case GumpEntryKind.Checkbox:
                    case GumpEntryKind.Radio:
                        AddLayoutGump(p, 3);
                        AddLayoutGump(p, 4);

                        break;

                    case GumpEntryKind.GumpPicTiled:
                        AddLayoutGump(p, 5);

                        break;

                    case GumpEntryKind.ResizePic when p.Count > 3:
                        ushort first = UInt16Converter.Parse(p[3]);

                        for (int k = 0; k < 9; k++)
                        {
                            _layoutGumps.Add((uint)(first + k));
                        }

                        break;
                }
            }

            if (_layoutGumps.Count >= 2)
            {
                Client.Game.UO.Gumps.Prefetch(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_layoutGumps));
            }
        }

        private static void AddLayoutGump(GumpArgs p, int index)
        {
            if (p.Count > index)
            {
                _layoutGumps.Add(UInt16Converter.Parse(p[index]));
            }
        }

        /// <summary>
        /// The control loop of gump_layout_v2: the old loop's controls and order, dispatched on the parsed kind. Strings are
        /// made only for text a control keeps (cliloc arguments, tooltips).
        /// </summary>
        internal static void BuildControls(World world, Gump gump, GumpLayout layout, string[] lines, bool shadow)
        {
            int group = 0;
            int page = 0;
            bool textBoxFocused = false;
            GumpEntry[] entries = layout.Entries;

            for (int cnt = 0; cnt < entries.Length; cnt++)
            {
                ref readonly GumpEntry e = ref entries[cnt];
                GumpArgs p = layout.Args(e);

                switch (e.Kind)
                {
                    case GumpEntryKind.None:
                        break;

                    case GumpEntryKind.Button:
                        gump.Add(new Button(p), page);

                        break;

                    case GumpEntryKind.ButtonTileArt:
                        gump.Add(new ButtonTileArt(p), page);

                        break;

                    case GumpEntryKind.CheckerTrans:
                        var checkerTrans = new CheckerTrans(p);
                        gump.Add(checkerTrans, page);
                        ApplyTrans(gump, page, checkerTrans.X, checkerTrans.Y, checkerTrans.Width, checkerTrans.Height);

                        break;

                    case GumpEntryKind.CroppedText:
                        gump.Add(new CroppedText(p, lines), page);

                        break;

                    case GumpEntryKind.TilePicAsGumpPic:
                    case GumpEntryKind.GumpPic:
                        GumpPic pic;

                        if (p.Count >= 6 && p[5].IndexOf("virtuegumpitem", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            pic = new VirtueGumpPic(world, p);
                            pic.ContainsByBounds = true;
                            SetVirtueTooltip(pic);
                        }
                        else
                        {
                            pic = new GumpPic(p);
                        }

                        gump.Add(pic, page);

                        break;

                    case GumpEntryKind.GumpPicTiled:
                        gump.Add(new GumpPicTiled(p), page);

                        break;

                    case GumpEntryKind.HtmlGump:
                        gump.Add(new HtmlControl(p, lines), page);

                        break;

                    case GumpEntryKind.XmfHtmlGump:
                        gump.Add(
                            new HtmlControl(
                                int.Parse(p[1]),
                                int.Parse(p[2]),
                                int.Parse(p[3]),
                                int.Parse(p[4]),
                                int.Parse(p[6]) == 1,
                                int.Parse(p[7]) != 0,
                                !p.Is(6, "0") && p.Is(7, "2"),
                                Client.Game.UO.FileManager.Clilocs.GetString(ParseCliloc(p[5])),
                                0,
                                true
                            )
                            {
                                IsFromServer = true
                            },
                            page
                        );

                        break;

                    case GumpEntryKind.XmfHtmlGumpColor:
                    {
                        int color = int.Parse(p[8]);

                        if (color == 0x7FFF)
                        {
                            color = 0x00FFFFFF;
                        }

                        gump.Add(
                            new HtmlControl(
                                int.Parse(p[1]),
                                int.Parse(p[2]),
                                int.Parse(p[3]),
                                int.Parse(p[4]),
                                int.Parse(p[6]) == 1,
                                int.Parse(p[7]) != 0,
                                !p.Is(6, "0") && p.Is(7, "2"),
                                Client.Game.UO.FileManager.Clilocs.GetString(ParseCliloc(p[5])),
                                color,
                                true
                            )
                            {
                                IsFromServer = true
                            },
                            page
                        );

                        break;
                    }

                    case GumpEntryKind.XmfHtmlTok:
                    {
                        int color = int.Parse(p[7]);

                        if (color == 0x7FFF)
                        {
                            color = 0x00FFFFFF;
                        }

                        gump.Add(
                            new HtmlControl(
                                int.Parse(p[1]),
                                int.Parse(p[2]),
                                int.Parse(p[3]),
                                int.Parse(p[4]),
                                int.Parse(p[5]) == 1,
                                int.Parse(p[6]) != 0,
                                !p.Is(5, "0") && p.Is(6, "2"),
                                p.Count >= 9
                                    ? Client.Game.UO.FileManager.Clilocs.Translate(ParseCliloc(p[8]), JoinArgs(p, 9, true))
                                    : Client.Game.UO.FileManager.Clilocs.GetString(ParseCliloc(p[8])),
                                color,
                                true
                            )
                            {
                                IsFromServer = true
                            },
                            page
                        );

                        break;
                    }

                    case GumpEntryKind.Page:
                        if (p.Count >= 2)
                        {
                            page = int.Parse(p[1]);
                        }

                        break;

                    case GumpEntryKind.ResizePic:
                        gump.Add(new ResizePic(p), page);

                        break;

                    case GumpEntryKind.Text:
                        if (p.Count >= 5)
                        {
                            gump.Add(new Label(p, lines), page);
                        }

                        break;

                    case GumpEntryKind.TextEntryLimited:
                    case GumpEntryKind.TextEntry:
                        var textBox = new StbTextBox(p, lines);

                        if (!textBoxFocused && !shadow)
                        {
                            textBox.SetKeyboardFocus();
                            textBoxFocused = true;
                        }

                        gump.Add(textBox, page);

                        break;

                    case GumpEntryKind.TilePicHue:
                    case GumpEntryKind.TilePic:
                        gump.Add(new StaticPic(p), page);

                        break;

                    case GumpEntryKind.NoClose:
                        gump.CanCloseWithRightClick = false;

                        break;

                    case GumpEntryKind.NoDispose:
                        gump.CanCloseWithEsc = false;

                        break;

                    case GumpEntryKind.NoMove:
                        gump.CanMove = false;

                        break;

                    case GumpEntryKind.Group:
                    case GumpEntryKind.EndGroup:
                        group++;

                        break;

                    case GumpEntryKind.Radio:
                        gump.Add(new RadioButton(group, p, lines), page);

                        break;

                    case GumpEntryKind.Checkbox:
                        gump.Add(new Checkbox(p, lines), page);

                        break;

                    case GumpEntryKind.Tooltip:
                    {
                        string text;
                        int cliloc = int.Parse(p[1]);

                        // the arguments joined by tabs; the old check for empty arguments could never be true (p[2] is not empty)
                        text = p.Count > 2 && p[2].Length != 0
                            ? Client.Game.UO.FileManager.Clilocs.Translate(cliloc, JoinArgs(p, 2, false), false)
                            : Client.Game.UO.FileManager.Clilocs.GetString(cliloc);

                        Control last = gump.Children.Count != 0 ? gump.Children[gump.Children.Count - 1] : null;

                        if (last != null)
                        {
                            if (last.HasTooltip)
                            {
                                if (last.Tooltip is string s)
                                {
                                    s += '\n' + text;
                                    last.SetTooltip(s);
                                }
                            }
                            else
                            {
                                last.SetTooltip(text);
                            }

                            last.Priority = ClickPriority.High;
                            last.AcceptMouseInput = true;
                        }

                        break;
                    }

                    case GumpEntryKind.ItemProperty:
                        if (world.ClientFeatures.TooltipsEnabled && gump.Children.Count != 0)
                        {
                            gump.Children[gump.Children.Count - 1].SetTooltip(SerialHelper.Parse(p[1]));

                            if (
                                !shadow
                                && uint.TryParse(p[1], out uint s)
                                && (!world.OPL.TryGetRevision(s, out uint rev) || rev == 0)
                            )
                            {
                                PacketHandlers.AddMegaClilocRequest(s, OplRequestKind.Gump, 0);
                            }
                        }

                        break;

                    case GumpEntryKind.NoResize:
                        break;

                    case GumpEntryKind.MasterGump:
                        gump.MasterGumpSerial = SerialHelper.Parse(p[1]);

                        break;

                    case GumpEntryKind.PicInPicHued:
                    case GumpEntryKind.PicInPicPHued:
                    case GumpEntryKind.PicInPic:
                        if (p.Count > 7)
                        {
                            var g = gump.Add(new GumpPicInPic(p), page);

                            if (p.Count > 8)
                            {
                                g.Hue = UInt16Converter.Parse(p[8]);

                                if (e.Kind == GumpEntryKind.PicInPicPHued)
                                {
                                    g.IsPartialHue = true;
                                }
                            }
                        }

                        break;

                    case GumpEntryKind.Terminator:
                        // this gump is null terminated
                        return;

                    case GumpEntryKind.GumpPicHued:
                    case GumpEntryKind.GumpPicPHued:
                        // added without a page, as the old loop
                        if (p.Count >= 3)
                        {
                            gump.Add(new GumpPic(p));
                        }

                        break;

                    case GumpEntryKind.ToggleLimitGumpScale:
                        break;

                    default:
                        Log.Warn($"Invalid Gump Command: \"{p.GetString(0)}\"");

                        break;
                }
            }
        }

        /// <summary>A cliloc number with any '#' removed (the old <c>int.Parse(s.Replace("#", ""))</c>), without a string.</summary>
        internal static int ParseCliloc(ReadOnlySpan<char> s)
        {
            if (s.IndexOf('#') < 0)
            {
                return int.Parse(s);
            }

            Span<char> buf = s.Length <= 64 ? stackalloc char[s.Length] : new char[s.Length];
            int n = 0;

            foreach (char c in s)
            {
                if (c != '#')
                {
                    buf[n++] = c;
                }
            }

            return int.Parse(buf.Slice(0, n));
        }

        /// <summary>
        /// Arguments <paramref name="from"/>.. joined by tabs, as the old loop built them: xmfhtmltok puts a tab before each
        /// (<paramref name="leadingTab"/>; its Trim('@')/Replace('@') were no-ops, tokens hold no '@'), tooltip between them.
        /// </summary>
        internal static string JoinArgs(GumpArgs p, int from, bool leadingTab)
        {
            int len = 0;

            for (int i = from; i < p.Count; i++)
            {
                len += p[i].Length + 1;
            }

            if (!leadingTab && len > 0)
            {
                len--;
            }

            if (len == 0)
            {
                return string.Empty;
            }

            Span<char> buf = len <= 512 ? stackalloc char[len] : new char[len];
            int n = 0;

            for (int i = from; i < p.Count; i++)
            {
                if (leadingTab || i > from)
                {
                    buf[n++] = '\t';
                }

                ReadOnlySpan<char> t = p[i];
                t.CopyTo(buf.Slice(n));
                n += t.Length;
            }

            return new string(buf.Slice(0, n));
        }
    }
}
