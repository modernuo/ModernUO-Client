// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Network;

namespace ClassicUO.Game.Data
{
    internal class PopupMenuData
    {
        public PopupMenuData(uint serial, PopupMenuItem[] items)
        {
            Serial = serial;
            Items = items;
        }

        public PopupMenuItem[] Items { get; }

        public uint Serial { get; }

        public PopupMenuItem this[int i] => Items[i];

        public static PopupMenuData Parse(ref NetReader p)
        {
            // the old layout's optional words: this client's rule (0x84, 0x40, 0x20) or only the color word (0x20), whichever
            // the packet's length fits
            int layout = PacketLayouts.Resolve(ref p, PacketLayouts.Popup(p.Buffer, p.Position), PacketLayouts.PopupOldClassic);
            ushort mode = p.ReadUInt16();
            bool isNewCliloc = mode >= 2;
            bool classic = layout != PacketLayouts.PopupOldColor;
            uint serial = p.ReadUInt32();
            byte count = p.ReadByte();
            PopupMenuItem[] items = new PopupMenuItem[count];

            for (int i = 0; i < count; i++)
            {
                ushort hue = 0xFFFF, replaced = 0;
                int cliloc;
                ushort index, flags;

                if (isNewCliloc)
                {
                    cliloc = (int) p.ReadUInt32();
                    index = p.ReadUInt16();
                    flags = p.ReadUInt16();
                }
                else
                {
                    index = p.ReadUInt16();
                    cliloc = p.ReadUInt16() + 3000000;
                    flags = p.ReadUInt16();

                    if (classic && (flags & 0x84) != 0)
                    {
                        p.Skip(2);
                    }

                    if (classic && (flags & 0x40) != 0)
                    {
                        p.Skip(2);
                    }

                    if ((flags & 0x20) != 0)
                    {
                        replaced = (ushort) (p.ReadUInt16() );
                    }
                }

                if ((flags & 0x01) != 0)
                {
                    hue = 0x0386;
                }

                items[i] = new PopupMenuItem
                (
                    cliloc,
                    index,
                    hue,
                    replaced,
                    flags
                );
            }

            return new PopupMenuData(serial, items);
        }
    }
}