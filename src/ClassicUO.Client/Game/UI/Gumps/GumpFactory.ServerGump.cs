// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.IO;
using ClassicUO.Network;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using ClassicUO.Utility.Platforms;
using Microsoft.Xna.Framework;
using System;
using System.IO;
using System.Collections.Generic;
using System.Text;

namespace ClassicUO.Game.UI.Gumps
{
    // The server gump (0xB0 / 0xDD): the layout parser, moved from PacketHandlers.Gumps.cs.
    internal static partial class GumpFactory
    {
        private static readonly TextFileParser _parser = new TextFileParser(
            string.Empty,
            new[] { ' ' },
            new char[] { },
            new[] { '{', '}' }
        );
        private static readonly TextFileParser _cmdparser = new TextFileParser(
            string.Empty,
            new[] { ' ', ',' },
            new char[] { },
            new[] { '@', '@' }
        );

        private static readonly List<uint> _layoutGumps = new List<uint>();

        /// <summary>
        /// The gump pictures a server gump's layout names (gumppic, button, resizepic's nine pieces, gumppictiled,
        /// checkbox, radio), decoded on workers in one batch before the controls are built (Renderer.Gumps.Gump.Prefetch;
        /// pack only). The controls then find them in the atlas; what the batch misses decodes on the spot as before.
        /// </summary>
        internal static void PrefetchLayoutGumps(List<string>[] commands)
        {
            if (!Renderer.Gumps.Gump.AsyncRequests || !Client.Game.UO.FileManager.Gumps.CanDecodeOnWorker)
            {
                return;
            }

            _layoutGumps.Clear();

            for (int cnt = 0; cnt < commands.Length; cnt++)
            {
                List<string> p = commands[cnt];

                if (p.Count == 0)
                {
                    continue;
                }

                string entry = p[0];

                if (string.Equals(entry, "gumppic", StringComparison.InvariantCultureIgnoreCase))
                {
                    AddLayoutGump(p, 3);
                }
                else if (string.Equals(entry, "button", StringComparison.InvariantCultureIgnoreCase)
                    || string.Equals(entry, "checkbox", StringComparison.InvariantCultureIgnoreCase)
                    || string.Equals(entry, "radio", StringComparison.InvariantCultureIgnoreCase))
                {
                    AddLayoutGump(p, 3);
                    AddLayoutGump(p, 4);
                }
                else if (string.Equals(entry, "gumppictiled", StringComparison.InvariantCultureIgnoreCase))
                {
                    AddLayoutGump(p, 5);
                }
                else if (string.Equals(entry, "resizepic", StringComparison.InvariantCultureIgnoreCase)
                    && p.Count > 3)
                {
                    ushort first = UInt16Converter.Parse(p[3]);

                    for (int i = 0; i < 9; i++)
                    {
                        _layoutGumps.Add((uint)(first + i));
                    }
                }
            }

            if (_layoutGumps.Count >= 2)
            {
                Client.Game.UO.Gumps.Prefetch(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_layoutGumps));
            }
        }

        private static void AddLayoutGump(List<string> p, int index)
        {
            if (p.Count > index)
            {
                _layoutGumps.Add(UInt16Converter.Parse(p[index]));
            }
        }

        /// <summary>0xB0 / 0xDD: the server gump of <paramref name="layout"/> and <paramref name="lines"/>; the gump already open with the same sender and id is rebuilt in place.</summary>
        internal static Gump OpenServerGump(
            World world,
            uint sender,
            uint gumpID,
            int x,
            int y,
            string layout,
            string[] lines
        )
        {
            if (GumpLayoutSwitch.Enabled)
            {
                return OpenServerGumpV2(world, sender, gumpID, x, y, layout, lines);
            }

            List<string> cmdlist = _parser.GetTokens(layout);
            int cmdlen = cmdlist.Count;

            if (cmdlen <= 0)
            {
                return null;
            }

            Gump gump = FindOrCreateServerGump(world, sender, gumpID, x, y, out bool mustBeAdded);

            // the layout is tokenized once: the prefetch scan and the control loop below share the tokens
            List<string>[] commands = TokenizeEntries(cmdlist);

            PrefetchLayoutGumps(commands);
            BuildControls(world, gump, commands, lines, false);

            return FinishServerGump(gump, mustBeAdded);
        }

