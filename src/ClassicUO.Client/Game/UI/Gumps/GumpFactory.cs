// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>A menu entry of 0x7C with item pictures.</summary>
    internal readonly record struct MenuItemData(ushort Graphic, ushort Hue, string Name);

    /// <summary>An item of a 0x9E sell list, its name already resolved.</summary>
    internal readonly record struct SellItemData(uint Serial, ushort Graphic, ushort Hue, ushort Amount, ushort Price, string Name, bool FromCliloc);

    /// <summary>
    /// The one place that builds the gumps a server packet opens. Each method takes the packet's parsed data (no reader),
    /// builds the gump, adds it to UIManager and returns it. Handlers keep the reads and the world effects (an item marked
    /// opened, a reply, a sound); a gump a packet updates in place is updated by its handler. Static and called once per gump
    /// opened: nothing here runs per frame.
    /// </summary>
    internal static partial class GumpFactory
    {
        // ---- 0x24

        /// <summary>0x24 with graphic 0xFFFF: the spellbook of <paramref name="book"/>, replacing the one open.</summary>
        public static SpellbookGump OpenSpellbook(World world, Item book)
        {
            UIManager.GetGump<SpellbookGump>(book.Serial)?.Dispose();

            SpellbookGump spellbookGump = new SpellbookGump(world, book);

            if (!UIManager.GetGumpCachePosition(book, out Point location))
            {
                location = new Point(64, 64);
            }

            spellbookGump.Location = location;
            UIManager.Add(spellbookGump);

            return spellbookGump;
        }

        /// <summary>0x24 with graphic 0x0030: the buy window of <paramref name="vendor"/>, listing its shop layers' contents.</summary>
        public static ShopGump OpenVendorShop(World world, Mobile vendor)
        {
            UIManager.GetGump<ShopGump>(vendor.Serial)?.Dispose();

            ShopGump gump = new ShopGump(world, vendor.Serial, true, 150, 5);
            UIManager.Add(gump);

            for (Layer layer = Layer.ShopBuyRestock; layer < Layer.ShopBuy + 1; layer++)
            {
                Item item = vendor.FindItemByLayer(layer);

                // a vendor without this shop layer lists nothing from it (a throw here would disconnect: span_reader)
                if (item == null)
                {
                    continue;
                }

                // T2: the shop's list from the ECS (reversed unless 0x2AF8: hardcoded logic in original client that we must match)
                bool backwards = item.Graphic != 0x2AF8;
                ReadOnlySpan<uint> shopContents = world.State.Contents(item.Serial);

                for (int k = 0; k < shopContents.Length; k++)
                {
                    Item it = world.FindItem(shopContents[backwards ? shopContents.Length - 1 - k : k]);

                    if (it != null)
                    {
                        gump.AddItem(it.Serial, it.Graphic, it.Hue, it.Amount, it.Price, it.Name, false);
                    }
                }
            }

            return gump;
        }

        /// <summary>
        /// 0x24 with a container gump graphic: the container window of <paramref name="item"/>, at the place of the one open
        /// (else where ContainerManager puts it, with the open sound). Large container gumps replace the classic ones when the
        /// profile asks for them and the pack has them.
        /// </summary>
        public static ContainerGump OpenContainer(World world, Item item, ushort graphic)
        {
            uint serial = item.Serial;
            ContainerGump container = UIManager.GetGump<ContainerGump>(serial);
            bool playsound = false;
            int x,
                y;

            // TODO: check client version ?
            if (
                Client.Game.UO.Version >= Utility.ClientVersion.CV_706000
                && ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.UseLargeContainerGumps
            )
            {
                graphic = LargeContainerGraphic(graphic);
            }

            if (container != null)
            {
                x = container.ScreenCoordinateX;
                y = container.ScreenCoordinateY;
                container.Dispose();
            }
            else
            {
                world.ContainerManager.CalculateContainerPosition(serial, graphic);
                x = world.ContainerManager.X;
                y = world.ContainerManager.Y;
                playsound = true;
            }

            var gump = new ContainerGump(world, item, graphic, playsound)
            {
                X = x,
                Y = y,
                InvalidateContents = true
            };

            UIManager.Add(gump);
            UIManager.RemovePosition(serial);

            return gump;
        }

        private static ushort LargeContainerGraphic(ushort graphic)
        {
            var gumps = Client.Game.UO.Gumps;

            switch (graphic)
            {
                case 0x0048:
                    if (gumps.GetGump(0x06E8).Texture != null)
                    {
                        graphic = 0x06E8;
                    }

                    break;

                case 0x0049:
                    if (gumps.GetGump(0x9CDF).Texture != null)
                    {
                        graphic = 0x9CDF;
                    }

                    break;

                case 0x0051:
                    if (gumps.GetGump(0x06E7).Texture != null)
                    {
                        graphic = 0x06E7;
                    }

                    break;

                case 0x003E:
                    if (gumps.GetGump(0x06E9).Texture != null)
                    {
                        graphic = 0x06E9;
                    }

                    break;

                case 0x004D:
                    if (gumps.GetGump(0x06EA).Texture != null)
                    {
                        graphic = 0x06EA;
                    }

                    break;

                case 0x004E:
                    if (gumps.GetGump(0x06E6).Texture != null)
                    {
                        graphic = 0x06E6;
                    }

                    break;

                case 0x004F:
                    if (gumps.GetGump(0x06E5).Texture != null)
                    {
                        graphic = 0x06E5;
                    }

                    break;

                case 0x004A:
                    if (gumps.GetGump(0x9CDD).Texture != null)
                    {
                        graphic = 0x9CDD;
                    }

                    break;

                case 0x0044:
                    if (gumps.GetGump(0x9CE3).Texture != null)
                    {
                        graphic = 0x9CE3;
                    }

                    break;
            }

            return graphic;
        }

        // ---- 0x74 / 0x9E

        /// <summary>0x74: the buy window of <paramref name="vendor"/>: the one open if it is this vendor's buy window, else a new one.</summary>
        public static ShopGump OpenBuyList(World world, Mobile vendor)
        {
            ShopGump gump = UIManager.GetGump<ShopGump>();

            if (gump != null && (gump.LocalSerial != vendor || !gump.IsBuyGump))
            {
                gump.Dispose();
                gump = null;
            }

            if (gump == null)
            {
                gump = new ShopGump(world, vendor, true, 150, 5);
                UIManager.Add(gump);
            }

            return gump;
        }

        /// <summary>0x9E: the sell window of <paramref name="vendor"/> with <paramref name="items"/>, replacing the one open.</summary>
        public static ShopGump OpenSellList(World world, Mobile vendor, ReadOnlySpan<SellItemData> items)
        {
            ShopGump gump = UIManager.GetGump<ShopGump>(vendor);
            gump?.Dispose();
            gump = new ShopGump(world, vendor, false, 100, 0);

            foreach (ref readonly SellItemData it in items)
            {
                gump.AddItem(it.Serial, it.Graphic, it.Hue, it.Amount, it.Price, it.Name, it.FromCliloc);
            }

            UIManager.Add(gump);

            return gump;
        }

        // ---- 0x7C

        /// <summary>0x7C with item pictures: a row of the items whose art exists, each centred in a 47-pixel band.</summary>
        public static MenuGump OpenMenu(World world, uint serial, ushort id, string name, ReadOnlySpan<MenuItemData> items)
        {
            MenuGump gump = new MenuGump(world, serial, id, name) { X = 100, Y = 100 };

            int posX = 0;

            for (int i = 0; i < items.Length; i++)
            {
                ushort graphic = items[i].Graphic;
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

                    gump.AddItem(graphic, items[i].Hue, items[i].Name, posX, posY, i + 1);

                    posX += artInfo.UV.Width;
                }
            }

            UIManager.Add(gump);

            return gump;
        }

        /// <summary>0x7C without item pictures: a gray menu of <paramref name="names"/> with Continue and Cancel, centred.</summary>
        public static GrayMenuGump OpenGrayMenu(World world, uint serial, ushort id, string name, ReadOnlySpan<string> names)
        {
            int count = names.Length;

            GrayMenuGump gump = new GrayMenuGump(world, serial, id, name)
            {
                X = (Client.Game.ClientBounds.Width >> 1) - 200,
                Y = (Client.Game.ClientBounds.Height >> 1) - ((121 + count * 21) >> 1)
            };

            int offsetY = 35 + gump.Height;
            int gumpHeight = 70 + offsetY;

            for (int i = 0; i < count; i++)
            {
                int addHeight = gump.AddItem(names[i], offsetY);

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

            return gump;
        }

        // ---- 0x90 / 0xF5

        /// <summary>0x90 / 0xF5: a map of the area from (startX, startY) to (endX, endY), <paramref name="facet"/> null for the old packet before 3.0.8z.</summary>
        public static MapGump OpenMap(World world, uint serial, ushort gumpid, int width, int height, int? facet, int startX, int startY, int endX, int endY)
        {
            MapGump gump = new MapGump(world, serial, gumpid, width, height);
            var multiMapInfo = Client.Game.UO.MultiMaps.GetMap(facet, width, height, startX, startY, endX, endY);

            if (multiMapInfo.Texture != null)
                gump.SetMapTexture(multiMapInfo.Texture);

            UIManager.Add(gump);

            return gump;
        }

        // ---- 0x93 / 0xD4

        /// <summary>0x93 / 0xD4: a book not open yet.</summary>
        public static ModernBookGump OpenBook(World world, uint serial, ushort pageCount, string title, string author, bool editable, bool oldPacket)
        {
            var gump = new ModernBookGump(world, serial, pageCount, title, author, editable, oldPacket)
            {
                X = 100,
                Y = 100
            };

            UIManager.Add(gump);

            return gump;
        }

        // ---- 0x95

        /// <summary>0x95: the dye picker for <paramref name="serial"/>, centred on the client; the open one stays if it is for the same graphic.</summary>
        public static ColorPickerGump OpenColorPicker(World world, uint serial, ushort graphic)
        {
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

            return gump;
        }

        // ---- 0x71

        /// <summary>0x71 open: the board of <paramref name="board"/>, centred on the client, replacing the one open.</summary>
        public static BulletinBoardGump OpenBulletinBoard(World world, Item board, string name)
        {
            UIManager.GetGump<BulletinBoardGump>(board.Serial)?.Dispose();

            int x = (Client.Game.ClientBounds.Width >> 1) - 245;
            int y = (Client.Game.ClientBounds.Height >> 1) - 205;

            var gump = new BulletinBoardGump(world, board, x, y, name);
            UIManager.Add(gump);

            return gump;
        }

        /// <summary>0x71 message: one message of board <paramref name="boardSerial"/>.</summary>
        public static BulletinBoardItem OpenBulletinMessage(World world, uint boardSerial, uint serial, string poster, string subject, string dateTime, string message, byte variant)
        {
            var gump = new BulletinBoardItem(world, boardSerial, serial, poster, subject, dateTime, message, variant)
            {
                X = 40,
                Y = 40
            };

            UIManager.Add(gump);

            return gump;
        }

        // ---- 0xB8

        /// <summary>0xB8: the profile of <paramref name="serial"/>, editable when it is the player's, replacing the one open.</summary>
        public static ProfileGump OpenProfile(World world, uint serial, string header, string footer, string body)
        {
            UIManager.GetGump<ProfileGump>(serial)?.Dispose();

            var gump = new ProfileGump(world, serial, header, footer, body, serial == world.Player.Serial);
            UIManager.Add(gump);

            return gump;
        }

        // ---- 0xBF

        /// <summary>0xBF 0x14: the context menu of <paramref name="data"/> at the last clicked point.</summary>
        public static PopupMenuGump OpenPopupMenu(World world, PopupMenuData data)
        {
            var gump = new PopupMenuGump(world, data)
            {
                X = world.DelayedObjectClickManager.LastMouseX,
                Y = world.DelayedObjectClickManager.LastMouseY
            };

            UIManager.ShowGamePopup(gump);

            return gump;
        }

        /// <summary>0xBF 0x2A: the race change window, replacing the one open.</summary>
        public static RaceChangeGump OpenRaceChange(World world, bool isFemale, byte race)
        {
            UIManager.GetGump<RaceChangeGump>()?.Dispose();

            var gump = new RaceChangeGump(world, isFemale, race);
            UIManager.Add(gump);

            return gump;
        }
    }
}
