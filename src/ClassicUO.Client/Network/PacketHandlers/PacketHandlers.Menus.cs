// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;
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

namespace ClassicUO.Network
{
    sealed partial class PacketHandlers
    {
        private static void BookData(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            uint serial = p.ReadUInt32();
            ushort pageCnt = p.ReadUInt16();

            ModernBookGump gump = UIManager.GetGump<ModernBookGump>(serial);

            if (gump == null || gump.IsDisposed)
            {
                return;
            }

            for (int i = 0; i < pageCnt; i++)
            {
                int pageNum = p.ReadUInt16() - 1;
                gump.KnownPages.Add(pageNum);

                if (pageNum < gump.BookPageCount && pageNum >= 0)
                {
                    ushort lineCnt = p.ReadUInt16();

                    for (int line = 0; line < lineCnt; line++)
                    {
                        int index = pageNum * ModernBookGump.MAX_BOOK_LINES + line;

                        if (index < gump.BookLines.Length)
                        {
                            gump.BookLines[index] = ModernBookGump.IsNewBook
                                ? p.ReadUTF8Safe()
                                : p.ReadCp1252();
                        }
                        else
                        {
                            Log.Error(
                                "BOOKGUMP: The server is sending a page number GREATER than the allowed number of pages in BOOK!"
                            );
                        }
                    }

                    if (lineCnt < ModernBookGump.MAX_BOOK_LINES)
                    {
                        for (int line = lineCnt; line < ModernBookGump.MAX_BOOK_LINES; line++)
                        {
                            gump.BookLines[pageNum * ModernBookGump.MAX_BOOK_LINES + line] =
                                string.Empty;
                        }
                    }
                }
                else
                {
                    Log.Error(
                        "BOOKGUMP: The server is sending a page number GREATER than the allowed number of pages in BOOK!"
                    );
                }
            }

            gump.ServerSetBookText();
        }

        private static void BulletinBoardData(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            switch (p.ReadByte())
            {
                case 0: // open

                    {
                        uint serial = p.ReadUInt32();
                        Item item = world.FindItem(serial);

                        if (item != null)
                        {
                            BulletinBoardGump bulletinBoard = UIManager.GetGump<BulletinBoardGump>(
                                serial
                            );
                            bulletinBoard?.Dispose();

                            int x = (Client.Game.ClientBounds.Width >> 1) - 245;
                            int y = (Client.Game.ClientBounds.Height >> 1) - 205;

                            bulletinBoard = new BulletinBoardGump(world, item, x, y, p.ReadUTF8Safe(22)); //p.ReadCp1252(22));
                            UIManager.Add(bulletinBoard);

                            item.Opened = true;
                        }
                    }

                    break;

                case 1: // summary msg

                    {
                        uint boardSerial = p.ReadUInt32();
                        BulletinBoardGump bulletinBoard = UIManager.GetGump<BulletinBoardGump>(
                            boardSerial
                        );

                        if (bulletinBoard != null)
                        {
                            uint serial = p.ReadUInt32();
                            uint parendID = p.ReadUInt32();

                            // poster
                            int len = p.ReadByte();
                            string text = (len <= 0 ? string.Empty : p.ReadUTF8Safe(len)) + " - ";

                            // subject
                            len = p.ReadByte();
                            text += (len <= 0 ? string.Empty : p.ReadUTF8Safe(len)) + " - ";

                            // datetime
                            len = p.ReadByte();
                            text += (len <= 0 ? string.Empty : p.ReadUTF8Safe(len));

                            bulletinBoard.AddBulletinObject(serial, text);
                        }
                    }

                    break;

                case 2: // message

                    {
                        uint boardSerial = p.ReadUInt32();
                        BulletinBoardGump bulletinBoard = UIManager.GetGump<BulletinBoardGump>(
                            boardSerial
                        );

                        if (bulletinBoard != null)
                        {
                            uint serial = p.ReadUInt32();

                            int len = p.ReadByte();
                            string poster = len > 0 ? p.ReadCp1252(len) : string.Empty;

                            len = p.ReadByte();
                            string subject = len > 0 ? p.ReadUTF8Safe(len) : string.Empty;

                            len = p.ReadByte();
                            string dataTime = len > 0 ? p.ReadCp1252(len) : string.Empty;

                            p.Skip(4);

                            byte unk = p.ReadByte();

                            if (unk > 0)
                            {
                                p.Skip(unk * 4);
                            }

                            byte lines = p.ReadByte();

                            Span<char> span = stackalloc char[256];
                            ValueStringBuilder sb = new ValueStringBuilder(span);

                            for (int i = 0; i < lines; i++)
                            {
                                byte lineLen = p.ReadByte();

                                if (lineLen > 0)
                                {
                                    string putta = p.ReadUTF8Safe(lineLen);
                                    sb.Append(putta);
                                    sb.Append('\n');
                                }
                            }

                            string msg = sb.ToString();
                            byte variant = (byte)(1 + (poster == world.Player.Name ? 1 : 0));

                            UIManager.Add(
                                new BulletinBoardItem(
                                    world,
                                    boardSerial,
                                    serial,
                                    poster,
                                    subject,
                                    dataTime,
                                    msg.TrimStart(),
                                    variant
                                )
                                {
                                    X = 40,
                                    Y = 40
                                }
                            );

                            sb.Dispose();
                        }
                    }

                    break;
            }
        }

