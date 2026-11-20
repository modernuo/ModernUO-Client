// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.IO;
using ClassicUO.Assets;
using ClassicUO.Utility;

namespace ClassicUO.Network
{
    internal static class NetClientExt
    {
        public static void Send_ACKTalk(this NetClient socket)
        {
            const byte ID = 0x03;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x20);
            writer.Write((byte)0x00);
            writer.Write((byte)0x34);
            writer.Write((byte)0x00);
            writer.Write((byte)0x03);
            writer.Write((byte)0xdb);
            writer.Write((byte)0x13);
            writer.Write((byte)0x14);
            writer.Write((byte)0x3f);
            writer.Write((byte)0x45);
            writer.Write((byte)0x2c);
            writer.Write((byte)0x58);
            writer.Write((byte)0x0f);
            writer.Write((byte)0x5d);
            writer.Write((byte)0x44);
            writer.Write((byte)0x2e);
            writer.Write((byte)0x50);
            writer.Write((byte)0x11);
            writer.Write((byte)0xdf);
            writer.Write((byte)0x75);
            writer.Write((byte)0x5c);
            writer.Write((byte)0xe0);
            writer.Write((byte)0x3e);
            writer.Write((byte)0x71);
            writer.Write((byte)0x4f);
            writer.Write((byte)0x31);
            writer.Write((byte)0x34);
            writer.Write((byte)0x05);
            writer.Write((byte)0x4e);
            writer.Write((byte)0x18);
            writer.Write((byte)0x1e);
            writer.Write((byte)0x72);
            writer.Write((byte)0x0f);
            writer.Write((byte)0x59);
            writer.Write((byte)0xad);
            writer.Write((byte)0xf5);
            writer.Write((byte)0x00);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);

