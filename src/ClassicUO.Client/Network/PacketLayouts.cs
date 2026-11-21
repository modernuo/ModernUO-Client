// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers.Binary;
using ClassicUO.Utility;

namespace ClassicUO.Network
{
    /// <summary>A packet whose length matches none of the layouts its id has (the dispatch logs it and drops the connection).</summary>
    public sealed class PacketLayoutException : Exception
    {
        public PacketLayoutException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Packets with version-dependent or optional fields take their layout from the packet's own length (the header length
    /// for variable packets, the table length for fixed ones), not from the configured client version (owner decision
    /// 2026-11-20). Each selector looks at the whole packet as the dispatch hands it over: the framing only dispatches a
    /// packet once all its bytes are queued, so a packet cut by a socket read or by the decompression boundary is never
    /// seen short here. Selectors read the bytes directly (not through the reader), so the span_reader checker sees the
    /// same reads either way. They return a layout (0 or more) or <see cref="None"/>; the version only breaks a tie where
    /// two layouts fit the same bytes. <see cref="Resolve"/> turns None into a dropped connection with span_reader on;
    /// off, the handler keeps the layout the configured version names, as it did before.
    /// </summary>
    public static class PacketLayouts
    {
        public const int None = -1;

        /// <summary>The layout to read: <paramref name="layout"/>, or with none, a throw (span_reader on) or the version's layout (off).</summary>
        public static int Resolve(ref NetReader p, int layout, int versionLayout)
        {
            if (layout >= 0)
            {
                return layout;
            }

            if (p.Mode is SpanBufferMode.New or SpanBufferMode.CheckNew)
            {
                ReadOnlySpan<byte> b = p.Buffer;

                throw new PacketLayoutException($"packet 0x{b[0]:X2} length {b.Length} matches no layout");
            }

            return versionLayout;
        }

        private static ushort U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.Slice(at));

        private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.Slice(at));

        // 0xBA quest arrow: 6 bytes, or 10 with the target serial (7.0.9+)
        public const int QuestArrowNoSerial = 0, QuestArrowSerial = 1;

        public static int QuestArrow(ReadOnlySpan<byte> packet) => packet.Length switch
        {
            6 => QuestArrowNoSerial,
            10 => QuestArrowSerial,
            _ => None
        };

        // 0x25 / 0x3C contained items: an item is 19 bytes, or 20 with the container grid index (6.0.1.7+)
        public const int ContainedNoGrid = 0, ContainedGrid = 1;

        public static int ContainedItem(ReadOnlySpan<byte> packet) => packet.Length switch
        {
            20 => ContainedNoGrid,
            21 => ContainedGrid,
            _ => None
        };

        public static int ContainedItems(ReadOnlySpan<byte> packet, ClientVersion version)
        {
            if (packet.Length < 5)
            {
                return None;
            }

            int count = U16(packet, 3);
            int body = packet.Length - 5;
            int preferred = version >= ClientVersion.CV_6017 ? ContainedGrid : ContainedNoGrid;

            if (count == 0)
            {
                return body == 0 ? preferred : None;
            }

            if (body == count * 20)
            {
                return ContainedGrid;
            }

            return body == count * 19 ? ContainedNoGrid : None;
        }

        // 0x78 / 0xD3 mobile incoming, the worn list: each item's hue is always there (7.0.33+), or only when the graphic
        // has 0x8000 set. The list ends at a zero serial or at the packet's end.
        public const int WornFlagHue = 0, WornAlwaysHue = 1;

        public static int WornList(ReadOnlySpan<byte> packet, ClientVersion version)
        {
            int start = packet.Length > 0 && packet[0] == 0x78 ? 19 : 25;
            int preferred = version >= ClientVersion.CV_70331 ? WornAlwaysHue : WornFlagHue;

            if (WornListFits(packet, start, preferred == WornAlwaysHue))
            {
                return preferred;
            }

            int other = preferred == WornAlwaysHue ? WornFlagHue : WornAlwaysHue;

            return WornListFits(packet, start, other == WornAlwaysHue) ? other : None;
        }

        private static bool WornListFits(ReadOnlySpan<byte> b, int pos, bool alwaysHue)
        {
            if (pos > b.Length)
            {
                return false;
            }

            while (true)
            {
                if (pos == b.Length)
                {
                    return true;
                }

                if (b.Length - pos < 4)
                {
                    return false;
                }

                uint serial = U32(b, pos);
                pos += 4;

                if (serial == 0)
                {
                    return true;
                }

                if (b.Length - pos < 3)
                {
                    return false;
                }

                ushort graphic = U16(b, pos);
                pos += 3;

                if (alwaysHue || (graphic & 0x8000) != 0)
                {
                    if (b.Length - pos < 2)
                    {
                        return false;
                    }

                    pos += 2;
                }
            }
        }