        private static void OpenMenu(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            int layout = PacketLayouts.Resolve(ref p, PacketLayouts.Menu(p.Buffer), PacketLayouts.MenuItems);
            uint serial = p.ReadUInt32();
            ushort id = p.ReadUInt16();
            string name = p.ReadCp1252(p.ReadByte());
            int count = p.ReadByte();

            // the first item's graphic, peeked; a menu with no items has none
            ushort menuid = 0;

            if (layout == PacketLayouts.MenuItems && p.Remaining >= 2)
            {
                menuid = p.ReadUInt16();
                p.Seek(p.Position - 2, SeekOrigin.Begin);
            }

            if (menuid != 0)
            {
                MenuGump gump = new MenuGump(world, serial, id, name) { X = 100, Y = 100 };

                int posX = 0;

                for (int i = 0; i < count; i++)
                {
                    ushort graphic = p.ReadUInt16();
                    ushort hue = p.ReadUInt16();
                    name = p.ReadCp1252(p.ReadByte());

                    ref readonly var artInfo = ref Client.Game.UO.Arts.GetArt(graphic);

                    if (artInfo.UV.Width != 0 && artInfo.UV.Height != 0)
                    {
                        int posY = artInfo.UV.Height;

                        if (posY >= 47)
                        {
                            posY = 0;
                        }
                        else
                        {
                            posY = (47 - posY) >> 1;
                        }

                        gump.AddItem(graphic, hue, name, posX, posY, i + 1);

                        posX += artInfo.UV.Width;
                    }
                }

                UIManager.Add(gump);
            }
            else
            {
                GrayMenuGump gump = new GrayMenuGump(world, serial, id, name)
                {
                    X = (Client.Game.ClientBounds.Width >> 1) - 200,
                    Y = (Client.Game.ClientBounds.Height >> 1) - ((121 + count * 21) >> 1)
                };

                int offsetY = 35 + gump.Height;
                int gumpHeight = 70 + offsetY;

                for (int i = 0; i < count; i++)
                {
                    p.Skip(4);
                    name = p.ReadCp1252(p.ReadByte());

                    int addHeight = gump.AddItem(name, offsetY);

                    if (addHeight < 21)
                    {
                        addHeight = 21;
                    }

                    offsetY += addHeight - 1;
                    gumpHeight += addHeight;
                }

                offsetY += 5;

                gump.Add(
                    new Button(0, 0x1450, 0x1451, 0x1450)
                    {
                        ButtonAction = ButtonAction.Activate,
                        X = 70,
                        Y = offsetY
                    }
                );

                gump.Add(
                    new Button(1, 0x13B2, 0x13B3, 0x13B2)
                    {
                        ButtonAction = ButtonAction.Activate,
                        X = 200,
                        Y = offsetY
                    }
                );

                gump.SetHeight(gumpHeight);
                gump.WantUpdateSize = false;
                UIManager.Add(gump);
            }
        }

