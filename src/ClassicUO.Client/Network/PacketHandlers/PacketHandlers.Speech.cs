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
        private static void ClientTalk(World world, ref NetReader p)
        {
            switch (p.ReadByte())
            {
                case 0x78:
                    break;

                case 0x3C:
                    break;

                case 0x25:
                    break;

                case 0x2E:
                    break;
            }
        }

        private static void Talk(World world, ref NetReader p)
        {
            uint serial = p.ReadUInt32();
            Entity entity = world.Get(serial);
            ushort graphic = p.ReadUInt16();
            MessageType type = (MessageType)p.ReadByte();
            ushort hue = p.ReadUInt16();
            ushort font = p.ReadUInt16();
            string name = p.ReadCp1252(30);
            string text;

            if (p.Length > 44)
            {
                p.Seek(44, SeekOrigin.Begin);
                text = p.ReadCp1252();
            }
            else
            {
                text = string.Empty;
            }

            if (
                serial == 0
                && graphic == 0
                && type == MessageType.Regular
                && font == 0xFFFF
                && hue == 0xFFFF
                && name.StartsWith("SYSTEM")
            )
            {
                NetClient.Socket.Send_ACKTalk();

                return;
            }

            TextType text_type = TextType.SYSTEM;

            if (
                type == MessageType.System
                || serial == 0xFFFF_FFFF
                || serial == 0
                || name.ToLower() == "system" && entity == null
            )
            {
                // do nothing
            }
            else if (entity != null)
            {
                text_type = TextType.OBJECT;

                if (string.IsNullOrEmpty(entity.Name))
                {
                    world.Writer.SetName(entity, string.IsNullOrEmpty(name) ? text : name);
                }
            }

            world.MessageManager.HandleMessage(entity, text, name, hue, type, (byte)font, text_type);
        }

        private static void UnicodeTalk(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                LoginScene scene = Client.Game.GetScene<LoginScene>();

                if (scene != null)
                {
                    //Serial serial = p.ReadUInt32();
                    //ushort graphic = p.ReadUInt16();
                    //MessageType type = (MessageType)p.ReadByte();
                    //Hue hue = p.ReadUInt16();
                    //MessageFont font = (MessageFont)p.ReadUInt16();
                    //string lang = p.ReadCp1252(4);
                    //string name = p.ReadCp1252(30);
                    Log.Warn("UnicodeTalk received during LoginScene");

                    if (p.Length > 48)
                    {
                        p.Seek(48, SeekOrigin.Begin);
                        Log.PushIndent();
                        Log.Warn("Handled UnicodeTalk in LoginScene");
                        Log.PopIndent();
                    }
                }

                return;
            }

            uint serial = p.ReadUInt32();
            Entity entity = world.Get(serial);
            ushort graphic = p.ReadUInt16();
            MessageType type = (MessageType)p.ReadByte();
            ushort hue = p.ReadUInt16();
            ushort font = p.ReadUInt16();
            string lang = p.ReadCp1252(4);
            string name = p.ReadCp1252();

            if (
                serial == 0
                && graphic == 0
                && type == MessageType.Regular
                && font == 0xFFFF
                && hue == 0xFFFF
                && name.ToLower() == "system"
            )
            {
                Span<byte> buffer =
                    stackalloc byte[] {
                        0x03,
                        0x00,
                        0x28,
                        0x20,
                        0x00,
                        0x34,
                        0x00,
                        0x03,
                        0xdb,
                        0x13,
                        0x14,
                        0x3f,
                        0x45,
                        0x2c,
                        0x58,
                        0x0f,
                        0x5d,
                        0x44,
                        0x2e,
                        0x50,
                        0x11,
                        0xdf,
                        0x75,
                        0x5c,
                        0xe0,
                        0x3e,
                        0x71,
                        0x4f,
                        0x31,
                        0x34,
                        0x05,
                        0x4e,
                        0x18,
                        0x1e,
                        0x72,
                        0x0f,
                        0x59,
                        0xad,
                        0xf5,
                        0x00
                    };

                NetClient.Socket.Send(buffer);

                return;
            }

            string text = string.Empty;

            if (p.Length > 48)
            {
                p.Seek(48, SeekOrigin.Begin);
                text = p.ReadBigUni();
            }

            TextType text_type = TextType.SYSTEM;

            if (type == MessageType.Alliance || type == MessageType.Guild)
            {
                text_type = TextType.GUILD_ALLY;
            }
            else if (
                type == MessageType.System
                || serial == 0xFFFF_FFFF
                || serial == 0
                || name.ToLower() == "system" && entity == null
            )
            {
                // do nothing
            }
            else if (entity != null)
            {
                text_type = TextType.OBJECT;

                if (string.IsNullOrEmpty(entity.Name))
                {
                    world.Writer.SetName(entity, string.IsNullOrEmpty(name) ? text : name);
                }
            }

            world.MessageManager.HandleMessage(
                entity,
                text,
                name,
                hue,
                type,
                ProfileManager.CurrentProfile.ChatFont,
                text_type,
                true,
                lang
            );
        }

        private static void ChatMessage(World world, ref NetReader p)
        {
            int layout = PacketLayouts.Resolve(ref p, PacketLayouts.Chat(p.Buffer), PacketLayouts.ChatFull);
            ushort cmd = p.ReadUInt16();

            switch (cmd)
            {
                case 0x03E8: // create conference
                    SkipChatLanguage(ref p, layout);
                    string channelName = p.ReadBigUni();
                    bool hasPassword = p.Remaining > 0 && p.ReadUInt16() == 0x31;
                    world.ChatManager.CurrentChannelName = channelName;
                    world.ChatManager.AddChannel(channelName, hasPassword);

                    UIManager.GetGump<ChatGump>()?.RequestUpdateContents();

                    break;

                case 0x03E9: // destroy conference
                    SkipChatLanguage(ref p, layout);
                    channelName = p.ReadBigUni();
                    world.ChatManager.RemoveChannel(channelName);

                    UIManager.GetGump<ChatGump>()?.RequestUpdateContents();

                    break;

                case 0x03EB: // display enter username window
                    world.ChatManager.ChatIsEnabled = ChatStatus.EnabledUserRequest;

                    break;

                case 0x03EC: // close chat
                    world.ChatManager.Clear();
                    world.ChatManager.ChatIsEnabled = ChatStatus.Disabled;

                    UIManager.GetGump<ChatGump>()?.Dispose();

                    break;

                case 0x03ED: // username accepted, display chat
                    SkipChatLanguage(ref p, layout);
                    string username = p.ReadBigUni();
                    world.ChatManager.ChatIsEnabled = ChatStatus.Enabled;
                    NetClient.Socket.Send_ChatJoinCommand("General");

                    break;

                case 0x03EE: // add user
                    SkipChatLanguage(ref p, layout);
                    ushort userType = p.Remaining > 0 ? p.ReadUInt16() : (ushort)0;
                    username = p.ReadBigUni();

                    break;

                case 0x03EF: // remove user
                    SkipChatLanguage(ref p, layout);
                    username = p.ReadBigUni();

                    break;

                case 0x03F0: // clear all players
                    break;

                case 0x03F1: // you have joined a conference
                    SkipChatLanguage(ref p, layout);
                    channelName = p.ReadBigUni();
                    world.ChatManager.CurrentChannelName = channelName;

                    UIManager.GetGump<ChatGump>()?.UpdateConference();

                    GameActions.Print(
                        world,
                        string.Format(ResGeneral.YouHaveJoinedThe0Channel, channelName),
                        ProfileManager.CurrentProfile.ChatMessageHue,
                        MessageType.Regular,
                        1
                    );

                    break;

                case 0x03F4:
                    SkipChatLanguage(ref p, layout);
                    channelName = p.ReadBigUni();

                    GameActions.Print(
                        world,
                        string.Format(ResGeneral.YouHaveLeftThe0Channel, channelName),
                        ProfileManager.CurrentProfile.ChatMessageHue,
                        MessageType.Regular,
                        1
                    );

                    break;

                case 0x0025:
                case 0x0026:
                case 0x0027:
                    SkipChatLanguage(ref p, layout);
                    ushort msgType = p.Remaining > 0 ? p.ReadUInt16() : (ushort)0;
                    username = p.ReadBigUni();
                    string msgSent = p.ReadBigUni();

                    if (!string.IsNullOrEmpty(msgSent))
                    {
                        int idx = msgSent.IndexOf('{');
                        int idxLast = msgSent.IndexOf('}') + 1;

                        if (idxLast > idx && idx > -1)
                        {
                            msgSent = msgSent.Remove(idx, idxLast - idx);
                        }
                    }

                    //Color c = new Color(49, 82, 156, 0);
                    GameActions.Print(
                        world,
                        $"{username}: {msgSent}",
                        ProfileManager.CurrentProfile.ChatMessageHue,
                        MessageType.Regular,
                        1
                    );

                    break;

                default:
                    if (cmd >= 0x0001 && cmd <= 0x0024 || cmd >= 0x0028 && cmd <= 0x002C)
                    {
                        // TODO: read Chat.enu ?
                        // http://docs.polserver.com/packets/index.php?Packet=0xB2

                        string msg = ChatManager.GetMessage(cmd - 1);

                        if (string.IsNullOrEmpty(msg))
                        {
                            return;
                        }

                        SkipChatLanguage(ref p, layout);
                        string text = p.ReadBigUni();

                        if (!string.IsNullOrEmpty(text))
                        {
                            int idx = msg.IndexOf("%1");

                            if (idx >= 0)
                            {
                                msg = msg.Replace("%1", text);
                            }

                            if (cmd - 1 == 0x000A || cmd - 1 == 0x0017)
                            {
                                idx = msg.IndexOf("%2");

                                if (idx >= 0)
                                {
                                    msg = msg.Replace("%2", text);
                                }
                            }
                        }

                        GameActions.Print(
                            world,
                            msg,
                            ProfileManager.CurrentProfile.ChatMessageHue,
                            MessageType.Regular,
                            1
                        );
                    }

                    break;
            }
        }

        private static void SkipChatLanguage(ref NetReader p, int layout)
        {
            if (layout != PacketLayouts.ChatNoText)
            {
                p.Skip(4);
            }
        }

        private static void Help(World world, ref NetReader p) { }

        private static void UltimaMessengerR(World world, ref NetReader p) { }

        private static void DisplayClilocString(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            uint serial = p.ReadUInt32();
            Entity entity = world.Get(serial);
            ushort graphic = p.ReadUInt16();
            MessageType type = (MessageType)p.ReadByte();
            ushort hue = p.ReadUInt16();
            ushort font = p.ReadUInt16();
            uint cliloc = p.ReadUInt32();
            AffixType flags = p[0] == 0xCC ? (AffixType)p.ReadByte() : 0x00;
            string name = p.ReadCp1252(30);
            string affix = p[0] == 0xCC ? p.ReadCp1252() : string.Empty;

            string arguments = null;

            if (cliloc == 1008092 || cliloc == 1005445) // value for "You notify them you don't want to join the party" || "You have been added to the party"
            {
                for (LinkedListNode<Gump> g = UIManager.Gumps.Last; g != null; g = g.Previous)
                {
                    if (g.Value is PartyInviteGump pg)
                    {
                        pg.Dispose();
                    }
                }
            }

            int remains = p.Remaining;

            if (remains > 0)
            {
                if (p[0] == 0xCC)
                {
                    arguments = p.ReadBigUni(remains / 2); // remains is in bytes
                }
                else
                {
                    arguments = p.ReadLittleUni(remains / 2);
                }
            }

            string text = Client.Game.UO.FileManager.Clilocs.Translate((int)cliloc, arguments);

            if (text == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(affix))
            {
                if ((flags & AffixType.Prepend) != 0)
                {
                    text = $"{affix}{text}";
                }
                else
                {
                    text = $"{text}{affix}";
                }
            }

            if ((flags & AffixType.System) != 0)
            {
                type = MessageType.System;
            }

            if (!Client.Game.UO.FileManager.Fonts.UnicodeFontExists((byte)font))
            {
                font = 0;
            }

            TextType text_type = TextType.SYSTEM;

            if (
                serial == 0xFFFF_FFFF
                || serial == 0
                || !string.IsNullOrEmpty(name)
                    && string.Equals(name, "system", StringComparison.InvariantCultureIgnoreCase)
            )
            {
                // do nothing
            }
            else if (entity != null)
            {
                //entity.Graphic = graphic;
                text_type = TextType.OBJECT;

                if (string.IsNullOrEmpty(entity.Name))
                {
                    world.Writer.SetName(entity, name);
                }
            }
            else
            {
                if (type == MessageType.Label)
                    return;
            }

            world.MessageManager.HandleMessage(
                entity,
                text,
                name,
                hue,
                type,
                (byte)font,
                text_type,
                true
            );
        }
    }
}
