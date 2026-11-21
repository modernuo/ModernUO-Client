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
        private static void UpdateItem(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            uint serial = p.ReadUInt32();
            ushort count = 0;
            byte graphicInc = 0;
            byte direction = 0;
            ushort hue = 0;
            byte flags = 0;
            byte type = 0;

            if ((serial & 0x80000000) != 0)
            {
                serial &= 0x7FFFFFFF;
                count = 1;
            }

            ushort graphic = p.ReadUInt16();

            if ((graphic & 0x8000) != 0)
            {
                graphic &= 0x7FFF;
                graphicInc = p.ReadByte();
            }

            if (count > 0)
            {
                count = p.ReadUInt16();
            }
            else
            {
                count++;
            }

            ushort x = p.ReadUInt16();

            if ((x & 0x8000) != 0)
            {
                x &= 0x7FFF;
                direction = 1;
            }

            ushort y = p.ReadUInt16();

            if ((y & 0x8000) != 0)
            {
                y &= 0x7FFF;
                hue = 1;
            }

            if ((y & 0x4000) != 0)
            {
                y &= 0x3FFF;
                flags = 1;
            }

            if (direction != 0)
            {
                direction = p.ReadByte();
            }

            sbyte z = p.ReadSByte();

            if (hue != 0)
            {
                hue = p.ReadUInt16();
            }

            if (flags != 0)
            {
                flags = p.ReadByte();
            }

            //if (graphic != 0x2006)
            //    graphic += graphicInc;

            if (graphic >= 0x4000)
            {
                //graphic -= 0x4000;
                type = 2;
            }

            UpdateGameObject(
                world,
                serial,
                graphic,
                graphicInc,
                count,
                x,
                y,
                z,
                (Direction)direction,
                hue,
                (Flags)flags,
                count,
                type,
                1
            );
        }

        private static void DeleteObject(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            uint serial = p.ReadUInt32();

            if (world.Player == serial)
            {
                return;
            }

            Entity entity = world.Get(serial);

            if (entity == null)
            {
                return;
            }

            bool updateAbilities = false;

            if (entity is Item it)
            {
                uint cont = it.Container & 0x7FFFFFFF;

                if (SerialHelper.IsValid(it.Container))
                {
                    Entity top = world.Get(it.RootContainer);

                    if (top != null)
                    {
                        if (top == world.Player)
                        {
                            updateAbilities =
                                it.Layer == Layer.OneHanded || it.Layer == Layer.TwoHanded;
                            Item tradeBoxItem = world.Player.GetSecureTradeBox();

                            if (tradeBoxItem != null)
                            {
                                UIManager.GetTradingGump(tradeBoxItem)?.RequestUpdateContents();
                            }
                        }
                    }

                    if (cont == world.Player && it.Layer == Layer.Invalid)
                    {
                        Client.Game.UO.GameCursor.ItemHold.Enabled = false;
                    }

                    if (it.Layer != Layer.Invalid)
                    {
                        UIManager.GetGump<PaperDollGump>(cont)?.RequestUpdateContents();
                    }

                    UIManager.GetGump<ContainerGump>(cont)?.RequestUpdateContents();

                    if (
                        top != null
                        && top.Graphic == 0x2006
                        && (
                            ProfileManager.CurrentProfile.GridLootType == 1
                            || ProfileManager.CurrentProfile.GridLootType == 2
                        )
                    )
                    {
                        UIManager.GetGump<GridLootGump>(cont)?.RequestUpdateContents();
                    }

                    if (it.Graphic == 0x0EB0)
                    {
                        UIManager.GetGump<BulletinBoardItem>(serial)?.Dispose();

                        BulletinBoardGump bbgump = UIManager.GetGump<BulletinBoardGump>();

                        if (bbgump != null)
                        {
                            bbgump.RemoveBulletinObject(serial);
                        }
                    }
                }
            }

            if (world.CorpseManager.Exists(0, serial))
            {
                return;
            }

            if (entity is Mobile m)
            {
                if (world.Party.Contains(serial))
                {
                    // m.RemoveFromTile();
                }

                // else
                {
                    //BaseHealthBarGump bar = UIManager.GetGump<BaseHealthBarGump>(serial);

                    //if (bar == null)
                    //{
                    //    NetClient.Socket.Send(new PCloseStatusBarGump(serial));
                    //}

                    // T1: the mobile and everything it wears or holds (contents, OPL, Destroy, the ECS entities)
                    world.Writer.RemoveMobile(m);
                }
            }
            else
            {
                Item item = (Item)entity;
                bool wasMulti = item.IsMulti;
                Layer layer = item.Layer;

                // T1: the house, the parent's child list and the removal (contents, OPL, Destroy, the ECS entity)
                world.Writer.RemoveWorldItem(item, out Entity cont);

                if (cont != null)
                {
                    if (layer != Layer.Invalid)
                    {
                        UIManager.GetGump<PaperDollGump>(cont)?.RequestUpdateContents();
                    }
                }
                else if (wasMulti)
                {
                    UIManager.GetGump<MiniMapGump>()?.RequestUpdateContents();
                }

                if (updateAbilities)
                {
                    world.Player.UpdateAbilities();
                }
            }
        }

        private static void DragAnimation(World world, ref NetReader p)
        {
            ushort graphic = p.ReadUInt16();
            graphic += p.ReadByte();
            ushort hue = p.ReadUInt16();
            ushort count = p.ReadUInt16();
            uint source = p.ReadUInt32();
            ushort sourceX = p.ReadUInt16();
            ushort sourceY = p.ReadUInt16();
            sbyte sourceZ = p.ReadSByte();
            uint dest = p.ReadUInt32();
            ushort destX = p.ReadUInt16();
            ushort destY = p.ReadUInt16();
            sbyte destZ = p.ReadSByte();

            if (graphic == 0x0EED)
            {
                graphic = 0x0EEF;
            }
            else if (graphic == 0x0EEA)
            {
                graphic = 0x0EEC;
            }
            else if (graphic == 0x0EF0)
            {
                graphic = 0x0EF2;
            }

            Mobile entity = world.FindMobile(source);

            if (entity == null)
            {
                source = 0;
            }
            else
            {
                sourceX = entity.X;
                sourceY = entity.Y;
                sourceZ = entity.Z;
            }

            Mobile destEntity = world.FindMobile(dest);

            if (destEntity == null)
            {
                dest = 0;
            }
            else
            {
                destX = destEntity.X;
                destY = destEntity.Y;
                destZ = destEntity.Z;
            }

            world.SpawnEffect(
                !SerialHelper.IsValid(source) || !SerialHelper.IsValid(dest)
                    ? GraphicEffectType.Moving
                    : GraphicEffectType.DragEffect,
                source,
                dest,
                graphic,
                hue,
                sourceX,
                sourceY,
                sourceZ,
                destX,
                destY,
                destZ,
                5,
                5000,
                true,
                false,
                false,
                GraphicEffectBlendMode.Normal
            );

            //if (effect.AnimDataFrame.FrameCount != 0)
            //{
            //    effect.IntervalInMs = (uint) (effect.AnimDataFrame.FrameInterval * 45);
            //}
            //else
            //{
            //    effect.IntervalInMs = 13;
            //}
        }

        private static void EquipItem(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            uint serial = p.ReadUInt32();
            ushort graphic = (ushort)(p.ReadUInt16() + p.ReadSByte());
            Layer layer = (Layer)p.ReadByte();
            uint container = p.ReadUInt32();
            ushort hue = p.ReadUInt16();

            // T1: the item (found or created), out of its old container unless it is new or a backpack, the packet's values,
            // then at the end of the wearer's child list
            Item item = world.Writer.Equip(serial, graphic, layer, container, hue, out Entity entity, out uint shown);

            if (item == null)
            {
                return;
            }

            if (SerialHelper.IsValid(shown))
            {
                UIManager.GetGump<ContainerGump>(shown)?.RequestUpdateContents();

                UIManager.GetGump<PaperDollGump>(shown)?.RequestUpdateContents();
            }

            if (layer >= Layer.ShopBuyRestock && layer <= Layer.ShopSell)
            {
                //item.Clear();
            }
            else if (SerialHelper.IsValid(container) && layer < Layer.Mount)
            {
                UIManager.GetGump<PaperDollGump>(container)?.RequestUpdateContents();
            }

            if (
                entity == world.Player
                && (layer == Layer.OneHanded || layer == Layer.TwoHanded)
            )
            {
                world.Player?.UpdateAbilities();
            }

            //if (ItemHold.Serial == item.Serial)
            //{
            //    Console.WriteLine("PACKET - ITEM EQUIP");
            //    ItemHold.Enabled = false;
            //    ItemHold.Dropped = true;
            //}
        }

        private static void CorpseEquipment(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            uint serial = p.ReadUInt32();
            Entity corpse = world.Get(serial);

            if (corpse == null)
            {
                return;
            }

            // if it's not a corpse we should skip this [?]
            if (corpse.Graphic != 0x2006)
            {
                return;
            }

            Layer layer = (Layer)p.ReadByte();

            while (layer != Layer.Invalid && p.Position < p.Length)
            {
                uint item_serial = p.ReadUInt32();

                if (layer - 1 != Layer.Backpack)
                {
                    world.Writer.PlaceCorpseEquipment(corpse, item_serial, layer - 1);
                }

                layer = (Layer)p.ReadByte();
            }
        }

        private static void UpdateItemSA(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            int layout = PacketLayouts.Resolve(ref p, PacketLayouts.ItemSA(p.Buffer, Client.Game.UO.Version),
                Client.Game.UO.Version >= Utility.ClientVersion.CV_7090 ? PacketLayouts.ItemSALong : PacketLayouts.ItemSAShort);
            p.Skip(2);
            byte type = p.ReadByte();
            uint serial = p.ReadUInt32();
            ushort graphic = p.ReadUInt16();
            byte graphicInc = p.ReadByte();
            ushort amount = p.ReadUInt16();
            ushort unk = p.ReadUInt16();
            ushort x = p.ReadUInt16();
            ushort y = p.ReadUInt16();
            sbyte z = p.ReadSByte();
            Direction dir = (Direction)p.ReadByte();
            ushort hue = p.ReadUInt16();
            Flags flags = (Flags)p.ReadByte();
            // the 24-byte SA layout (clients before 7.0.9) ends before this field
            ushort unk2 = layout == PacketLayouts.ItemSALong ? p.ReadUInt16() : (ushort)0;

            if (serial != world.Player)
            {
                UpdateGameObject(
                    world,
                    serial,
                    graphic,
                    graphicInc,
                    amount,
                    x,
                    y,
                    z,
                    dir,
                    hue,
                    flags,
                    unk,
                    type,
                    unk2
                );

                if (graphic == 0x2006 && ProfileManager.CurrentProfile.AutoOpenCorpses)
                {
                    world.Player.TryOpenCorpses();
                }
            }
            else if (p[0] == 0xF7)
            {
                UpdatePlayer(world, serial, graphic, graphicInc, hue, flags, x, y, z, 0, dir);
            }
        }

        private static void UpdateGameObject(
            World world,
            uint serial,
            ushort graphic,
            byte graphic_inc,
            ushort count,
            ushort x,
            ushort y,
            sbyte z,
            Direction direction,
            ushort hue,
            Flags flagss,
            int UNK,
            byte type,
            ushort UNK_2
        )
        {
            Mobile mobile = null;
            Item item = null;
            Entity obj = world.Get(serial);

            if (
                Client.Game.UO.GameCursor.ItemHold.Enabled
                && Client.Game.UO.GameCursor.ItemHold.Serial == serial
            )
            {
                if (SerialHelper.IsValid(Client.Game.UO.GameCursor.ItemHold.Container))
                {
                    if (Client.Game.UO.GameCursor.ItemHold.Layer == 0)
                    {
                        UIManager
                            .GetGump<ContainerGump>(Client.Game.UO.GameCursor.ItemHold.Container)
                            ?.RequestUpdateContents();
                    }
                    else
                    {
                        UIManager
                            .GetGump<PaperDollGump>(Client.Game.UO.GameCursor.ItemHold.Container)
                            ?.RequestUpdateContents();
                    }
                }

                Client.Game.UO.GameCursor.ItemHold.UpdatedInWorld = true;
            }

            bool created = false;

            // T1: a serial that is (or becomes) an item goes through the funnel: create or take out of its container, the
            // values (ECS and GameObject) and CheckGraphicChange's state; then the callbacks below; then the tile
            if (obj == null || obj.IsDestroyed ? !(SerialHelper.IsMobile(serial) && type != 3) : obj is Item)
            {
                item = world.Writer.UpsertWorldItem(serial, graphic, graphic_inc, count, x, y, z, direction, hue, flagss, type, out created, out WorldWriter.Placement placement);

                if (item == null)
                {
                    return;
                }

                if (graphic != 0x2006)
                {
                    graphic += graphic_inc;
                }

                if (created && !item.IsClicked && graphic == 0x2006)
                {
                    if (ProfileManager.CurrentProfile.ShowNewCorpseNameIncoming)
                    {
                        GameActions.SingleClick(world, serial);
                    }
                }

                if (
                    Client.Game.UO.GameCursor.ItemHold.Serial == serial
                    && Client.Game.UO.GameCursor.ItemHold.Dropped
                )
                {
                    // we want maintain the item data due to the denymoveitem packet
                    //ItemHold.Clear();
                    Client.Game.UO.GameCursor.ItemHold.Enabled = false;
                    Client.Game.UO.GameCursor.ItemHold.Dropped = false;
                }

                // after the SingleClick above (it sends a packet), as before T1
                world.Writer.PlaceWorldItem(placement);

                if (item.OnGround && graphic == 0x2006 && ProfileManager.CurrentProfile.AutoOpenCorpses)
                {
                    world.Player.TryOpenCorpses();
                }

                return;
            }

            // T1 (funnel part 4): a mobile goes through the funnel: create or take obj, the step or the reset to the packet's
            // position, the body, hue and flags (ECS and GameObject); then the callbacks below; then the tile
            mobile = world.Writer.UpsertMobile(obj, serial, graphic, graphic_inc, x, y, z, direction, hue, flagss, out created, out WorldWriter.Placement mobilePlacement);

            if (mobile == null)
            {
                return;
            }

            if (created && !mobile.IsClicked)
            {
                if (ProfileManager.CurrentProfile.ShowNewMobileNameIncoming)
                {
                    GameActions.SingleClick(world, serial);
                }
            }

            // after the SingleClick above (it sends a packet), as before T1
            world.Writer.PlaceMobile(mobilePlacement);

            if (created)
            {
                // This is actually a way to get all Hp from all new mobiles.
                // Real UO client does it only when LastAttack == serial.
                // We force to close suddenly.
                GameActions.RequestMobileStatus(world, serial);

                //if (TargetManager.LastAttack != serial)
                //{
                //    GameActions.SendCloseStatus(serial);
                //}
            }
        }
    }
}