        private static void OpenPaperdoll(World world, ref NetReader p)
        {
            Mobile mobile = world.FindMobile(p.ReadUInt32());

            if (mobile == null)
            {
                return;
            }

            string text = p.ReadCp1252(60);
            byte flags = p.ReadByte();

            mobile.Title = text;

            PaperDollGump paperdoll = UIManager.GetGump<PaperDollGump>(mobile);

            if (paperdoll == null)
            {
                if (!UIManager.GetGumpCachePosition(mobile, out Point location))
                {
                    location = new Point(100, 100);
                }

                UIManager.Add(
                    new PaperDollGump(world, mobile, (flags & 0x02) != 0) { Location = location }
                );
            }
            else
            {
                bool old = paperdoll.CanLift;
                bool newLift = (flags & 0x02) != 0;

                paperdoll.CanLift = newLift;
                paperdoll.UpdateTitle(text);

                if (old != newLift)
                {
                    paperdoll.RequestUpdateContents();
                }

                paperdoll.SetInScreen();
                paperdoll.BringOnTop();
            }
        }

        private static void DisplayMap(World world, ref NetReader p)
        {
            uint serial = p.ReadUInt32();
            ushort gumpid = p.ReadUInt16();
            ushort startX = p.ReadUInt16();
            ushort startY = p.ReadUInt16();
            ushort endX = p.ReadUInt16();
            ushort endY = p.ReadUInt16();
            ushort width = p.ReadUInt16();
            ushort height = p.ReadUInt16();

            MapGump gump = new MapGump(world, serial, gumpid, width, height);
            SpriteInfo multiMapInfo;

            if (p[0] == 0xF5 || Client.Game.UO.Version >= Utility.ClientVersion.CV_308Z)
            {
                ushort facet = 0;

                if (p[0] == 0xF5)
                {
                    facet = p.ReadUInt16();
                }

                multiMapInfo = Client.Game.UO.MultiMaps.GetMap(facet, width, height, startX, startY, endX, endY);
            }
            else
            {
                multiMapInfo = Client.Game.UO.MultiMaps.GetMap(null, width, height, startX, startY, endX, endY);
            }

            if (multiMapInfo.Texture != null)
                gump.SetMapTexture(multiMapInfo.Texture);

            UIManager.Add(gump);

            Item it = world.FindItem(serial);

            if (it != null)
            {
                it.Opened = true;
            }
        }

        private static void OpenBook(World world, ref NetReader p)
        {
            uint serial = p.ReadUInt32();
            bool oldpacket = p[0] == 0x93;
            bool editable = p.ReadBoolean();

            if (!oldpacket)
            {
                editable = p.ReadBoolean();
            }
            else
            {
                p.Skip(1);
            }

            ModernBookGump bgump = UIManager.GetGump<ModernBookGump>(serial);

            if (bgump == null || bgump.IsDisposed)
            {
                ushort page_count = p.ReadUInt16();
                string title = oldpacket
                    ? p.ReadUTF8Safe(60)
                    : p.ReadUTF8Safe(p.ReadUInt16());
                string author = oldpacket
                    ? p.ReadUTF8Safe(30)
                    : p.ReadUTF8Safe(p.ReadUInt16());

                UIManager.Add(
                    new ModernBookGump(world, serial, page_count, title, author, editable, oldpacket)
                    {
                        X = 100,
                        Y = 100
                    }
                );

                NetClient.Socket.Send_BookPageDataRequest(serial, 1);
            }
            else
            {
                p.Skip(2);
                bgump.IsEditable = editable;
                bgump.SetTitle(
                    oldpacket ? p.ReadUTF8Safe(60) : p.ReadUTF8Safe(p.ReadUInt16()),
                    editable
                );
                bgump.SetAuthor(
                    oldpacket ? p.ReadUTF8Safe(30) : p.ReadUTF8Safe(p.ReadUInt16()),
                    editable
                );
                bgump.UseNewHeader = !oldpacket;
                bgump.SetInScreen();
                bgump.BringOnTop();
            }
        }

        private static void DyeData(World world, ref NetReader p)
        {
            uint serial = p.ReadUInt32();
            p.Skip(2);
            ushort graphic = p.ReadUInt16();

            ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(0x0906);

            int x = (Client.Game.ClientBounds.Width >> 1) - (gumpInfo.UV.Width >> 1);
            int y = (Client.Game.ClientBounds.Height >> 1) - (gumpInfo.UV.Height >> 1);

            ColorPickerGump gump = UIManager.GetGump<ColorPickerGump>(serial);

            if (gump == null || gump.IsDisposed || gump.Graphic != graphic)
            {
                gump?.Dispose();

                gump = new ColorPickerGump(world, serial, graphic, x, y, null);

                UIManager.Add(gump);
            }
        }

