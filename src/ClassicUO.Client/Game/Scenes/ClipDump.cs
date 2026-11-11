// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// CLEANUP(release) clip-1: driver <c>clipdump &lt;name&gt; [radius]</c> logs the render-order inputs of the tiles around the
    /// player as <c>[clipdump]</c> lines: every object of each tile in walk order (kind, graphic, X/Y/Z, PriorityZ, tiledata
    /// height and flags, screen position, Legacy depth, Gpu D = 100·(x+y)+127+pz), and for the next ~0.5 s every animation layer
    /// the mobile extract draws (frame size, centre, anchor, mount offset, strips with their Legacy depth and Gpu D).
    /// Off, it costs one static bool read per drawn layer.
    /// </summary>
    internal static class ClipDump
    {
        public static bool Active;

        private static uint _until;
        private static string _name;
        private static readonly HashSet<string> _seen = new HashSet<string>();

        public static void Run(World world, string name, int radius)
        {
            Mobile player = world.Player;
            _name = name;
            Log.Info($"[clipdump] {name} player {player.X} {player.Y} {player.Z} dir {player.Direction} mounted {player.IsMounted} body 0x{player.Graphic:X4} offset {player.Offset}");

            for (int ty = player.Y - radius; ty <= player.Y + radius; ty++)
            {
                for (int tx = player.X - radius; tx <= player.X + radius; tx++)
                {
                    Chunk chunk = world.Map.GetChunk(tx, ty, false);

                    if (chunk == null)
                    {
                        continue;
                    }

                    int cell = (tx & 7) << 3 | (ty & 7);

                    for (GameObject o = TileWalk.Begin(chunk, cell, out TileWalk walk); o != null; o = walk.Next())
                    {
                        Log.Info($"[clipdump] {name} tile {tx} {ty} {Describe(o)}");
                    }
                }
            }

            _seen.Clear();
            _until = Time.Ticks + 500;
            Active = true;
        }

        private static string Describe(GameObject o)
        {
            string kind = o switch
            {
                Land => "land",
                Static => "static",
                Multi => "multi",
                Mobile => "mobile",
                Item => "item",
                GameEffect => "effect",
                _ => o.GetType().Name
            };

            string tile = "";

            if (o is Static or Multi or Item)
            {
                ref StaticTiles data = ref o is Static s ? ref s.ItemData : ref o is Multi m ? ref m.ItemData : ref ((Item)o).ItemData;
                tile = $" h {data.Height} flags {data.Flags}";
            }

            float depth = o.CalculateDepthZ();
            float drawn = o is Mobile ? depth : depth + 0.5f;

            return $"{kind} 0x{o.Graphic:X4} z {o.Z} pz {o.PriorityZ}{tile} screen {o.RealScreenPosition.X},{o.RealScreenPosition.Y} mesh {o.InChunkMesh} " +
                   $"depth {depth:F2} drawn {drawn:F2} gpuD {(int)MathF.Round(drawn * 100f) - 50}";
        }

        /// <summary>MobileSpriteExtract.Layer, per drawn (non-shadow) layer while armed.</summary>
        public static void Layer(int objX, ushort id, byte group, byte dir, byte frame, bool isMount, bool isEquip, int w, int h, int centerX, int centerY,
            int anchorX, int anchorY, int frameX, int frameY, int mountOffset, float depth)
        {
            if (Time.Ticks > _until)
            {
                Active = false;

                return;
            }

            Span<(int Y, int H)> strips = stackalloc (int, int)[8];
            int n = AnimationRules.Strips(h, centerY, mountOffset, strips);
            var sb = new System.Text.StringBuilder();

            for (int i = 0; i < n && i < strips.Length; i++)
            {
                float d = depth + 1f + i * 2;
                sb.Append($" [{i}: rows {strips[i].Y}..{strips[i].Y + strips[i].H} screenY {frameY + strips[i].Y}..{frameY + strips[i].Y + strips[i].H} depth {d:F2} gpuD {(int)MathF.Round(d * 100f) - 50}]");
            }

            string line = $"[clipdump] {_name} layer x {objX} body {id} group {group} dir {dir} frame {frame} mount {isMount} equip {isEquip} size {w}x{h} " +
                          $"centre {centerX},{centerY} anchor {anchorX},{anchorY} frameTop {frameX},{frameY} frameBottom {frameY + h} mountOffset {mountOffset} base {depth:F2}{sb}";

            if (_seen.Add(line))
            {
                Log.Info(line);
            }
        }
    }
}
#endif
