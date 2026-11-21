// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using ClassicUO.Utility.Platforms;
using Microsoft.Xna.Framework;
using System;
using System.IO;
using System.Collections.Generic;
using System.Text;

namespace ClassicUO.Network
{
    sealed partial class PacketHandlers
    {
        private static void OpenGump(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            uint sender = p.ReadUInt32();
            uint gumpID = p.ReadUInt32();
            int x = (int)p.ReadUInt32();
            int y = (int)p.ReadUInt32();

            ushort cmdLen = p.ReadUInt16();
            string cmd = p.ReadCp1252(cmdLen);

            ushort textLinesCount = p.ReadUInt16();

            string[] lines = new string[textLinesCount];

            for (int i = 0; i < textLinesCount; ++i)
            {
                int length = p.ReadUInt16();

                if (length > 0)
                {
                    lines[i] = p.ReadBigUni(length);
                }
                else
                {
                    lines[i] = string.Empty;
                }
            }

            //for (int i = 0, index = p.Position; i < textLinesCount; i++)
            //{
            //    int length = ((p[index++] << 8) | p[index++]) << 1;
            //    int true_length = 0;

            //    while (true_length < length)
            //    {
            //        if (((p[index + true_length++] << 8) | p[index + true_length++]) << 1 == '\0')
            //        {
            //            break;
            //        }
            //    }

            //    unsafe
            //    {

            //        fixed (byte* ptr = &p.Buffer[index])
            //        {
            //            lines[i] = Encoding.BigEndianUnicode.GetString(ptr, true_length);
            //        }
            //    }
            //    index += length;
            //}

            GumpFactory.OpenServerGump(world, sender, gumpID, x, y, cmd, lines);
        }

        private static void OpenCompressedGump(World world, ref NetReader p)
        {
            uint sender = p.ReadUInt32();
            uint gumpID = p.ReadUInt32();
            uint x = p.ReadUInt32();
            uint y = p.ReadUInt32();
            uint clen = p.ReadUInt32() - 4;
            int dlen = (int)p.ReadUInt32();
            byte[] decData = System.Buffers.ArrayPool<byte>.Shared.Rent(dlen);
            string layout;

            try
            {
                // A bad stream (wrong dlen from a misconfigured server) yields an empty layout; the gump opens empty.
                if (ZlibInflater.TryDecompress(p.Buffer.Slice(p.Position, (int)clen), decData.AsSpan(0, dlen), out int inflated))
                {
                    layout = Encoding.UTF8.GetString(decData.AsSpan(0, inflated));
                }
                else
                {
                    Log.Warn($"Compressed gump 0x{gumpID:X8}: bad layout stream (clen {clen}, dlen {dlen})");
                    layout = string.Empty;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(decData);
            }

            p.Skip((int)clen);

            uint linesNum = p.ReadUInt32();
            string[] lines = new string[linesNum];

            try
            {
                if (linesNum != 0)
                {
                    clen = p.ReadUInt32() - 4;
                    dlen = (int)p.ReadUInt32();
                    decData = System.Buffers.ArrayPool<byte>.Shared.Rent(dlen);

                    try
                    {
                        if (!ZlibInflater.TryDecompress(p.Buffer.Slice(p.Position, (int)clen), decData.AsSpan(0, dlen), out int inflated))
                        {
                            // Same edge as the layout above: all lines read as empty.
                            Log.Warn($"Compressed gump 0x{gumpID:X8}: bad text-lines stream (clen {clen}, dlen {dlen})");
                            inflated = 0;
                        }

                        p.Skip((int)clen);

                        var reader = new NetReader(decData.AsSpan(0, inflated));

                        for (int i = 0; i < linesNum; ++i)
                        {
                            int remaining = reader.Remaining;

                            if (remaining >= 2)
                            {
                                int length = reader.ReadUInt16();

                                if (length > 0)
                                {
                                    lines[i] = reader.ReadBigUni(length);
                                }
                                else
                                {
                                    lines[i] = string.Empty;
                                }
                            }
                            else
                            {
                                lines[i] = string.Empty;
                            }
                        }


                        //for (int i = 0, index = 0; i < linesNum && index < dlen; i++)
                        //{
                        //    int length = ((decData[index++] << 8) | decData[index++]) << 1;
                        //    int true_length = 0;

                        //    for (int k = 0; k < length && true_length < length && index + true_length < dlen; ++k, true_length += 2)
                        //    {
                        //        ushort c = (ushort)(((decData[index + true_length] << 8) | decData[index + true_length + 1]) << 1);

                        //        if (c == '\0')
                        //        {
                        //            break;
                        //        }
                        //    }

                        //    lines[i] = Encoding.BigEndianUnicode.GetString(decData, index, true_length);

                        //    index += length;
                        //}
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(decData);
                    }
                }

                GumpFactory.OpenServerGump(world, sender, gumpID, (int)x, (int)y, layout, lines);
            }
            finally
            {
                //System.Buffers.ArrayPool<string>.Shared.Return(lines);
            }
        }

        [Flags]
        private enum AffixType
        {
            Append = 0x00,
            Prepend = 0x01,
            System = 0x02
        }
    }
}
