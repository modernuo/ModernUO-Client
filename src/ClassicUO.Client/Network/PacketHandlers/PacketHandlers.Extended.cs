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
        private static void ExtendedCommand(World world, ref NetReader p)
        {
            ushort cmd = p.ReadUInt16();

            switch (cmd)
            {
                case 0:
                    break;

                //===========================================================================================
                //===========================================================================================
                case 1: // fast walk prevention
                    for (int i = 0; i < 6; i++)
                    {
                        world.Player.Walker.FastWalkStack.SetValue(i, p.ReadUInt32());
                    }

                    break;

                //===========================================================================================
                //===========================================================================================
                case 2: // add key to fast walk stack
                    world.Player.Walker.FastWalkStack.AddValue(p.ReadUInt32());

                    break;

                //===========================================================================================
                //===========================================================================================
                case 4: // close generic gump
                    uint ser = p.ReadUInt32();
                    int button = (int)p.ReadUInt32();

                    LinkedListNode<Gump> first = UIManager.Gumps.First;

                    while (first != null)
                    {
                        LinkedListNode<Gump> nextGump = first.Next;

                        if (first.Value.ServerSerial == ser && first.Value.IsFromServer)
                        {
                            if (button != 0)
                            {
                                (first.Value as Gump)?.OnButtonClick(button);
                            }
                            else
                            {
                                if (first.Value.CanMove)
                                {
                                    UIManager.SavePosition(ser, first.Value.Location);
                                }
                                else
                                {
                                    UIManager.RemovePosition(ser);
                                }
                            }

                            first.Value.Dispose();
                        }

                        first = nextGump;
                    }

                    break;

                //===========================================================================================
                //===========================================================================================
                case 6: //party
                    world.Party.ParsePacket(ref p);

                    break;

                //===========================================================================================
                //===========================================================================================
                case 8: // map change
                    world.MapIndex = p.ReadByte();

                    break;

                //===========================================================================================
                //===========================================================================================
                case 0x0C: // close statusbar gump
                    UIManager.GetGump<HealthBarGump>(p.ReadUInt32())?.Dispose();

                    break;

                //===========================================================================================
                //===========================================================================================
                case 0x10: // display equip info
                    Item item = world.FindItem(p.ReadUInt32());

                    if (item == null)
                    {
                        return;
                    }

                    uint cliloc = p.ReadUInt32();
                    string str = string.Empty;

                    if (cliloc > 0)
                    {
                        str = Client.Game.UO.FileManager.Clilocs.GetString((int)cliloc, true);

                        if (!string.IsNullOrEmpty(str))
                        {
                            world.Writer.SetName(item, str);
                        }

                        world.MessageManager.HandleMessage(
                            item,
                            str,
                            item.Name,
                            0x3B2,
                            MessageType.Regular,
                            3,
                            TextType.OBJECT,
                            true
                        );
                    }

                    str = string.Empty;
                    ushort crafterNameLen = 0;
                    uint next = p.ReadUInt32();

                    Span<char> span = stackalloc char[256];
                    ValueStringBuilder strBuffer = new ValueStringBuilder(span);
                    if (next == 0xFFFFFFFD)
                    {
                        crafterNameLen = p.ReadUInt16();

                        if (crafterNameLen > 0)
                        {
                            strBuffer.Append(ResGeneral.CraftedBy);
                            strBuffer.Append(p.ReadCp1252(crafterNameLen));
                        }
                    }

                    if (crafterNameLen != 0)
                    {
                        next = p.ReadUInt32();
                    }

                    if (next == 0xFFFFFFFC)
                    {
                        strBuffer.Append("[Unidentified");
                    }

                    byte count = 0;

                    while (p.Position < p.Length - 4)
                    {
                        if (count != 0 || next == 0xFFFFFFFD || next == 0xFFFFFFFC)
                        {
                            next = p.ReadUInt32();
                        }

                        short charges = (short)p.ReadUInt16();
                        string attr = Client.Game.UO.FileManager.Clilocs.GetString((int)next);

                        if (attr != null)
                        {
                            if (charges == -1)
                            {
                                if (count > 0)
                                {
                                    strBuffer.Append("/");
                                    strBuffer.Append(attr);
                                }
                                else
                                {
                                    strBuffer.Append(" [");
                                    strBuffer.Append(attr);
                                }
                            }
                            else
                            {
                                strBuffer.Append("\n[");
                                strBuffer.Append(attr);
                                strBuffer.Append(" : ");
                                strBuffer.Append(charges.ToString());
                                strBuffer.Append("]");
                                count += 20;
                            }
                        }

                        count++;
                    }

                    if (count < 20 && count > 0 || next == 0xFFFFFFFC && count == 0)
                    {
                        strBuffer.Append(']');
                    }

                    if (strBuffer.Length != 0)
                    {
                        world.MessageManager.HandleMessage(
                            item,
                            strBuffer.ToString(),
                            item.Name,
                            0x3B2,
                            MessageType.Regular,
                            3,
                            TextType.OBJECT,
                            true
                        );
                    }

                    strBuffer.Dispose();

                    NetClient.Socket.Send_MegaClilocRequest_Old(item);

                    break;

                //===========================================================================================
                //===========================================================================================
                case 0x11:
                    break;

                //===========================================================================================
                //===========================================================================================
                case 0x14: // display popup/context menu
                    GumpFactory.OpenPopupMenu(world, PopupMenuData.Parse(ref p));

                    break;

                //===========================================================================================
                //===========================================================================================
                case 0x16: // close user interface windows
                    uint id = p.ReadUInt32();
                    uint serial = p.ReadUInt32();

                    switch (id)
                    {
                        case 1: // paperdoll
                            UIManager.GetGump<PaperDollGump>(serial)?.Dispose();

                            break;

                        case 2: //statusbar
                            UIManager.GetGump<HealthBarGump>(serial)?.Dispose();

                            if (serial == world.Player.Serial)
                            {
                                StatusGumpBase.GetStatusGump()?.Dispose();
                            }

                            break;

                        case 8: // char profile
                            UIManager.GetGump<ProfileGump>()?.Dispose();

                            break;

                        case 0x0C: //container
                            UIManager.GetGump<ContainerGump>(serial)?.Dispose();

                            break;
                    }

                    break;

                //===========================================================================================
                //===========================================================================================
                case 0x18: // enable map patches
                    uint patchMaps = p.ReadUInt32();
                    bool patched = false;

                    for (uint i = 0; i < patchMaps && p.Remaining >= 8; i++)
                    {
                        uint patchStatics = p.ReadUInt32();
                        uint patchLand = p.ReadUInt32();
                        patched |= Client.Game.UO.FileManager.Maps.ApplyPatch((int)i, patchLand, patchStatics);
                    }

                    if (patched)
                    {
                        //List<GameObject> list = new List<GameObject>();

                        //foreach (int i in World.Map.GetUsedChunks())
                        //{
                        //    Chunk chunk = World.Map.Chunks[i];

                        //    for (int xx = 0; xx < 8; xx++)
                        //    {
                        //        for (int yy = 0; yy < 8; yy++)
                        //        {
                        //            Tile tile = chunk.Tiles[xx, yy];

                        //            for (GameObject obj = tile.FirstNode; obj != null; obj = obj.Right)
                        //            {
                        //                if (!(obj is Static) && !(obj is Land))
                        //                {
                        //                    list.Add(obj);
                        //                }
                        //            }
                        //        }
                        //    }
                        //}


                        int map = world.MapIndex;
                        world.MapIndex = -1;
                        world.MapIndex = map;

                        Log.Trace("Map Patches applied.");
                    }

                    break;

                //===========================================================================================
                //===========================================================================================
                case 0x19: //extened stats
                    byte version = p.ReadByte();
                    serial = p.ReadUInt32();

                    switch (version)
                    {
                        case 0:
                            Mobile bonded = world.FindMobile(serial);

                            if (bonded == null)
                            {
                                break;
                            }

                            bool dead = p.ReadBoolean();
                            world.Writer.SetDead(bonded, dead);

                            break;

                        case 2:

                            if (serial == world.Player)
                            {
                                byte updategump = p.ReadByte();
                                byte state = p.ReadByte();

                                world.PlayerState.StrLock = (Lock)((state >> 4) & 3);
                                world.PlayerState.DexLock = (Lock)((state >> 2) & 3);
                                world.PlayerState.IntLock = (Lock)(state & 3);

                                StatusGumpBase.GetStatusGump()?.RequestUpdateContents();
                            }

                            break;

                        case 5:

                            int pos = p.Position;
                            byte zero = p.ReadByte();
                            byte type2 = p.ReadByte();

                            if (type2 == 0xFF)
                            {
                                byte status = p.ReadByte();
                                ushort animation = p.ReadUInt16();
                                ushort frame = p.ReadUInt16();

                                if (status == 0 && animation == 0 && frame == 0)
                                {
                                    p.Seek(pos, SeekOrigin.Begin);
                                    goto case 0;
                                }

                                Mobile mobile = world.FindMobile(serial);

                                if (mobile != null)
                                {
                                    // T1 (funnel part 6): the animation held at the frame
                                    world.Writer.AnimateMobile(
                                        mobile,
                                        Mobile.GetReplacedObjectAnimation(mobile.Graphic, animation),
                                        frame: (byte)frame,
                                        hold: true
                                    );
                                }
                            }
                            else if (world.Player != null && serial == world.Player)
                            {
                                p.Seek(pos, SeekOrigin.Begin);
                                goto case 2;
                            }

                            break;
                    }

                    break;

                //===========================================================================================
                //===========================================================================================
                case 0x1B: // new spellbook content
                    p.Skip(2);
                    uint spellbookSerial = p.ReadUInt32();
                    ushort spellbookGraphic = p.ReadUInt16();
                    ushort type = p.ReadUInt16();
                    uint spells0 = 0, spells1 = 0;

                    for (int i = 0; i < 4; i++)
                    {
                        spells0 |= (uint)(p.ReadByte() << (i * 8));
                    }

                    for (int i = 0; i < 4; i++)
                    {
                        spells1 |= (uint)(p.ReadByte() << (i * 8));
                    }

                    // T1 (funnel part 6): the spellbook's graphic, its emptied child list and Sync, then the (detached) entries
                    Item spellbook = world.Writer.FillSpellbook(spellbookSerial, spellbookGraphic, spells0, spells1);

                    if (spellbook == null)
                    {
                        break;
                    }

                    UIManager.GetGump<SpellbookGump>(spellbook)?.RequestUpdateContents();

                    break;

                //===========================================================================================
                //===========================================================================================
                case 0x1D: // house revision state
                    serial = p.ReadUInt32();
                    uint revision = p.ReadUInt32();

                    Item multi = world.FindItem(serial);

                    if (multi == null)
                    {
                        world.HouseManager.Remove(serial);
                    }

                    if (
                        !world.HouseManager.TryGetHouse(serial, out House house)
                        || !house.IsCustom
                        || house.Revision != revision
                    )
                    {
                        Handler._customHouseRequests.Add(serial);
                    }
                    else
                    {
                        house.Generate();
                        world.BoatMovingManager.ClearSteps(serial);

                        UIManager.GetGump<MiniMapGump>()?.RequestUpdateContents();

                        if (world.HouseManager.EntityIntoHouse(serial, world.Player))
                        {
                            Client.Game.GetScene<GameScene>()?.UpdateMaxDrawZ(true);
                        }
                    }

                    break;

                //===========================================================================================
                //===========================================================================================
                case 0x20:
                    serial = p.ReadUInt32();
                    type = p.ReadByte();
                    ushort graphic = p.ReadUInt16();
                    ushort x = p.ReadUInt16();
                    ushort y = p.ReadUInt16();
                    sbyte z = p.ReadSByte();

                    switch (type)
                    {
                        case 1: // update
                            break;

                        case 2: // remove
                            break;

                        case 3: // update multi pos
                            break;

                        case 4: // begin
                            HouseCustomizationGump gump = UIManager.GetGump<HouseCustomizationGump>();

                            if (gump != null)
                            {
                                break;
                            }

                            gump = new HouseCustomizationGump(world, serial, 50, 50);
                            UIManager.Add(gump);

                            break;

                        case 5: // end
                            UIManager.GetGump<HouseCustomizationGump>(serial)?.Dispose();

                            break;
                    }

                    break;

                //===========================================================================================
                //===========================================================================================
                case 0x21:

                    for (int i = 0; i < 2; i++)
                    {
                        world.PlayerState.Abilities[i] &= (Ability)0x7F;
                    }

                    break;

                //===========================================================================================
                //===========================================================================================
                case 0x22:
                    p.Skip(1);

                    Entity en = world.Get(p.ReadUInt32());

                    if (en != null)
                    {
                        byte damage = p.ReadByte();

                        if (damage > 0)
                        {
                            world.WorldTextManager.AddDamage(en, damage);
                        }
                    }

                    break;

                case 0x25:

                    ushort spell = p.ReadUInt16();
                    bool active = p.ReadBoolean();

                    foreach (Gump g in UIManager.Gumps)
                    {
                        if (!g.IsDisposed && g.IsVisible)
                        {
                            if (g is UseSpellButtonGump spellButton && spellButton.SpellID == spell)
                            {
                                if (active)
                                {
                                    spellButton.Hue = 38;
                                    world.ActiveSpellIcons.Add(spell);
                                }
                                else
                                {
                                    spellButton.Hue = 0;
                                    world.ActiveSpellIcons.Remove(spell);
                                }

                                break;
                            }
                        }
                    }

                    break;

                //===========================================================================================
                //===========================================================================================
                case 0x26:
                    byte val = p.ReadByte();

                    if (val > (int)CharacterSpeedType.FastUnmountAndCantRun)
                    {
                        val = 0;
                    }

                    world.Writer.SetSpeedMode(world.Player, (CharacterSpeedType)val);

                    break;

                case 0x2A:
                    bool isfemale = p.ReadBoolean();
                    byte race = p.ReadByte();

                    GumpFactory.OpenRaceChange(world, isfemale, race);
                    break;

                case 0x2B:
                    serial = p.ReadUInt16();
                    byte animID = p.ReadByte();
                    byte frameCount = p.ReadByte();

                    foreach (Mobile m in world.Mobiles.Values)
                    {
                        if ((m.Serial & 0xFFFF) == serial)
                        {
                            // T1 (funnel part 6): the animation held at the frame
                            world.Writer.AnimateMobile(m, animID, frame: frameCount, hold: true);

                            break;
                        }
                    }

                    break;

                case 0xBEEF: // ClassicUO commands

                    type = p.ReadUInt16();

                    break;

                default:
                    Log.Warn($"Unhandled 0xBF - sub: {cmd.ToHex()}");

                    break;
            }
        }
    }
}
