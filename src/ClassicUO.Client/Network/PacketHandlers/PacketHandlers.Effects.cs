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
        private static void TargetCursor(World world, ref NetReader p)
        {
            world.TargetManager.SetTargeting(
                (CursorTarget)p.ReadByte(),
                p.ReadUInt32(),
                (TargetType)p.ReadByte()
            );

            if (world.Party.PartyHealTimer < Time.Ticks && world.Party.PartyHealTarget != 0)
            {
                world.TargetManager.Target(world.Party.PartyHealTarget);
                world.Party.PartyHealTimer = 0;
                world.Party.PartyHealTarget = 0;
            }
        }

        private static void GraphicEffect(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            GraphicEffectType type = (GraphicEffectType)p.ReadByte();

            if (type > GraphicEffectType.FixedFrom)
            {
                if (type == GraphicEffectType.ScreenFade && p[0] == 0x70)
                {
                    p.Skip(8);
                    ushort val = p.ReadUInt16();

                    if (val > 4)
                    {
                        val = 4;
                    }

                    Log.Warn("Effect not implemented");
                }

                return;
            }

            uint source = p.ReadUInt32();
            uint target = p.ReadUInt32();
            ushort graphic = p.ReadUInt16();
            ushort srcX = p.ReadUInt16();
            ushort srcY = p.ReadUInt16();
            sbyte srcZ = p.ReadSByte();
            ushort targetX = p.ReadUInt16();
            ushort targetY = p.ReadUInt16();
            sbyte targetZ = p.ReadSByte();
            byte speed = p.ReadByte();
            byte duration = p.ReadByte();
            ushort unk = p.ReadUInt16();
            bool fixedDirection = p.ReadBoolean();
            bool doesExplode = p.ReadBoolean();
            uint hue = 0;
            GraphicEffectBlendMode blendmode = 0;

            if (p[0] == 0x70) { }
            else
            {
                hue = p.ReadUInt32();
                blendmode = (GraphicEffectBlendMode)(p.ReadUInt32() % 7);

                if (p[0] == 0xC7)
                {
                    var tileID = p.ReadUInt16();
                    var explodeEffect = p.ReadUInt16();
                    var explodeSound = p.ReadUInt16();
                    var serial = p.ReadUInt32();
                    var layer = p.ReadByte();
                    p.Skip(2);
                }
            }

            world.SpawnEffect(
                type,
                source,
                target,
                graphic,
                (ushort)hue,
                srcX,
                srcY,
                srcZ,
                targetX,
                targetY,
                targetZ,
                speed,
                duration,
                fixedDirection,
                doesExplode,
                false,
                blendmode
            );
        }

        private static void MultiPlacement(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            bool allowGround = p.ReadBoolean();
            uint targID = p.ReadUInt32();
            byte flags = p.ReadByte();
            p.Seek(18, SeekOrigin.Begin);
            ushort multiID = p.ReadUInt16();
            ushort xOff = p.ReadUInt16();
            ushort yOff = p.ReadUInt16();
            ushort zOff = p.ReadUInt16();
            // the hue word exists from 7.0.9 (30 bytes); older clients get the 26-byte form without it
            ushort hue = p.Position + 2 <= p.Length ? p.ReadUInt16() : (ushort)0;

            world.TargetManager.SetTargetingMulti(targID, multiID, xOff, yOff, zOff, hue);
        }

        private static void ParticleEffect3D(World world, ref NetReader p) { }

        private static void BuffDebuff(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            const ushort BUFF_ICON_START = 0x03E9;
            const ushort BUFF_ICON_START_NEW = 0x466;

            uint serial = p.ReadUInt32();
            BuffIconType ic = (BuffIconType)p.ReadUInt16();

            ushort iconID =
                (ushort)ic >= BUFF_ICON_START_NEW
                    ? (ushort)(ic - (BUFF_ICON_START_NEW - 125))
                    : (ushort)((ushort)ic - BUFF_ICON_START);

            if (iconID < BuffTable.Table.Length)
            {
                BuffGump gump = UIManager.GetGump<BuffGump>();
                ushort count = p.ReadUInt16();

                if (count != 0)
                {
                    // the second and third text blocks may be absent (RunUO and ModernUO end the packet after one or two)
                    PacketLayouts.Resolve(ref p, PacketLayouts.BuffEntries(p.Buffer), 3);
                }

                if (count == 0)
                {
                    world.PlayerState.RemoveBuff(ic);
                    gump?.RequestUpdateContents();
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        ushort source_type = p.ReadUInt16();
                        p.Skip(2);
                        ushort icon = p.ReadUInt16();
                        ushort queue_index = p.ReadUInt16();
                        p.Skip(4);
                        ushort timer = p.ReadUInt16();
                        p.Skip(3);

                        uint titleCliloc = p.ReadUInt32();
                        uint descriptionCliloc = p.ReadUInt32();
                        uint wtfCliloc = p.ReadUInt32();

                        ushort arg_length = p.ReadUInt16();
                        var str = p.ReadLittleUni(2);
                        var args = str + p.ReadLittleUni();
                        string title = Client.Game.UO.FileManager.Clilocs.Translate(
                            (int)titleCliloc,
                            args,
                            true
                        );

                        string args_2 = string.Empty;

                        if (p.Remaining > 0)
                        {
                            arg_length = p.ReadUInt16();
                            args_2 = p.ReadLittleUni();
                        }

                        string description = string.Empty;

                        if (descriptionCliloc != 0)
                        {
                            description =
                                "\n"
                                + Client.Game.UO.FileManager.Clilocs.Translate(
                                    (int)descriptionCliloc,
                                    String.IsNullOrEmpty(args_2) ? args : args_2,
                                    true
                                );

                            if (description.Length < 2)
                            {
                                description = string.Empty;
                            }
                        }

                        string args_3 = string.Empty;

                        if (p.Remaining > 0)
                        {
                            arg_length = p.ReadUInt16();
                            args_3 = p.ReadLittleUni();
                        }

                        string wtf = string.Empty;

                        if (wtfCliloc != 0)
                        {
                            wtf = Client.Game.UO.FileManager.Clilocs.Translate(
                                (int)wtfCliloc,
                                String.IsNullOrEmpty(args_3) ? args : args_3,
                                true
                            );

                            if (!string.IsNullOrWhiteSpace(wtf))
                            {
                                wtf = $"\n{wtf}";
                            }
                        }

                        string text = $"<left>{title}{description}{wtf}</left>";
                        bool alreadyExists = world.PlayerState.IsBuffIconExists(ic);
                        world.PlayerState.AddBuff(ic, BuffTable.Table[iconID], timer, text);

                        if (!alreadyExists)
                        {
                            gump?.RequestUpdateContents();
                        }
                    }
                }
            }
        }
    }
}