        // 0xB9 locked features: a 16-bit (3 bytes) or 32-bit (5 bytes, 6.0.14.2+) flag set
        public const int Features16 = 0, Features32 = 1;

        public static int LockedFeatures(ReadOnlySpan<byte> packet) => packet.Length switch
        {
            3 => Features16,
            5 => Features32,
            _ => None
        };

        // 0xF3 item (SA): 24 bytes, or 26 with the last word (7.0.9+); 0xF7 holds a list of them, each with its id byte
        public const int ItemSAShort = 0, ItemSALong = 1;

        public static int ItemSA(ReadOnlySpan<byte> packet, ClientVersion version)
        {
            if (packet.Length == 0)
            {
                return None;
            }

            if (packet[0] != 0xF7)
            {
                return packet.Length switch
                {
                    24 => ItemSAShort,
                    26 => ItemSALong,
                    _ => None
                };
            }

            if (packet.Length < 5)
            {
                return None;
            }

            int count = U16(packet, 3);
            int body = packet.Length - 5;

            if (count == 0)
            {
                return body == 0 ? (version >= ClientVersion.CV_7090 ? ItemSALong : ItemSAShort) : None;
            }

            if (body == count * 26)
            {
                return ItemSALong;
            }

            return body == count * 24 ? ItemSAShort : None;
        }

        // 0x7C menu: the item list (graphic/hue or 4 skipped bytes, then a name with its length byte) must fit; the first
        // item's graphic is peeked only when there is an item
        public const int MenuNoItems = 0, MenuItems = 1;

        public static int Menu(ReadOnlySpan<byte> b)
        {
            // id, length, serial, menu id, then the name with its length byte
            if (b.Length < 10)
            {
                return None;
            }

            int pos = 10 + b[9];

            if (b.Length < pos + 1)
            {
                return None;
            }

            int count = b[pos++];

            for (int i = 0; i < count; i++)
            {
                if (b.Length - pos < 5)
                {
                    return None;
                }

                pos += 5 + b[pos + 4];

                if (pos > b.Length)
                {
                    return None;
                }
            }

            return count == 0 ? MenuNoItems : MenuItems;
        }

        // 0xF0 (Krrios) party / guild track: entries end at a zero serial, or at the packet's end
        public const int KrriosValid = 0;

        public static int Krrios(ReadOnlySpan<byte> b)
        {
            if (b.Length < 4)
            {
                return None;
            }

            byte type = b[3];

            if (type != 0x01 && type != 0x02)
            {
                return KrriosValid;
            }

            int pos = 4;
            bool locations = true;

            if (type == 0x02)
            {
                if (b.Length < 5)
                {
                    return None;
                }

                locations = b[pos++] != 0;
            }

            int entry = !locations ? 0 : type == 0x01 ? 5 : 6;

            while (true)
            {
                if (pos == b.Length)
                {
                    return KrriosValid;
                }

                if (b.Length - pos < 4)
                {
                    return None;
                }

                uint serial = U32(b, pos);
                pos += 4;

                if (serial == 0)
                {
                    return KrriosValid;
                }

                if (b.Length - pos < entry)
                {
                    return None;
                }

                pos += entry;
            }
        }

        // 0xA9 character list: cities of 63 bytes, or 89 (7.0.13+), then the flags (absent from the oldest layout) and the
        // last-character word (7.0.13+). Layout bits: CharListNewCities, CharListFlags.
        public const int CharListNewCities = 1, CharListFlags = 2;

        public static int CharacterList(ReadOnlySpan<byte> b, ClientVersion version)
        {
            if (b.Length < 4)
            {
                return None;
            }

            int pos = 4 + b[3] * 60;

            if (b.Length < pos + 1)
            {
                return None;
            }

            int cities = b[pos++];
            bool preferNew = version >= ClientVersion.CV_70130;
            int layout = CharListCities(b.Length - pos, cities, preferNew);

            if (layout >= 0)
            {
                return layout;
            }

            return CharListCities(b.Length - pos, cities, !preferNew);
        }

        private static int CharListCities(int rest, int cities, bool newCities)
        {
            int tail = rest - cities * (newCities ? 89 : 63);
            int bits = newCities ? CharListNewCities : 0;

            return tail switch
            {
                0 => bits,
                4 or 6 => bits | CharListFlags,
                _ => None
            };
        }

        // 0xB2 chat: the language block and what follows may be absent (a command with no text); 0x03E8's password word and
        // the word after the language block of 0x03EE / 0x0025-0x0027 may end the packet early
        public const int ChatNoText = 0, ChatFull = 1, ChatNoWord = 2;

