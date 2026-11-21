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
    internal static partial class NetClientExt
    {
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
    }
}
