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
    }
}