        /// <summary>CUO_GUMP_LAYOUT_CHECK: the old tokenizer's entries of <paramref name="layout"/>.</summary>
        internal static List<string>[] TokenizeForCheck(string layout) => TokenizeEntries(_parser.GetTokens(layout));

        private static List<string>[] TokenizeEntries(List<string> cmdlist)
        {
            var commands = new List<string>[cmdlist.Count];

            for (int cnt = 0; cnt < commands.Length; cnt++)
            {
                commands[cnt] = _cmdparser.GetTokens(cmdlist[cnt], false);
            }

            return commands;
        }

        /// <summary>
        /// The open gump with this sender and id, cleared for a rebuild in place, else a new one at the cached position (the
        /// packet's position is cached on a first open). The open gump is found also when its position-cache entry was removed
        /// (a closed gump with the same id), so a resend never opens a duplicate.
        /// </summary>
        private static Gump FindOrCreateServerGump(World world, uint sender, uint gumpID, int x, int y, out bool mustBeAdded)
        {
            bool cached = UIManager.GetGumpCachePosition(gumpID, out Point pos);

            if (cached)
            {
                x = pos.X;
                y = pos.Y;
            }

            for (LinkedListNode<Gump> last = UIManager.Gumps.Last; last != null; last = last.Previous)
            {
                Gump g = last.Value;

                if (!g.IsDisposed && g.LocalSerial == sender && g.ServerSerial == gumpID)
                {
                    if (!cached)
                    {
                        UIManager.SavePosition(gumpID, g.Location);
                    }

                    g.Clear();
                    // the flags the layout sets start from a new gump's values again (a rebuild in place equals a fresh build)
                    g.CanMove = true;
                    g.CanCloseWithRightClick = true;
                    g.CanCloseWithEsc = true;
                    g.MasterGumpSerial = 0;
                    mustBeAdded = false;

                    return g;
                }
            }

            if (!cached)
            {
                UIManager.SavePosition(gumpID, new Point(x, y));
            }

            mustBeAdded = true;

            return NewServerGump(world, sender, gumpID, x, y);
        }

        private static Gump NewServerGump(World world, uint sender, uint gumpID, int x, int y) =>
            new Gump(world, sender, gumpID)
            {
                X = x,
                Y = y,
                CanMove = true,
                CanCloseWithRightClick = true,
                CanCloseWithEsc = true,
                InvalidateContents = false,
                IsFromServer = true
            };

        private static Gump FinishServerGump(Gump gump, bool mustBeAdded)
        {
            if (mustBeAdded)
            {
                UIManager.Add(gump);
            }

            gump.Update();
            gump.SetInScreen();

            return gump;
        }