        private static void ASCIIPrompt(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            world.MessageManager.PromptData = new PromptData(ConsolePrompt.ASCII, p.ReadUInt64());
        }

        private static void OpenUrl(World world, ref NetReader p)
        {
            string url = p.ReadCp1252();

            if (!string.IsNullOrEmpty(url))
            {
                PlatformHelper.LaunchBrowser(url);
            }
        }

        private static void TipWindow(World world, ref NetReader p)
        {
            byte flag = p.ReadByte();

            if (flag == 1)
            {
                return;
            }

            uint tip = p.ReadUInt32();
            string str = p.ReadCp1252(p.ReadUInt16())?.Replace('\r', '\n');

            int x = 20;
            int y = 20;

            if (flag == 0)
            {
                x = 200;
                y = 100;
            }

            UIManager.Add(new TipNoticeGump(world, tip, flag, str) { X = x, Y = y });
        }

        private static void TextEntryDialog(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            uint serial = p.ReadUInt32();
            byte parentID = p.ReadByte();
            byte buttonID = p.ReadByte();

            ushort textLen = p.ReadUInt16();
            string text = p.ReadCp1252(textLen);

            bool haveCancel = p.ReadBoolean();
            byte variant = p.ReadByte();
            uint maxLength = p.ReadUInt32();

            ushort descLen = p.ReadUInt16();
            string desc = p.ReadCp1252(descLen);

            TextEntryDialogGump gump = new TextEntryDialogGump(
                world,
                serial,
                143,
                172,
                variant,
                (int)maxLength,
                text,
                desc,
                buttonID,
                parentID
            )
            {
                CanCloseWithRightClick = haveCancel
            };

            UIManager.Add(gump);
        }

        private static void CharacterProfile(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            uint serial = p.ReadUInt32();
            string header = p.ReadCp1252();
            string footer = p.ReadBigUni();

            string body = p.ReadBigUni();

            UIManager.GetGump<ProfileGump>(serial)?.Dispose();

            UIManager.Add(
                new ProfileGump(world, serial, header, footer, body, serial == world.Player.Serial)
            );
        }

        private static void DisplayQuestArrow(World world, ref NetReader p)
        {
            bool display = p.ReadBoolean();
            ushort mx = p.ReadUInt16();
            ushort my = p.ReadUInt16();

            uint serial = 0;
            int layout = PacketLayouts.Resolve(ref p, PacketLayouts.QuestArrow(p.Buffer),
                Client.Game.UO.Version >= Utility.ClientVersion.CV_7090 ? PacketLayouts.QuestArrowSerial : PacketLayouts.QuestArrowNoSerial);

            if (layout == PacketLayouts.QuestArrowSerial)
            {
                serial = p.ReadUInt32();
            }

            QuestArrowGump arrow = UIManager.GetGump<QuestArrowGump>(serial);

            if (display)
            {
                if (arrow == null)
                {
                    UIManager.Add(new QuestArrowGump(world, serial, mx, my));
                }
                else
                {
                    arrow.SetRelativePosition(mx, my);
                }
            }
            else
            {
                if (arrow != null)
                {
                    arrow.Dispose();
                }
            }
        }

        private static void UnicodePrompt(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            world.MessageManager.PromptData = new PromptData(ConsolePrompt.Unicode, p.ReadUInt64());
        }

        private static void DisplayWaypoint(World world, ref NetReader p)
        {
            uint serial = p.ReadUInt32();
            ushort x = p.ReadUInt16();
            ushort y = p.ReadUInt16();
            sbyte z = p.ReadSByte();
            byte map = p.ReadByte();
            WaypointsType type = (WaypointsType)p.ReadUInt16();
            bool ignoreobject = p.ReadUInt16() != 0;
            uint cliloc = p.ReadUInt32();
            string name = p.ReadLittleUni();
        }

        private static void RemoveWaypoint(World world, ref NetReader p)
        {
            uint serial = p.ReadUInt32();
        }
    }
}
