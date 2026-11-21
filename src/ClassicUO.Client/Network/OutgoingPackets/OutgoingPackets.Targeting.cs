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
    }
}