        /// <summary>
        /// The old control loop over the old tokenizer's entries. <paramref name="shadow"/>: a checker's copy
        /// (CUO_GUMP_LAYOUT_CHECK) that takes no keyboard focus and sends no requests.
        /// </summary>
        internal static void BuildControls(World world, Gump gump, List<string>[] commands, string[] lines, bool shadow)
        {
            int cmdlen = commands.Length;
            int group = 0;
            int page = 0;

            bool textBoxFocused = false;

            for (int cnt = 0; cnt < cmdlen; cnt++)
            {
                List<string> gparams = commands[cnt];

                if (gparams.Count == 0)
                {
                    continue;
                }

                string entry = gparams[0];

                if (string.Equals(entry, "button", StringComparison.InvariantCultureIgnoreCase))
                {
                    gump.Add(new Button(gparams), page);
                }
                else if (
                    string.Equals(
                        entry,
                        "buttontileart",
                        StringComparison.InvariantCultureIgnoreCase
                    )
                )
                {
                    gump.Add(new ButtonTileArt(gparams), page);
                }
                else if (
                    string.Equals(
                        entry,
                        "checkertrans",
                        StringComparison.InvariantCultureIgnoreCase
                    )
                )
                {
                    var checkerTrans = new CheckerTrans(gparams);
                    gump.Add(checkerTrans, page);
                    ApplyTrans(
                        gump,
                        page,
                        checkerTrans.X,
                        checkerTrans.Y,
                        checkerTrans.Width,
                        checkerTrans.Height
                    );
                }
                else if (
                    string.Equals(entry, "croppedtext", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    gump.Add(new CroppedText(gparams, lines), page);
                }
                else if (
                    string.Equals(entry, "tilepicasgumppic", StringComparison.InvariantCultureIgnoreCase) ||
                    string.Equals(entry, "gumppic", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    GumpPic pic;
                    var isVirtue = gparams.Count >= 6
                        && gparams[5].IndexOf(
                            "virtuegumpitem",
                            StringComparison.InvariantCultureIgnoreCase
                        ) >= 0;

                    if (isVirtue)
                    {
                        pic = new VirtueGumpPic(world, gparams);
                        pic.ContainsByBounds = true;

                        SetVirtueTooltip(pic);
                    }
                    else
                    {
                        pic = new GumpPic(gparams);
                    }

                    gump.Add(pic, page);
                }
                else if (
                    string.Equals(
                        entry,
                        "gumppictiled",
                        StringComparison.InvariantCultureIgnoreCase
                    )
                )
                {
                    gump.Add(new GumpPicTiled(gparams), page);
                }
                else if (
                    string.Equals(entry, "htmlgump", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    gump.Add(new HtmlControl(gparams, lines), page);
                }
                else if (
                    string.Equals(entry, "xmfhtmlgump", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    gump.Add(
                        new HtmlControl(
                            int.Parse(gparams[1]),
                            int.Parse(gparams[2]),
                            int.Parse(gparams[3]),
                            int.Parse(gparams[4]),
                            int.Parse(gparams[6]) == 1,
                            int.Parse(gparams[7]) != 0,
                            gparams[6] != "0" && gparams[7] == "2",
                            Client.Game.UO.FileManager.Clilocs.GetString(int.Parse(gparams[5].Replace("#", ""))),
                            0,
                            true
                        )
                        {
                            IsFromServer = true
                        },
                        page
                    );
                }
                else if (
                    string.Equals(
                        entry,
                        "xmfhtmlgumpcolor",
                        StringComparison.InvariantCultureIgnoreCase
                    )
                )
                {
                    int color = int.Parse(gparams[8]);

                    if (color == 0x7FFF)
                    {
                        color = 0x00FFFFFF;
                    }

                    gump.Add(
                        new HtmlControl(
                            int.Parse(gparams[1]),
                            int.Parse(gparams[2]),
                            int.Parse(gparams[3]),
                            int.Parse(gparams[4]),
                            int.Parse(gparams[6]) == 1,
                            int.Parse(gparams[7]) != 0,
                            gparams[6] != "0" && gparams[7] == "2",
                            Client.Game.UO.FileManager.Clilocs.GetString(int.Parse(gparams[5].Replace("#", ""))),
                            color,
                            true
                        )
                        {
                            IsFromServer = true
                        },
                        page
                    );
                }
                else if (
                    string.Equals(entry, "xmfhtmltok", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    int color = int.Parse(gparams[7]);

                    if (color == 0x7FFF)
                    {
                        color = 0x00FFFFFF;
                    }

                    StringBuilder sb = null;

                    if (gparams.Count >= 9)
                    {
                        sb = new StringBuilder();

                        for (int i = 9; i < gparams.Count; i++)
                        {
                            sb.Append('\t');
                            sb.Append(gparams[i]);
                        }
                    }

                    gump.Add(
                        new HtmlControl(
                            int.Parse(gparams[1]),
                            int.Parse(gparams[2]),
                            int.Parse(gparams[3]),
                            int.Parse(gparams[4]),
                            int.Parse(gparams[5]) == 1,
                            int.Parse(gparams[6]) != 0,
                            gparams[5] != "0" && gparams[6] == "2",
                            sb == null
                                ? Client.Game.UO.FileManager.Clilocs.GetString(
                                    int.Parse(gparams[8].Replace("#", ""))
                                )
                                : Client.Game.UO.FileManager.Clilocs.Translate(
                                    int.Parse(gparams[8].Replace("#", "")),
                                    sb.ToString().Trim('@').Replace('@', '\t')
                                ),
                            color,
                            true
                        )
                        {
                            IsFromServer = true
                        },
                        page
                    );
                }
                else if (string.Equals(entry, "page", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (gparams.Count >= 2)
                    {
                        page = int.Parse(gparams[1]);
                    }
                }
                else if (
                    string.Equals(entry, "resizepic", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    gump.Add(new ResizePic(gparams), page);
                }
                else if (string.Equals(entry, "text", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (gparams.Count >= 5)
                    {
                        gump.Add(new Label(gparams, lines), page);
                    }
                }
                else if (
                    string.Equals(
                        entry,
                        "textentrylimited",
                        StringComparison.InvariantCultureIgnoreCase
                    )
                    || string.Equals(
                        entry,
                        "textentry",
                        StringComparison.InvariantCultureIgnoreCase
                    )
                )
                {
                    StbTextBox textBox = new StbTextBox(gparams, lines);

                    if (!textBoxFocused && !shadow)
                    {
                        textBox.SetKeyboardFocus();
                        textBoxFocused = true;
                    }

                    gump.Add(textBox, page);
                }
                else if (
                    string.Equals(entry, "tilepichue", StringComparison.InvariantCultureIgnoreCase) ||
                    string.Equals(entry, "tilepic", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    gump.Add(new StaticPic(gparams), page);
                }
                else if (
                    string.Equals(entry, "noclose", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    gump.CanCloseWithRightClick = false;
                }
                else if (
                    string.Equals(entry, "nodispose", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    gump.CanCloseWithEsc = false;
                }
                else if (
                    string.Equals(entry, "nomove", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    gump.CanMove = false;
                }
                else if (
                    string.Equals(entry, "group", StringComparison.InvariantCultureIgnoreCase)
                    || string.Equals(entry, "endgroup", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    group++;
                }
                else if (string.Equals(entry, "radio", StringComparison.InvariantCultureIgnoreCase))
                {
                    gump.Add(new RadioButton(group, gparams, lines), page);
                }
                else if (
                    string.Equals(entry, "checkbox", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    gump.Add(new Checkbox(gparams, lines), page);
                }
                else if (
                    string.Equals(entry, "tooltip", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    string text = null;

                    if (gparams.Count > 2 && gparams[2].Length != 0)
                    {
                        string args = gparams[2];

                        for (int i = 3; i < gparams.Count; i++)
                        {
                            args += '\t' + gparams[i];
                        }

                        if (args.Length == 0)
                        {
                            text = Client.Game.UO.FileManager.Clilocs.GetString(int.Parse(gparams[1]));
                            Log.Error(
                                $"String '{args}' too short, something wrong with gump tooltip: {text}"
                            );
                        }
                        else
                        {
                            text = Client.Game.UO.FileManager.Clilocs.Translate(
                                int.Parse(gparams[1]),
                                args,
                                false
                            );
                        }
                    }
                    else
                    {
                        text = Client.Game.UO.FileManager.Clilocs.GetString(int.Parse(gparams[1]));
                    }

                    Control last =
                        gump.Children.Count != 0 ? gump.Children[gump.Children.Count - 1] : null;

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
                }
                else if (
                    string.Equals(
                        entry,
                        "itemproperty",
                        StringComparison.InvariantCultureIgnoreCase
                    )
                )
                {
                    if (world.ClientFeatures.TooltipsEnabled && gump.Children.Count != 0)
                    {
                        gump.Children[gump.Children.Count - 1].SetTooltip(
                            SerialHelper.Parse(gparams[1])
                        );

                        if (
                            !shadow
                            && uint.TryParse(gparams[1], out uint s)
                            && (!world.OPL.TryGetRevision(s, out uint rev) || rev == 0)
                        )
                        {
                            PacketHandlers.AddMegaClilocRequest(s, OplRequestKind.Gump, 0);
                        }
                    }
                }
                else if (
                    string.Equals(entry, "noresize", StringComparison.InvariantCultureIgnoreCase)
                ) { }
                else if (
                    string.Equals(entry, "mastergump", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    gump.MasterGumpSerial = gparams.Count > 0 ? SerialHelper.Parse(gparams[1]) : 0;
                }
                else if (string.Equals(entry, "picinpichued", StringComparison.InvariantCultureIgnoreCase) ||
                    string.Equals(entry, "picinpicphued", StringComparison.InvariantCultureIgnoreCase) ||
                    string.Equals(entry, "picinpic", StringComparison.InvariantCultureIgnoreCase)
                )
                {
                    if (gparams.Count > 7)
                    {
                        var g = gump.Add(new GumpPicInPic(gparams), page);

                        if (gparams.Count > 8)
                        {
                            g.Hue = UInt16Converter.Parse(gparams[8]);

                            if (string.Equals(entry, "picinpicphued", StringComparison.InvariantCultureIgnoreCase))
                            {
                                g.IsPartialHue = true;
                            }
                        }
                    }
                }
                else if (string.Equals(entry, "\0", StringComparison.InvariantCultureIgnoreCase))
                {
                    //This gump is null terminated: Breaking
                    break;
                }
                else if (string.Equals(entry, "gumppichued", StringComparison.InvariantCultureIgnoreCase) ||
                         string.Equals(entry, "gumppicphued", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (gparams.Count >= 3)
                        gump.Add(new GumpPic(gparams));
                }
                else if (string.Equals(entry, "togglelimitgumpscale", StringComparison.InvariantCultureIgnoreCase))
                {
                    // ??
                }
                else
                {
                    Log.Warn($"Invalid Gump Command: \"{gparams[0]}\"");
                }
            }
        }

        private static void SetVirtueTooltip(GumpPic pic)
        {
            string s,
                lvl;

            switch (pic.Hue)
            {
                case 2403:
                    lvl = "";

                    break;

                case 1154:
                case 1547:
                case 2213:
                case 235:
                case 18:
                case 2210:
                case 1348:
                    lvl = "Seeker of ";

                    break;

                case 2404:
                case 1552:
                case 2216:
                case 2302:
                case 2118:
                case 618:
                case 2212:
                case 1352:
                    lvl = "Follower of ";

                    break;

                case 43:
                case 53:
                case 1153:
                case 33:
                case 318:
                case 67:
                case 98:
                    lvl = "Knight of ";

                    break;

                case 2406:
                    if (pic.Graphic == 0x6F)
                    {
                        lvl = "Seeker of ";
                    }
                    else
                    {
                        lvl = "Knight of ";
                    }

                    break;

                default:
                    lvl = "";

                    break;
            }

            switch (pic.Graphic)
            {
                case 0x69:
                    s = Client.Game.UO.FileManager.Clilocs.GetString(1051000 + 2);

                    break;

                case 0x6A:
                    s = Client.Game.UO.FileManager.Clilocs.GetString(1051000 + 7);

                    break;

                case 0x6B:
                    s = Client.Game.UO.FileManager.Clilocs.GetString(1051000 + 5);

                    break;

                case 0x6D:
                    s = Client.Game.UO.FileManager.Clilocs.GetString(1051000 + 6);

                    break;

                case 0x6E:
                    s = Client.Game.UO.FileManager.Clilocs.GetString(1051000 + 1);

                    break;

                case 0x6F:
                    s = Client.Game.UO.FileManager.Clilocs.GetString(1051000 + 3);

                    break;

                case 0x70:
                    s = Client.Game.UO.FileManager.Clilocs.GetString(1051000 + 4);

                    break;

                case 0x6C:
                default:
                    s = Client.Game.UO.FileManager.Clilocs.GetString(1051000);

                    break;
            }

            if (string.IsNullOrEmpty(s))
            {
                s = "Unknown virtue";
            }

            pic.SetTooltip(lvl + s, 100);
        }

        private static void ApplyTrans(
            Gump gump,
            int current_page,
            int x,
            int y,
            int width,
            int height
        )
        {
            int x2 = x + width;
            int y2 = y + height;
            for (int i = 0; i < gump.Children.Count; i++)
            {
                Control child = gump.Children[i];
                bool canDraw = child.Page == 0 || current_page == child.Page;

                bool overlap =
                    (x < child.X + child.Width)
                    && (child.X < x2)
                    && (y < child.Y + child.Height)
                    && (child.Y < y2);

                if (canDraw && child.IsVisible && overlap)
                {
                    child.Alpha = 0.5f;
                }
            }
        }
    }
}