            writer.Dispose();
        }

        public static void Send_Ping(this NetClient socket, byte idx)
        {
            const byte ID = 0x73;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)idx);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);

            writer.Dispose();
        }

        public static void Send_DoubleClick(this NetClient socket, uint serial)
        {
            const byte ID = 0x06;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);

            writer.Dispose();
        }

        public static void Send_Seed
        (
            this NetClient socket,
            uint v,
            byte major,
            byte minor,
            byte build,
            byte extra
        )
        {
            const byte ID = 0xEF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)v);
            writer.Write((uint)major);
            writer.Write((uint)minor);
            writer.Write((uint)build);
            writer.Write((uint)extra);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten, skipEncryption: true);

            writer.Dispose();
        }

        public static void Send_Seed_Old(this NetClient socket, uint v)
        {
            var writer = new NetWriter(4);
            writer.Write((uint)v);

            socket.Send(writer.BufferWritten, skipEncryption: true);

            writer.Dispose();
        }

        public static void Send_FirstLogin(this NetClient socket, string user, string psw)
        {
            const byte ID = 0x80;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.WriteCp1252(user, 30);
            writer.WriteCp1252(psw, 30);
            writer.Write((byte)0xFF);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);

            writer.Dispose();
        }

        public static void Send_SelectServer(this NetClient socket, byte index)
        {
            const byte ID = 0xA0;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x00);
            writer.Write((byte)index);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);

            writer.Dispose();
        }

        public static void Send_SecondLogin(this NetClient socket, string user, string psw, uint seed)
        {
            const byte ID = 0x91;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)seed);
            writer.WriteCp1252(user, 30);
            writer.WriteCp1252(psw, 30);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);

            writer.Dispose();
        }

        public static void Send_CreateCharacter
        (
            this NetClient socket,
            PlayerMobile character,
            string name,
            PlayerState stats,
            int cityIndex,
            uint clientIP,
            int serverIndex,
            uint slot,
            byte profession
        )
        {
            const byte ID = 0x00;
            const byte ID_NEW = 0xF8;

            byte id = ID;
            int skillcount = 3;

            if (Client.Game.UO.Version >= ClientVersion.CV_70160)
            {
                id = ID_NEW;
                ++skillcount;
            }

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)id);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)0xEDEDEDED);
            writer.Write((uint)(0xFFFF_FFFF));
            writer.Write((byte)0x00);
            writer.WriteCp1252(name, 30);
            writer.WriteZero(2);

            writer.Write((uint)Client.Game.UO.Protocol);
            writer.Write((uint)0x01);
            writer.Write((uint)0x00);
            writer.Write((byte)profession);
            writer.WriteZero(15);

            byte val;

            if (Client.Game.UO.Version < ClientVersion.CV_4011D)
            {
                val = (byte) (character.Flags.HasFlag(Flags.Female) ? 0x01 : 0x00);
            }
            else
            {
                val = (byte) character.Race;

                if (Client.Game.UO.Version < ClientVersion.CV_7000)
                {
                    val--;
                }

                val = (byte) (val * 2 + (byte) (character.Flags.HasFlag(Flags.Female) ? 0x01 : 0x00));
            }

            writer.Write((byte)val);
            writer.Write((byte)stats.Strength);
            writer.Write((byte)stats.Dexterity);
            writer.Write((byte)stats.Intelligence);

            List<Skill> skills = stats.Skills.OrderByDescending(o => o.Value).Take(skillcount).ToList();

            foreach (Skill skill in skills)
            {
                writer.Write((byte)skill.Index);
                writer.Write((byte)skill.ValueFixed);
            }

            writer.Write((ushort)character.Hue);

            Item hair = character.FindItemByLayer(Layer.Hair);

            if (hair != null)
            {
                writer.Write((ushort)hair.Graphic);
                writer.Write((ushort)hair.Hue);
            }
            else
            {
                writer.WriteZero(2 * 2);
            }

            Item beard = character.FindItemByLayer(Layer.Beard);

            if (beard != null)
            {
                writer.Write((ushort)beard.Graphic);
                writer.Write((ushort)beard.Hue);
            }
            else
            {
                writer.WriteZero(2 * 2);
            }

            writer.Write((ushort)cityIndex);
            writer.WriteZero(2);
            writer.Write((ushort)slot);
            writer.Write((uint)clientIP);

            Item shirt = character.FindItemByLayer(Layer.Shirt);

            if (shirt != null)
            {
                writer.Write((ushort)shirt.Hue);
            }
            else
            {
                writer.WriteZero(2);
            }

            Item pants = character.FindItemByLayer(Layer.Pants);

            if (pants != null)
            {
                writer.Write((ushort)pants.Hue);
            }
            else
            {
                writer.WriteZero(2);
            }

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);

            writer.Dispose();
        }

        public static void Send_DeleteCharacter(this NetClient socket, byte index, uint ipclient)
        {
            const byte ID = 0x83;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.WriteZero(30);
            writer.Write((uint)index);
            writer.Write((uint)ipclient);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);

            writer.Dispose();
        }

        public static void Send_SelectCharacter(this NetClient socket, uint index, string name, uint ipclient)
        {
            const byte ID = 0x5D;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)0xEDEDEDED);
            writer.WriteCp1252(name, 30);
            writer.WriteZero(2);
            writer.Write((uint)Client.Game.UO.Protocol);
            writer.WriteZero(24);
            writer.Write((uint)index);
            writer.Write((uint)ipclient);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);

            writer.Dispose();
        }

        public static void Send_PickUpRequest(this NetClient socket, uint serial, ushort count)
        {
            const byte ID = 0x07;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((ushort)count);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_DropRequest_Old
        (
            this NetClient socket,
            uint serial,
            ushort x,
            ushort y,
            sbyte z,
            uint container
        )
        {
            const byte ID = 0x08;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((ushort)x);
            writer.Write((ushort)y);
            writer.Write((sbyte)z);
            writer.Write((uint)container);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_DropRequest
        (
            this NetClient socket,
            uint serial,
            ushort x,
            ushort y,
            sbyte z,
            byte slot,
            uint container
        )
        {
            const byte ID = 0x08;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((ushort)x);
            writer.Write((ushort)y);
            writer.Write((sbyte)z);
            writer.Write((byte)slot);
            writer.Write((uint)container);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_EquipRequest(this NetClient socket, uint serial, Layer layer, uint container)
        {
            const byte ID = 0x13;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((byte)layer);
            writer.Write((uint)container);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ChangeWarMode(this NetClient socket, bool state)
        {
            const byte ID = 0x72;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write(state);
            writer.Write((byte)0x32);
            writer.Write((byte)0x00);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_HelpRequest(this NetClient socket)
        {
            const byte ID = 0x9B;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.WriteZero(257);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_StatusRequest(this NetClient socket, uint serial)
        {
            const byte ID = 0x34;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)0xEDEDEDED);
            writer.Write((byte)0x04);
            writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_SkillsRequest(this NetClient socket, uint serial)
        {
            const byte ID = 0x34;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)0xEDEDEDED);
            writer.Write((byte)0x05);
            writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_SkillsStatusRequest(this NetClient socket, ushort skillIndex, byte lockState)
        {
            const byte ID = 0x3A;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)skillIndex);
            writer.Write((byte)lockState);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ClickRequest(this NetClient socket, uint serial)
        {
            const byte ID = 0x09;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);
            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_AttackRequest(this NetClient socket, uint serial)
        {
            const byte ID = 0x05;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ClientVersion(this NetClient socket, string version)
        {
            const byte ID = 0xBD;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.WriteCp1252Null(version);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ASCIISpeechRequest(this NetClient socket, string text, MessageType type, byte font, ushort hue)
        {
            const byte ID = 0x03;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            List<SpeechEntry> entries = Client.Game.UO.FileManager.Speeches.GetKeywords(text);
            bool encoded = entries != null && entries.Count != 0;

            if (encoded)
            {
                type |= MessageType.Encoded;
            }

            writer.Write((byte)type);
            writer.Write((ushort)hue);
            writer.Write((ushort)font);
            writer.WriteCp1252Null(text);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_UnicodeSpeechRequest
        (
            this NetClient socket,
            string text,
            MessageType type,
            byte font,
            ushort hue,
            string lang
        )
        {
            const byte ID = 0xAD;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            List<SpeechEntry> entries = Client.Game.UO.FileManager.Speeches.GetKeywords(text);
            bool encoded = entries != null && entries.Count != 0;

            if (encoded)
            {
                type |= MessageType.Encoded;
            }

            writer.Write((byte)type);
            writer.Write((ushort)hue);
            writer.Write((ushort)font);
            writer.WriteCp1252(lang, 4);

            if (encoded)
            {
                List<byte> codeBytes = new List<byte>();
                byte[] utf8 = Encoding.UTF8.GetBytes(text);
                int len = entries.Count;
                codeBytes.Add((byte) (len >> 4));
                int num3 = len & 15;
                bool flag = false;
                int index = 0;

                while (index < len)
                {
                    int keywordID = entries[index].KeywordID;

                    if (flag)
                    {
                        codeBytes.Add((byte) (keywordID >> 4));
                        num3 = keywordID & 15;
                    }
                    else
                    {
                        codeBytes.Add((byte) ((num3 << 4) | ((keywordID >> 8) & 15)));
                        codeBytes.Add((byte) keywordID);
                    }

                    index++;
                    flag = !flag;
                }

                if (!flag)
                {
                    codeBytes.Add((byte) (num3 << 4));
                }

                for (int i = 0; i < codeBytes.Count; ++i)
                {
                    writer.Write((byte)(codeBytes[i]));
                }

                writer.Write(utf8);
                writer.Write((byte)0x00);
            }
            else
            {
                writer.WriteBigUniNull(text);
            }

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CastSpell(this NetClient socket, int idx)
        {
            const byte ID = 0xBF;
            const byte ID_OLD = 0x12;

            byte id = ID;

            if (Client.Game.UO.Version < ClientVersion.CV_60142)
            {
                id = ID_OLD;
            }

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)id);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            if (Client.Game.UO.Version >= ClientVersion.CV_60142)
            {
                writer.Write((ushort)0x1C);
                writer.Write((ushort)0x02);
                writer.Write((ushort)idx);
            }
            else
            {
                writer.Write((byte)0x56);
                writer.WriteCp1252Null(idx.ToString());
            }

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CastSpellFromBook(this NetClient socket, int idx, uint serial)
        {
            const byte ID = 0x12;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x27);
            writer.WriteCp1252Null($"{idx} {serial}");

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_UseSkill(this NetClient socket, int idx)
        {
            const byte ID = 0x12;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x24);
            writer.WriteCp1252Null($"{idx} 0");

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_OpenDoor(this NetClient socket)
        {
            const byte ID = 0x12;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x58);
            writer.Write((byte)0x00);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_OpenSpellBook(this NetClient socket, byte type)
        {
            const byte ID = 0x12;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x43);
            writer.Write((byte)type);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_EmoteAction(this NetClient socket, string action)
        {
            const byte ID = 0x12;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0xC7);
            writer.WriteCp1252Null(action);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_GumpResponse
        (
            this NetClient socket,
            uint local,
            uint server,
            int button,
            uint[] switches,
            Tuple<ushort, string>[] entries
        )
        {
            const byte ID = 0xB1;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)local);
            writer.Write((uint)server);
            writer.Write((uint)button);

            writer.Write((uint)switches.Length);

            for (int i = 0; i < switches.Length; ++i)
            {
                writer.Write((uint)(switches[i]));
            }

            writer.Write((uint)entries.Length);

            for (int i = 0; i < entries.Length; ++i)
            {
                int len = Math.Min(239, entries[i].Item2.Length);

                writer.Write((ushort)(entries[i].Item1));
                writer.Write((ushort)len);
                writer.WriteBigUni(entries[i].Item2, len);
            }

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_VirtueGumpResponse(this NetClient socket, uint serial, uint code)
        {
            const byte ID = 0xB1;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((uint)0x000001CD);
            writer.Write((uint)code);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_MenuResponse
        (
            this NetClient socket,
            uint serial,
            ushort graphic,
            int code,
            ushort itemGraphic,
            ushort itemHue
        )
        {
            const byte ID = 0x7D;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((ushort)graphic);

            if (code != 0)
            {
                writer.Write((ushort)code);
                writer.Write((ushort)itemGraphic);
                writer.Write((ushort)itemHue);
            }

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_GrayMenuResponse(this NetClient socket, uint serial, ushort graphic, ushort code)
        {
            const byte ID = 0x7D;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((ushort)graphic);
            writer.Write((ushort)code);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_TradeResponse(this NetClient socket, uint serial, int code, bool state)
        {
            const byte ID = 0x6F;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            if (code == 1)
            {
                writer.Write((byte)0x01);
                writer.Write((uint)serial);
            }
            else if (code == 2)
            {
                writer.Write((byte)0x02);
                writer.Write((uint)serial);
                writer.Write((uint)(state ? 1 : 0));
            }
            else
            {
                writer.Dispose();

                return;
            }

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_TradeUpdateGold(this NetClient socket, uint serial, uint gold, uint platinum)
        {
            const byte ID = 0x6F;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x03);
            writer.Write((uint)serial);
            writer.Write((uint)gold);
            writer.Write((uint)platinum);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_LogoutNotification(this NetClient socket)
        {
            const byte ID = 0xD1;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x00);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_TextEntryDialogResponse
        (
            this NetClient socket,
            uint serial,
            byte parentID,
            byte button,
            string text,
            bool code
        )
        {
            const byte ID = 0xAC;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((byte)parentID);
            writer.Write((byte)button);
            writer.Write(code);
            writer.Write((ushort)(text.Length + 1));
            writer.WriteCp1252(text, text.Length + 1);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_RenameRequest(this NetClient socket, uint serial, string name)
        {
            const byte ID = 0x75;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.WriteCp1252(name, 30);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_NameRequest(this NetClient socket, uint serial)
        {
            const byte ID = 0x98;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_TipRequest(this NetClient socket, ushort id, byte flag)
        {
            const byte ID = 0xA7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)id);
            writer.Write((byte)flag);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_TargetObject
        (
            this NetClient socket,
            uint entity,
            ushort graphic,
            ushort x,
            ushort y,
            sbyte z,
            uint cursorID,
            byte cursorType
        )
        {
            const byte ID = 0x6C;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x00);
            writer.Write((uint)cursorID);
            writer.Write((byte)cursorType);
            writer.Write((uint)entity);
            writer.Write((ushort)x);
            writer.Write((ushort)y);
            writer.Write((ushort)z);
            writer.Write((ushort)graphic);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_TargetXYZ
        (
            this NetClient socket,
            ushort graphic,
            ushort x,
            ushort y,
            sbyte z,
            uint cursorID,
            byte cursorType
        )
        {
            const byte ID = 0x6C;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x01);
            writer.Write((uint)cursorID);
            writer.Write((byte)cursorType);
            writer.Write((uint)0x00);
            writer.Write((ushort)x);
            writer.Write((ushort)y);
            writer.Write((ushort)z);
            writer.Write((ushort)graphic);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_TargetCancel(this NetClient socket, CursorTarget type, uint cursorID, byte cursorType)
        {
            const byte ID = 0x6C;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)type);
            writer.Write((uint)cursorID);
            writer.Write((byte)cursorType);
            writer.Write((uint)0x00);
            writer.Write((uint)(0xFFFF_FFFF));
            writer.Write((uint)(0x0000_0000));

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ASCIIPromptResponse(this NetClient socket, World world, string text, bool cancel)
        {
            const byte ID = 0x9A;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ulong)world.MessageManager.PromptData.Data);
            writer.Write((uint)(cancel ? 0 : 1));
            writer.WriteCp1252Null(text);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_UnicodePromptResponse(this NetClient socket, World world, string text, string lang, bool cancel)
        {
            const byte ID = 0xC2;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ulong)world.MessageManager.PromptData.Data);
            writer.Write((uint)(cancel ? 0 : 1));
            writer.WriteCp1252(lang, 3);
            writer.Write((byte)0x00);
            writer.WriteLittleUni(text, text.Length);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_DyeDataResponse(this NetClient socket, uint serial, ushort graphic, ushort hue)
        {
            const byte ID = 0x95;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((ushort)0);
            writer.Write((ushort)hue);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ProfileRequest(this NetClient socket, uint serial)
        {
            const byte ID = 0xB8;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x00);
            writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ProfileUpdate(this NetClient socket, uint serial, string text)
        {
            const byte ID = 0xB8;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x01);
            writer.Write((uint)serial);
            writer.Write((ushort)0x01);
            writer.Write((ushort)text.Length);
            writer.WriteBigUni(text, text.Length);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ClickQuestArrow(this NetClient socket, bool righClick)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x07);
            writer.Write(righClick);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CloseStatusBarGump(this NetClient socket, uint serial)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x0C);
            writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_PartyInviteRequest(this NetClient socket)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x06);
            writer.Write((byte)1);
            writer.Write((uint)0);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_PartyRemoveRequest(this NetClient socket, uint serial)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x06);
            writer.Write((byte)2);
            writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_PartyChangeLootTypeRequest(this NetClient socket, bool type)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x06);
            writer.Write((byte)0x06);
            writer.Write(type);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_PartyAccept(this NetClient socket, uint serial)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x06);
            writer.Write((byte)0x08);
            writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_PartyDecline(this NetClient socket, uint serial)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x06);
            writer.Write((byte)0x09);
            writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_PartyMessage(this NetClient socket, string text, uint serial)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }


            writer.Write((ushort)0x06);

            if (SerialHelper.IsValid(serial))
            {
                writer.Write((byte)0x03);
                writer.Write((uint)serial);
            }
            else
            {
                writer.Write((byte)0x04);
            }

            writer.WriteBigUniNull(text);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_GameWindowSize(this NetClient socket, uint w, uint h)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x05);
            writer.Write((uint)w);
            writer.Write((uint)h);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_BulletinBoardRequestMessage(this NetClient socket, uint serial, uint msgSerial)
        {
            const byte ID = 0x71;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x03);
            writer.Write((uint)serial);
            writer.Write((uint)msgSerial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_BulletinBoardRequestMessageSummary(this NetClient socket, uint serial, uint msgSerial)
        {
            const byte ID = 0x71;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x04);
            writer.Write((uint)serial);
            writer.Write((uint)msgSerial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_BulletinBoardPostMessage(this NetClient socket, uint serial, uint msgSerial, string subject, string text)
        {
            const byte ID = 0x71;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            // cleanup windows CR
            subject = subject.Replace("\r\n", "\n");
            text = text.Replace("\r\n", "\n");

            writer.Write((byte)0x05);
            writer.Write((uint)serial);
            writer.Write((uint)msgSerial);
            writer.Write((byte)(subject.Length + 1));

            byte[] title = Encoding.UTF8.GetBytes(subject);
            writer.Write(title);
            writer.Write((byte)0x00);

            var lines = text.Split('\n');

            if (lines.Length > 0)
            {
                writer.Write((byte)lines.Length);

                foreach (var line in lines)
                {
                    var bytes = Encoding.UTF8.GetBytes(line);
                    writer.Write((byte)(bytes.Length + 1));
                    writer.Write(bytes.AsSpan());
                    writer.Write((byte)0x00);
                }
            }
            else
            {
                writer.Write((byte)0x01);
                var bytes = Encoding.UTF8.GetBytes(text);
                writer.Write((byte)(bytes.Length + 1));
                writer.Write(bytes.AsSpan());
                writer.Write((byte)0x00);
            }


            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_BulletinBoardRemoveMessage(this NetClient socket, uint serial, uint msgSerial)
        {
            const byte ID = 0x71;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x06);
            writer.Write((uint)serial);
            writer.Write((uint)msgSerial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_RazorACK(this NetClient socket)
        {
            const byte ID = 0xF0;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0xFF);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_QueryGuildPosition(this NetClient socket)
        {
            const byte ID = 0xF0;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x01);
            writer.Write(true);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_QueryPartyPosition(this NetClient socket)
        {
            const byte ID = 0xF0;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x00);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_Language(this NetClient socket, string lang)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x0B);
            ClassicUO.Utility.Logging.Log.Info($"language packet: sending '{lang}'");
            writer.WriteCp1252(lang, 3);
            writer.Write((byte)0x00);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ClientType(this NetClient socket)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x0F);
            writer.Write((byte)0x0A);

            uint clientFlag = 0;

            for (int i = 0; i < (uint) Client.Game.UO.Protocol; ++i)
            {
                clientFlag |= (uint) (1 << i);
            }


            writer.Write((uint)clientFlag);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_RequestPopupMenu(this NetClient socket, uint serial)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x13);
            writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_PopupMenuSelection(this NetClient socket, uint serial, ushort menuid)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x15);
            writer.Write((uint)serial);
            writer.Write((ushort)menuid);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ChatJoinCommand(this NetClient socket, string name, string password = null)
        {
            const byte ID = 0xB3;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.WriteCp1252(Settings.GlobalSettings.Language, 4);
            writer.Write((ushort)0x62);
            writer.Write((ushort)0x22);
            writer.WriteBigUniNull(name);
            writer.Write((ushort)0x22);
            writer.Write((ushort)0x020);

            if (!string.IsNullOrEmpty(password))
            {
                writer.WriteBigUniNull(password);
            }

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ChatCreateChannelCommand(this NetClient socket, string name, string password = null)
        {
            const byte ID = 0xB3;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.WriteCp1252(Settings.GlobalSettings.Language, 4);
            writer.Write((ushort)0x63);
            writer.WriteBigUniNull(name);

            if (!string.IsNullOrEmpty(password))
            {
                writer.Write((ushort)0x7B);
                writer.WriteBigUniNull(password);
                writer.Write((ushort)0x07D);
            }

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ChatLeaveChannelCommand(this NetClient socket)
        {
            const byte ID = 0xB3;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.WriteCp1252(Settings.GlobalSettings.Language, 4);
            writer.Write((ushort)0x43);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ChatMessageCommand(this NetClient socket, string msg)
        {
            const byte ID = 0xB3;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.WriteCp1252(Settings.GlobalSettings.Language, 4);
            writer.Write((ushort)0x61);
            writer.WriteBigUniNull(msg);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_OpenChat(this NetClient socket, string name)
        {
            const byte ID = 0xB5;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }


            writer.Write((byte)0x00);
            int len = Math.Min(name.Length, 30);

            if (len > 0)
            {
                writer.WriteBigUni(name, len);
            }

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_MapMessage
        (
            this NetClient socket,
            uint serial,
            byte action,
            byte pin,
            ushort x,
            ushort y
        )
        {
            const byte ID = 0x56;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((byte)action);
            writer.Write((byte)pin);
            writer.Write((ushort)x);
            writer.Write((ushort)y);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_GuildMenuRequest(this NetClient socket, World world)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x28);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_QuestMenuRequest(this NetClient socket, World world)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x32);
            writer.Write((byte)0x00);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_EquipLastWeapon(this NetClient socket, World world)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x1E);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_InvokeVirtueRequest(this NetClient socket, byte id)
        {
            const byte ID = 0x12;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0xF4);
            writer.WriteCp1252Null(id.ToString());


            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_MegaClilocRequest_Old(this NetClient socket, uint serial)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x10);
            writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        // a batch of up to 15 serials (OplRequestQueue, OplRequestList)
        public static void Send_MegaClilocRequest(this NetClient socket, ReadOnlySpan<uint> serials)
        {
            const byte ID = 0xD6;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            int count = Math.Min(15, serials.Length);

            for (int i = 0; i < count; ++i)
            {
                writer.Write((uint)(serials[i]));
            }

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_StatLockStateRequest(this NetClient socket, byte stat, Lock state)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x1A);
            writer.Write((byte)stat);
            writer.Write((byte)state);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_SkillStatusChangeRequest(this NetClient socket, ushort skillindex, byte lockstate)
        {
            const byte ID = 0x3A;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)skillindex);
            writer.Write((byte)lockstate);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_BookHeaderChanged_Old(this NetClient socket, uint serial, string title, string author)
        {
            const byte ID = 0x93;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((byte)0x00);
            writer.Write((byte)0x01);
            writer.Write((ushort)0);
            writer.WriteUTF8(title, 60);
            writer.WriteUTF8(author, 30);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_BookHeaderChanged(this NetClient socket, uint serial, string title, string author)
        {
            const byte ID = 0xD4;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((byte)0x00);
            writer.Write((byte)0x00);
            writer.Write((ushort)0);
            int titleLength = Encoding.UTF8.GetByteCount(title);
            writer.Write((ushort)titleLength);
            writer.WriteUTF8(title, titleLength);
            int authorLength = Encoding.UTF8.GetByteCount(author);
            writer.Write((ushort)authorLength);
            writer.WriteUTF8(author, authorLength);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_BookPageData(this NetClient socket, uint serial, string[] lines, int page)
        {
            const byte ID = 0x66;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((ushort)0x01);
            writer.Write((ushort)page);
            writer.Write((ushort)lines.Length);

            for (int i = 0; i < lines.Length; ++i)
            {
                if (!string.IsNullOrEmpty(lines[i]))
                {
                    string t = lines[i].Replace("\n", "");

                    if (t.Length > 0)
                    {
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(t.Length * 2);//we have to assume we are using all two byte chars

                        try
                        {
                            int written = Encoding.UTF8.GetBytes
                            (
                                t,
                                0,
                                t.Length,
                                buffer,
                                0
                            );

                            writer.Write(buffer.AsSpan(0, written));
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                    }
                }

                writer.Write((byte)0x00);
            }

            writer.Write((byte)0x00);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_BookPageDataRequest(this NetClient socket, uint serial, ushort page)
        {
            const byte ID = 0x66;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((ushort)0x01);
            writer.Write((ushort)page);
            writer.Write((ushort)0xFFFF);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_BuyRequest(this NetClient socket, uint serial, Tuple<uint, ushort>[] items)
        {
            const byte ID = 0x3B;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);

            if (items.Length > 0)
            {
                writer.Write((byte)0x02);

                for (int i = 0; i < items.Length; ++i)
                {
                    writer.Write((byte)0x1A);
                    writer.Write((uint)(items[i].Item1));
                    writer.Write((ushort)(items[i].Item2));
                }
            }
            else
            {
                writer.Write((byte)0x00);
            }


            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_SellRequest(this NetClient socket, uint serial, Tuple<uint, ushort>[] items)
        {
            const byte ID = 0x9F;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)serial);
            writer.Write((ushort)items.Length);

            for (int i = 0; i < items.Length; ++i)
            {
                writer.Write((uint)(items[i].Item1));
                writer.Write((ushort)(items[i].Item2));
            }


            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_UseCombatAbility(this NetClient socket, World world, byte idx)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x19);
            writer.Write((uint)0);
            writer.Write((byte)idx);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_TargetSelectedObject(this NetClient socket, uint serial, uint targetSerial)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x2C);
            writer.Write((uint)serial);
            writer.Write((uint)targetSerial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ToggleGargoyleFlying(this NetClient socket)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x32);
            writer.Write((ushort)0x01);
            writer.Write((uint)0);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseDataRequest(this NetClient socket, uint serial)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x1E);
            writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_StunRequest(this NetClient socket)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x09);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_DisarmRequest(this NetClient socket)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ChangeRaceRequest
        (
            this NetClient socket,
            ushort skinHue,
            ushort hairStyle,
            ushort hairHue,
            ushort beardStyle,
            ushort beardHue
        )
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x2A);
            writer.Write((ushort)skinHue);
            writer.Write((ushort)hairStyle);
            writer.Write((ushort)hairHue);
            writer.Write((ushort)beardStyle);
            writer.Write((ushort)beardHue);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_MultiBoatMoveRequest(this NetClient socket, uint serial, Direction dir, byte speed)
        {
            const byte ID = 0xBF;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((ushort)0x33);
            writer.Write((uint)serial);
            writer.Write((byte)dir);
            writer.Write((byte)dir);
            writer.Write((byte)speed);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_Resync(this NetClient socket)
        {
            const byte ID = 0x22;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }


            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_WalkRequest(this NetClient socket, Direction direction, byte seq, bool run, uint fastWalk)
        {
            const byte ID = 0x02;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            if (run)
            {
                direction |= Direction.Running;
            }

            writer.Write((byte)direction);
            writer.Write((byte)seq);
            writer.Write((uint)fastWalk);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseBackup(this NetClient socket, World world)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x02);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseRestore(this NetClient socket, World world)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x03);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseCommit(this NetClient socket, World world)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x04);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseBuildingExit(this NetClient socket, World world)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x0C);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseGoToFloor(this NetClient socket, World world, byte floor)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x12);
            writer.Write((uint)0);
            writer.Write((byte)floor);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseSync(this NetClient socket, World world)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x0E);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseClear(this NetClient socket, World world)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x10);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }


            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseRevert(this NetClient socket, World world)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x1A);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseResponse(this NetClient socket, World world)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x0A);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseAddItem(this NetClient socket, World world, ushort graphic, int x, int y)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x06);
            writer.Write((byte)0x00);
            writer.Write((uint)graphic);
            writer.Write((byte)0x00);
            writer.Write((uint)x);
            writer.Write((byte)0x00);
            writer.Write((uint)y);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseDeleteItem(this NetClient socket, World world, ushort graphic, int x, int y, int z)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x05);
            writer.Write((byte)0x00);
            writer.Write((uint)graphic);
            writer.Write((byte)0x00);
            writer.Write((uint)x);
            writer.Write((byte)0x00);
            writer.Write((uint)y);
            writer.Write((byte)0x00);
            writer.Write((uint)z);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseAddRoof(this NetClient socket, World world, ushort graphic, int x, int y, int z)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x13);
            writer.Write((byte)0x00);
            writer.Write((uint)graphic);
            writer.Write((byte)0x00);
            writer.Write((uint)x);
            writer.Write((byte)0x00);
            writer.Write((uint)y);
            writer.Write((byte)0x00);
            writer.Write((uint)z);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseDeleteRoof(this NetClient socket, World world, ushort graphic, int x, int y, int z)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x14);
            writer.Write((byte)0x00);
            writer.Write((uint)graphic);
            writer.Write((byte)0x00);
            writer.Write((uint)x);
            writer.Write((byte)0x00);
            writer.Write((uint)y);
            writer.Write((byte)0x00);
            writer.Write((uint)z);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_CustomHouseAddStair(this NetClient socket, World world, ushort graphic, int x, int y)
        {
            const byte ID = 0xD7;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)world.Player.Serial);
            writer.Write((ushort)0x0D);
            writer.Write((byte)0x00);
            writer.Write((uint)graphic);
            writer.Write((byte)0x00);
            writer.Write((uint)x);
            writer.Write((byte)0x00);
            writer.Write((uint)y);
            writer.Write((byte)0x0A);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ClientViewRange(this NetClient socket, byte range)
        {
            const byte ID = 0xC8;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            if (range < Constants.MIN_VIEW_RANGE)
            {
                range = Constants.MIN_VIEW_RANGE;
            }
            else if (range > Constants.MAX_VIEW_RANGE)
            {
                range = Constants.MAX_VIEW_RANGE;
            }

            writer.Write((byte)range);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_OpenUOStore(this NetClient socket)
        {
            const byte ID = 0xFA;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_ShowPublicHouseContent(this NetClient socket, bool show)
        {
            const byte ID = 0xFB;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write(show);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_DeathScreen(this NetClient socket)
        {
            const byte ID = 0x2C;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)0x02); // Ghost

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_EquipMacroKR(this NetClient socket, ReadOnlySpan<uint> serials)
        {
            const byte ID = 0xEC;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)serials.Length);
            foreach (ref readonly var serial in serials)
                writer.Write((uint)serial);

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_UnequipMacroKR(this NetClient socket, ReadOnlySpan<Layer> layers)
        {
            const byte ID = 0xED;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((byte)layers.Length);
            foreach (ref readonly var layer in layers)
                writer.Write((ushort)((byte)layer));

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }

        public static void Send_UOLive_HashResponse(this NetClient socket, uint block, byte mapIndex, Span<ushort> checksums)
        {
            const byte ID = 0x3F;

            int length = socket.PacketsTable.GetPacketLength(ID);

            var writer = new NetWriter(length < 0 ? 64 : length);

            writer.Write((byte)ID);

            if (length < 0)
            {
                writer.WriteZero(2);
            }

            writer.Write((uint)block);
            writer.WriteZero(6);
            writer.Write((byte)0xFF);
            writer.Write((byte)mapIndex);

            for (int i = 0; i < checksums.Length; ++i)
            {
                writer.Write((ushort)(checksums[i]));
            }

            if (length < 0)
            {
                writer.Seek(1, SeekOrigin.Begin);
                writer.Write((ushort)writer.BytesWritten);
            }
            else
            {
                writer.WriteZero(length - writer.BytesWritten);
            }

            socket.Send(writer.BufferWritten);
            writer.Dispose();
        }
    }
}