        public static int Chat(ReadOnlySpan<byte> b)
        {
            if (b.Length < 5)
            {
                return None;
            }

            ushort cmd = U16(b, 3);
            int rest = b.Length - 5;

            // commands the handler reads nothing of
            if (cmd is 0x03EB or 0x03EC or 0x03F0 || !(cmd is >= 0x0001 and <= 0x002C || cmd is >= 0x03E8 and <= 0x03F4))
            {
                return ChatFull;
            }

            if (rest == 0)
            {
                return ChatNoText;
            }

            if (rest < 4)
            {
                return None;
            }

            int pos = 9;

            switch (cmd)
            {
                case 0x03E8:
                    pos = BigUniEnd(b, pos);

                    return (b.Length - pos) switch
                    {
                        0 => ChatNoWord,
                        1 => None,
                        _ => ChatFull
                    };

                case 0x03EE:
                case 0x0025:
                case 0x0026:
                case 0x0027:
                    return (b.Length - pos) switch
                    {
                        0 => ChatNoWord,
                        1 => None,
                        _ => ChatFull
                    };

                default:
                    return ChatFull;
            }
        }

        // the position after a big-endian UTF-16 string ended by a zero char or by the packet's end (as ReadBigUni reads it)
        private static int BigUniEnd(ReadOnlySpan<byte> b, int pos)
        {
            int size = (b.Length - pos) & ~1;

            for (int i = 0; i < size; i += 2)
            {
                if (b[pos + i] == 0 && b[pos + i + 1] == 0)
                {
                    return pos + i + 2;
                }
            }

            return pos + size;
        }

        // 0xDF buff entries (from offset 11, after serial, icon and count): 29 fixed bytes, the first text block (length
        // word, two fixed chars, a string), then two more blocks (length word, string) that the packet may end before (RunUO
        // and ModernUO send one or two). Returns the blocks in the last entry (1 to 3).
        public static int BuffEntries(ReadOnlySpan<byte> b)
        {
            if (b.Length < 11)
            {
                return None;
            }

            int count = U16(b, 9);
            int pos = 11;
            int blocks = 1;

            for (int i = 0; i < count; i++)
            {
                if (b.Length - pos < 29 + 2 + 4)
                {
                    return None;
                }

                pos = LittleUniEnd(b, pos + 29 + 2 + 4);
                blocks = 1;

                for (int k = 0; k < 2 && pos < b.Length; k++)
                {
                    if (b.Length - pos < 2)
                    {
                        return None;
                    }

                    pos = LittleUniEnd(b, pos + 2);
                    blocks++;
                }
            }

            return blocks;
        }

        private static int LittleUniEnd(ReadOnlySpan<byte> b, int pos) => BigUniEnd(b, pos);

        // 0xBF.0x14 popup menu (from the reader's position after the sub-command): mode 2+ has 8-byte entries; the old mode
        // has optional words after the flags. PopupOldClassic: one each for 0x84, 0x40 and 0x20 (what this client read);
        // PopupOldColor: only 0x20 (the color word RunUO/POL send).
        public const int PopupNew = 0, PopupOldClassic = 1, PopupOldColor = 2;

        public static int Popup(ReadOnlySpan<byte> b, int start)
        {
            if (b.Length - start < 7)
            {
                return None;
            }

            if (U16(b, start) >= 2)
            {
                return b.Length - start - 7 >= b[start + 6] * 8 ? PopupNew : None;
            }

            int classic = PopupOldEnd(b, start, true);

            if (classic == b.Length)
            {
                return PopupOldClassic;
            }

            int color = PopupOldEnd(b, start, false);

            if (color == b.Length)
            {
                return PopupOldColor;
            }

            // trailing bytes after a list that fits: as before, they are not read
            return classic >= 0 ? PopupOldClassic : color >= 0 ? PopupOldColor : None;
        }

        private static int PopupOldEnd(ReadOnlySpan<byte> b, int start, bool classic)
        {
            int count = b[start + 6];
            int pos = start + 7;

            for (int i = 0; i < count; i++)
            {
                if (b.Length - pos < 6)
                {
                    return -1;
                }

                ushort flags = U16(b, pos + 4);
                pos += 6;

                if (classic && (flags & 0x84) != 0)
                {
                    pos += 2;
                }

                if (classic && (flags & 0x40) != 0)
                {
                    pos += 2;
                }

                if ((flags & 0x20) != 0)
                {
                    pos += 2;
                }

                if (pos > b.Length)
                {
                    return -1;
                }
            }

            return pos;
        }
    }
}
