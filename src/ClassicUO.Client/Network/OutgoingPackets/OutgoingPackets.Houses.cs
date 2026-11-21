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
    }
}
