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
        private static void OpenContainer(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            uint serial = p.ReadUInt32();
            ushort graphic = p.ReadUInt16();

            if (graphic == 0xFFFF)
            {
                Item spellBookItem = world.FindItem(serial);

                if (spellBookItem == null)
                {
                    return;
                }

                GumpFactory.OpenSpellbook(world, spellBookItem);

                Client.Game.Audio.PlaySound(0x0055);
            }
            else if (graphic == 0x0030)
            {
                Mobile vendor = world.FindMobile(serial);

                if (vendor == null)
                {
                    return;
                }

                GumpFactory.OpenVendorShop(world, vendor);
            }
            else
            {
                Item item = world.FindItem(serial);

                if (item != null)
                {
                    if (
                        item.IsCorpse
                        && (
                            ProfileManager.CurrentProfile.GridLootType == 1
                            || ProfileManager.CurrentProfile.GridLootType == 2
                        )
                    )
                    {
                        //UIManager.GetGump<GridLootGump>(serial)?.Dispose();
                        //UIManager.Add(new GridLootGump(serial));
                        _requestedGridLoot = serial;

                        if (ProfileManager.CurrentProfile.GridLootType == 1)
                        {
                            return;
                        }
                    }

                    GumpFactory.OpenContainer(world, item, graphic);
                }
                else
                {
                    Log.Error("[OpenContainer]: item not found");
                }
            }

            if (graphic != 0x0030)
            {
                Item it = world.FindItem(serial);

                if (it != null)
                {
                    it.Opened = true;

                    if (!it.IsCorpse && graphic != 0xFFFF)
                    {
                        world.Writer.ClearContainer(it.Serial, false, itemOnly: true);
                    }
                }
            }
        }

        private static void UpdateContainedItem(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            int layout = PacketLayouts.Resolve(ref p, PacketLayouts.ContainedItem(p.Buffer),
                Client.Game.UO.Version >= Utility.ClientVersion.CV_6017 ? PacketLayouts.ContainedGrid : PacketLayouts.ContainedNoGrid);
            uint serial = p.ReadUInt32();
            ushort graphic = (ushort)(p.ReadUInt16() + p.ReadByte());
            ushort amount = Math.Max((ushort)1, p.ReadUInt16());
            ushort x = p.ReadUInt16();
            ushort y = p.ReadUInt16();

            if (layout == PacketLayouts.ContainedGrid)
            {
                p.Skip(1);
            }

            uint containerSerial = p.ReadUInt32();
            ushort hue = p.ReadUInt16();

            AddItemToContainer(world, serial, graphic, amount, x, y, hue, containerSerial);
        }

        private static void DenyMoveItem(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            Item firstItem = world.FindItem(Client.Game.UO.GameCursor.ItemHold.Serial);

            if (
                Client.Game.UO.GameCursor.ItemHold.Enabled
                || Client.Game.UO.GameCursor.ItemHold.Dropped
                    && (firstItem == null || !firstItem.AllowedToDraw)
            )
            {
                if (world.ObjectToRemove == Client.Game.UO.GameCursor.ItemHold.Serial)
                {
                    world.ObjectToRemove = 0;
                }

                if (
                    SerialHelper.IsValid(Client.Game.UO.GameCursor.ItemHold.Serial)
                    && Client.Game.UO.GameCursor.ItemHold.Graphic != 0xFFFF
                )
                {
                    if (!Client.Game.UO.GameCursor.ItemHold.UpdatedInWorld)
                    {
                        if (
                            Client.Game.UO.GameCursor.ItemHold.Layer == Layer.Invalid
                            && SerialHelper.IsValid(Client.Game.UO.GameCursor.ItemHold.Container)
                        )
                        {
                            // Server should send an UpdateContainedItem after this packet.
                            Log.Trace("Drop denied: held item returned to container");

                            AddItemToContainer(
                                world,
                                Client.Game.UO.GameCursor.ItemHold.Serial,
                                Client.Game.UO.GameCursor.ItemHold.Graphic,
                                Client.Game.UO.GameCursor.ItemHold.TotalAmount,
                                Client.Game.UO.GameCursor.ItemHold.X,
                                Client.Game.UO.GameCursor.ItemHold.Y,
                                Client.Game.UO.GameCursor.ItemHold.Hue,
                                Client.Game.UO.GameCursor.ItemHold.Container
                            );

                            UIManager
                                .GetGump<ContainerGump>(Client.Game.UO.GameCursor.ItemHold.Container)
                                ?.RequestUpdateContents();
                        }
                        else
                        {
                            // T1: the held item's values and its container's branch are one WorldWriter operation
                            ItemHold hold = Client.Game.UO.GameCursor.ItemHold;

                            Item item = world.Writer.ReturnHeldItem(
                                hold.Serial,
                                hold.Graphic,
                                hold.Hue,
                                hold.TotalAmount,
                                hold.Flags,
                                hold.Layer,
                                hold.X,
                                hold.Y,
                                hold.Z,
                                hold.Container,
                                out Entity container,
                                out WorldWriter.HeldItemReturn returned
                            );

                            switch (returned)
                            {
                                case WorldWriter.HeldItemReturn.Paperdoll:
                                    Log.Trace("Drop denied: held item returned to paperdoll");

                                    UIManager
                                        .GetGump<PaperDollGump>(item.Container)
                                        ?.RequestUpdateContents();

                                    break;

                                case WorldWriter.HeldItemReturn.Removed:
                                    Log.Trace("Drop denied: held item's container is an item; removed until the server resends it");

                                    break;

                                case WorldWriter.HeldItemReturn.Terrain:
                                    Log.Trace("Drop denied: held item returned to the ground");

                                    break;
                            }
                        }
                    }
                }
                else
                {
                    Log.Error(
                        $"Wrong data: serial = {Client.Game.UO.GameCursor.ItemHold.Serial:X8}  -  graphic = {Client.Game.UO.GameCursor.ItemHold.Graphic:X4}"
                    );
                }

                UIManager.GetGump<SplitMenuGump>(Client.Game.UO.GameCursor.ItemHold.Serial)?.Dispose();

                Client.Game.UO.GameCursor.ItemHold.Clear();
            }
            else
            {
                Log.Warn("There was a problem with ItemHold object. It was cleared before :|");
            }

            //var result = World.Items.Get(ItemHold.Serial);

            //if (result != null && !result.IsDestroyed)
            //    result.AllowedToDraw = true;

            byte code = p.ReadByte();

            if (code < 5)
            {
                world.MessageManager.HandleMessage(
                    null,
                    ServerErrorMessages.GetError(p[0], code),
                    string.Empty,
                    0x03b2,
                    MessageType.System,
                    3,
                    TextType.SYSTEM
                );
            }
        }

        private static void EndDraggingItem(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            Client.Game.UO.GameCursor.ItemHold.Enabled = false;
            Client.Game.UO.GameCursor.ItemHold.Dropped = false;
        }

        private static void DropItemAccepted(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            Client.Game.UO.GameCursor.ItemHold.Enabled = false;
            Client.Game.UO.GameCursor.ItemHold.Dropped = false;
        }

        private static void UpdateContainedItems(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            int layout = PacketLayouts.Resolve(ref p, PacketLayouts.ContainedItems(p.Buffer, Client.Game.UO.Version),
                Client.Game.UO.Version >= Utility.ClientVersion.CV_6017 ? PacketLayouts.ContainedGrid : PacketLayouts.ContainedNoGrid);
            ushort count = p.ReadUInt16();

            for (int i = 0; i < count; i++)
            {
                uint serial = p.ReadUInt32();
                ushort graphic = (ushort)(p.ReadUInt16() + p.ReadByte());
                ushort amount = Math.Max(p.ReadUInt16(), (ushort)1);
                ushort x = p.ReadUInt16();
                ushort y = p.ReadUInt16();

                if (layout == PacketLayouts.ContainedGrid)
                {
                    p.Skip(1);
                }

                uint containerSerial = p.ReadUInt32();
                ushort hue = p.ReadUInt16();

                if (i == 0)
                {
                    Entity container = world.Get(containerSerial);

                    if (container != null)
                    {
                        world.Writer.ClearContainer(containerSerial, container.Graphic == 0x2006, itemOnly: false);
                    }
                }

                AddItemToContainer(world, serial, graphic, amount, x, y, hue, containerSerial);
            }
        }

        private static void AddItemToContainer(
            World world,
            uint serial,
            ushort graphic,
            ushort amount,
            ushort x,
            ushort y,
            ushort hue,
            uint containerSerial
        )
        {
            if (Client.Game.UO.GameCursor.ItemHold.Serial == serial)
            {
                if (Client.Game.UO.GameCursor.ItemHold.Dropped)
                {
                    Client.Game.UO.GameCursor.ItemHold.Clear();
                }
        
                //else if (ItemHold.Graphic == graphic && ItemHold.Amount == amount &&
                //         ItemHold.Container == containerSerial)
                //{
               //     ItemHold.Enabled = false;
                //    ItemHold.Dropped = false;
                //}
            }
        
            // T1: the container, a mobile holding the serial, destroy-vs-reuse (an item from another container is destroyed
            // and recreated, except equipment going into a corpse), the values, the link at the end of the child list
            Item item = world.Writer.PlaceInContainer(serial, graphic, amount, x, y, hue, containerSerial, out Entity container);

            if (container == null)
            {
                Log.Warn($"No container ({containerSerial}) found");

                //container = world.GetOrCreateItem(containerSerial);
                return;
            }

            if (SerialHelper.IsMobile(containerSerial))
            {
                Mobile m = world.FindMobile(containerSerial);
                Item secureBox = m?.GetSecureTradeBox();
        
                if (secureBox != null)
                {
                    UIManager.GetTradingGump(secureBox)?.RequestUpdateContents();
                }
                else
                {
                    UIManager.GetGump<PaperDollGump>(containerSerial)?.RequestUpdateContents();
                }
            }
            else if (SerialHelper.IsItem(containerSerial))
            {
                Gump gump = UIManager.GetGump<BulletinBoardGump>(containerSerial);
        
                if (gump != null)
                {
                    NetClient.Socket.Send_BulletinBoardRequestMessageSummary(containerSerial, serial);
                }
                else
                {
                    gump = UIManager.GetGump<SpellbookGump>(containerSerial);
        
                    if (gump == null)
                    {
                        gump = UIManager.GetGump<ContainerGump>(containerSerial);
        
                        if (gump != null)
                        {
                            ((ContainerGump)gump).CheckItemControlPosition(item);
                        }
        
                        if (ProfileManager.CurrentProfile.GridLootType > 0)
                        {
                            GridLootGump grid_gump = UIManager.GetGump<GridLootGump>(containerSerial);
        
                            if (grid_gump == null
                                && SerialHelper.IsValid(_requestedGridLoot)
                                && _requestedGridLoot == containerSerial)
                            {
                                grid_gump = new GridLootGump(world, _requestedGridLoot);
                                UIManager.Add(grid_gump);
                                _requestedGridLoot = 0;
                            }
        
                            grid_gump?.RequestUpdateContents();
                        }
                    }
        
                    if (gump != null)
                    {
                        if (SerialHelper.IsItem(containerSerial))
                        {
                            ((Item)container).Opened = true;
                        }
        
                        gump.RequestUpdateContents();
                    }
                }
            }
        
            UIManager.GetTradingGump(containerSerial)?.RequestUpdateContents();
        }
    }
}
