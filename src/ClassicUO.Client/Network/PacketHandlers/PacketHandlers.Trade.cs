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
        private static void SecureTrading(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            byte type = p.ReadByte();
            uint serial = p.ReadUInt32();

            if (type == 0)
            {
                uint id1 = p.ReadUInt32();
                uint id2 = p.ReadUInt32();

                // standard client doesn't allow the trading system if one of the traders is invisible (=not sent by server)
                if (world.Get(id1) == null || world.Get(id2) == null)
                {
                    return;
                }

                bool hasName = p.ReadBoolean();
                string name = string.Empty;

                if (hasName && p.Position < p.Length)
                {
                    name = p.ReadCp1252();
                }

                UIManager.Add(new TradingGump(world, serial, name, id1, id2));
            }
            else if (type == 1)
            {
                UIManager.GetTradingGump(serial)?.Dispose();
            }
            else if (type == 2)
            {
                uint id1 = p.ReadUInt32();
                uint id2 = p.ReadUInt32();

                TradingGump trading = UIManager.GetTradingGump(serial);

                if (trading != null)
                {
                    trading.ImAccepting = id1 != 0;
                    trading.HeIsAccepting = id2 != 0;

                    trading.RequestUpdateContents();
                }
            }
            else if (type == 3 || type == 4)
            {
                TradingGump trading = UIManager.GetTradingGump(serial);

                if (trading != null)
                {
                    if (type == 4)
                    {
                        trading.Gold = p.ReadUInt32();
                        trading.Platinum = p.ReadUInt32();
                    }
                    else
                    {
                        trading.HisGold = p.ReadUInt32();
                        trading.HisPlatinum = p.ReadUInt32();
                    }
                }
            }
        }

        private static void CloseVendorInterface(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            uint serial = p.ReadUInt32();

            UIManager.GetGump<ShopGump>(serial)?.Dispose();
        }

        private static void BuyList(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            Item container = world.FindItem(p.ReadUInt32());

            if (container == null)
            {
                return;
            }

            Mobile vendor = world.FindMobile(container.Container);

            if (vendor == null)
            {
                return;
            }

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

            if (container.Layer == Layer.ShopBuyRestock || container.Layer == Layer.ShopBuy)
            {
                byte count = p.ReadByte();

                // T2: the list is the ECS's contents (after the sort, which re-stamps them)
                ReadOnlySpan<uint> shopContents = world.State.Contents(container.Serial);

                if (shopContents.IsEmpty)
                {
                    return;
                }

                bool reverse = false;

                if (container.Graphic == 0x2AF8) //hardcoded logic in original client that we must match
                {
                    //sort the contents (T1 part 9: through the funnel, which re-stamps the mirror's child order)
                    world.Writer.SortShopContents(container);
                    shopContents = world.State.Contents(container.Serial);
                }
                else
                {
                    //read in reverse
                    reverse = true;
                }

                int at = reverse ? shopContents.Length - 1 : 0;

                for (int i = 0; i < count; i++)
                {
                    Item it;

                    if (at < 0 || at >= shopContents.Length || (it = world.FindItem(shopContents[at])) == null)
                    {
                        break;
                    }

                    at += reverse ? -1 : 1;

                    world.Writer.SetPrice(it, p.ReadUInt32());
                    byte nameLen = p.ReadByte();
                    string name = p.ReadCp1252(nameLen);
                    string itName;

                    if (world.OPL.TryGetNameAndData(it.Serial, out string s, out _))
                    {
                        itName = s;
                    }
                    else if (int.TryParse(name, out int cliloc))
                    {
                        itName = Client.Game.UO.FileManager.Clilocs.Translate(
                            cliloc,
                            $"\t{it.ItemData.Name}: \t{it.Amount}",
                            true
                        );
                    }
                    else if (string.IsNullOrEmpty(name))
                    {
                        itName = it.ItemData.Name;
                    }
                    else
                    {
                        itName = name;
                    }

                    world.Writer.SetName(it, itName);
                }
            }
        }

        private static void SellList(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            Mobile vendor = world.FindMobile(p.ReadUInt32());

            if (vendor == null)
            {
                return;
            }

            ushort countItems = p.ReadUInt16();

            if (countItems <= 0)
            {
                return;
            }

            ShopGump gump = UIManager.GetGump<ShopGump>(vendor);
            gump?.Dispose();
            gump = new ShopGump(world, vendor, false, 100, 0);

            for (int i = 0; i < countItems; i++)
            {
                uint serial = p.ReadUInt32();
                ushort graphic = p.ReadUInt16();
                ushort hue = p.ReadUInt16();
                ushort amount = p.ReadUInt16();
                ushort price = p.ReadUInt16();
                string name = p.ReadCp1252(p.ReadUInt16());
                bool fromcliloc = false;

                if (int.TryParse(name, out int clilocnum))
                {
                    name = Client.Game.UO.FileManager.Clilocs.GetString(clilocnum);
                    fromcliloc = true;
                }
                else if (string.IsNullOrEmpty(name))
                {
                    bool success = world.OPL.TryGetNameAndData(serial, out name, out _);

                    if (!success)
                    {
                        name = Client.Game.UO.FileManager.TileData.StaticData[graphic].Name;
                    }
                }

                //if (string.IsNullOrEmpty(item.Name))
                //    item.Name = name;

                gump.AddItem(serial, graphic, hue, amount, price, name, fromcliloc);
            }

            UIManager.Add(gump);
        }
    }
}
