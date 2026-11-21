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
    }
}
