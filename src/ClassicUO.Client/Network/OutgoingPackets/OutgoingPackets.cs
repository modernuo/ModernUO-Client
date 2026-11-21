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
