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
    }
}
