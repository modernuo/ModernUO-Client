// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Utility.Logging;
using Uo.Render;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// CLEANUP(release) clip-2: <c>CUO_CLIP_ORDER_CHECK=1</c> compares the two depth models (DepthModel) on the pairs a mobile
    /// forms with the land and statics around it. About every 2 s one Gpu frame is armed: every animation layer the mobile
    /// extract draws for a standing mobile is sampled every 4 px where its frame overlaps the shape of a static or land tile
    /// within 2 tiles (GeoDepth.OnShape). At each sample the old order (the Gpu keys: the layer's strip against the tile's
    /// key, ties to the static) and the new one (GeoDepth, ties to the static) are worked out; pairs with changed samples are
    /// logged as <c>[clip-order]</c> lines (at most 24 per armed frame) and the frame ends with a summary line with the switch
    /// state. Items, multis and walking mobiles are not sampled (items and multis still draw by key in both models). Both
    /// orders are computed from positions and tiledata, not read back from the GPU, so the check runs with the switch off or
    /// on; samples where the art is transparent count too.
    /// </summary>
    internal static class ClipOrderCheck
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("CUO_CLIP_ORDER_CHECK") == "1";

        /// <summary>This frame's mobile layers are sampled (MobileSpriteExtract.Layer; the frame cache is bypassed).</summary>
        public static bool Armed;

        private const int STEP = 4, RADIUS = 2, MAX_LINES = 24;

        private static uint _next;
        private static World _world;
        private static int _lines, _framePairs, _frameChanged;
        private static long _checks, _pairs, _samples, _changed, _toFront, _toBack;
        private static readonly HashSet<long> _seen = new HashSet<long>();

        /// <summary>Gpu frame start: ends the armed frame before it and arms this one every ~2 s.</summary>
        public static void BeginFrame(World world)
        {
            if (!Enabled)
            {
                return;
            }

            if (Armed)
            {
                Finish();
            }

            if (Time.Ticks < _next || world?.Player == null)
            {
                return;
            }

            _next = Time.Ticks + 2000;
            _world = world;
            _lines = _framePairs = _frameChanged = 0;
            _seen.Clear();
            Armed = true;
        }

        private static void Finish()
        {
            Armed = false;
            _checks++;
            Log.Info($"[clip-order] summary: depth_model={DepthModel.On}, checks {_checks}, pairs {_pairs} (this check {_framePairs}), samples {_samples}, " +
                     $"changed {_changed} (this check {_frameChanged}): mobile now in front {_toFront}, now behind {_toBack}");
        }

        /// <summary>One drawn (non-shadow) layer of the mobile on tile (mx, my, mz): its frame's top-left and the anchor in the
        /// extract's screen space, its size, the strips' inputs and the base depth (MobileSpriteExtract.DepthOf kind 0).</summary>
        public static void Layer(int mx, int my, int mz, ushort body, int anchorX, int anchorY, int frameX, int frameY, int w, int h, int centerY,
            int mountOffset, float depth)
        {
            if (_world?.Map == null)
            {
                return;
            }

            // world pixels (GeoDepth): the anchor of a standing mobile, the frame at the same offset from it as on screen
            var (ax, ay) = GeoDepth.MobileAnchor(mx, my, mz);
            int fx = ax + frameX - anchorX, fy = ay + frameY - anchorY;
            Span<(int Y, int H)> strips = stackalloc (int, int)[8];
            int n = Math.Min(AnimationRules.Strips(h, centerY, mountOffset, strips), strips.Length);

            for (int ty = my - RADIUS; ty <= my + RADIUS; ty++)
            {
                for (int tx = mx - RADIUS; tx <= mx + RADIUS; tx++)
                {
                    Chunk chunk = _world.Map.GetChunk(tx, ty, false);

                    if (chunk == null)
                    {
                        continue;
                    }

                    int cell = (tx & 7) << 3 | (ty & 7);
                    int landZ = int.MinValue;

                    for (GameObject o = TileWalk.Begin(chunk, cell, out TileWalk walk); o != null; o = walk.Next())
                    {
                        if (o is Land)
                        {
                            landZ = o.Z;
                        }
                    }

                    for (GameObject o = TileWalk.Begin(chunk, cell, out TileWalk walk); o != null; o = walk.Next())
                    {
                        bool land = o is Land;

                        if (!land && o is not Static)
                        {
                            continue;
                        }

                        int z = o.Z, height = 0;
                        bool wall = false, flat = land, buried = false;

                        if (o is Static st)
                        {
                            ref StaticTiles data = ref st.ItemData;
                            height = data.Height;
                            wall = data.IsWall;
                            flat = !wall && height == 0 && (data.IsBackground || data.IsSurface);
                            buried = !flat && landZ != int.MinValue && z + height <= landZ;
                        }

                        // the static's key as baked (ClipDump's gpuD)
                        int staticD = (int)MathF.Round((o.CalculateDepthZ() + 0.5f) * 100f) - 50;
                        Sample(o, tx, ty, z, height, wall, flat, buried, land, staticD, mx, my, ax, ay, mz, body, fx, fy, w, h, strips.Slice(0, n), depth);
                    }
                }
            }
        }

        private static void Sample(GameObject o, int tx, int ty, int z, int height, bool wall, bool flat, bool buried, bool land, int staticD,
            int mx, int my, int ax, int ay, int mz, ushort body, int fx, int fy, int w, int h, ReadOnlySpan<(int Y, int H)> strips, float depth)
        {
            var (nx, ny) = GeoDepth.NorthVertex(tx, ty, z);
            int x0 = Math.Max(fx, nx - 22), x1 = Math.Min(fx + w, nx + 22);
            int y0 = Math.Max(fy, ny - 4 * height), y1 = Math.Min(fy + h, ny + 44);

            if (x0 >= x1 || y0 >= y1)
            {
                return;
            }

            int samples = 0, toFront = 0, toBack = 0;

            for (int sy = y0; sy < y1; sy += STEP)
            {
                // the strip of this row (MobileSpriteExtract: depth + 1 + 2i), else the base depth
                float rowDepth = depth;

                for (int i = 0; i < strips.Length; i++)
                {
                    int r = sy - fy;

                    if (r >= strips[i].Y && r < strips[i].Y + strips[i].H)
                    {
                        rowDepth = depth + 1f + i * 2;

                        break;
                    }
                }

                int mobD = (int)MathF.Round(rowDepth * 100f) - 50;
                bool oldFront = mobD > staticD;

                for (int sx = x0; sx < x1; sx += STEP)
                {
                    if (!GeoDepth.OnShape(tx, ty, z, height, wall, flat, sx, sy))
                    {
                        continue;
                    }

                    float stat = land ? GeoDepth.Land(sy, z) : GeoDepth.Static(tx, ty, z, height, wall, flat, buried, sx, sy);
                    bool newFront = GeoDepth.MobileBiased(ax, ay, mz, sx, sy) > stat;
                    samples++;

                    if (oldFront != newFront)
                    {
                        if (newFront)
                        {
                            toFront++;
                        }
                        else
                        {
                            toBack++;
                        }
                    }
                }
            }

            if (samples == 0)
            {
                return;
            }

            _pairs++;
            _framePairs++;
            _samples += samples;

            if (toFront + toBack == 0)
            {
                return;
            }

            _changed += toFront + toBack;
            _frameChanged += toFront + toBack;
            _toFront += toFront;
            _toBack += toBack;
            long key = (long)o.Graphic << 48 ^ (long)(tx & 0xFFFF) << 32 ^ (long)(ty & 0xFFFF) << 16 ^ (long)(z & 0xFF) << 8 ^ body;

            if (_lines < MAX_LINES && _seen.Add(key))
            {
                _lines++;
                string cls = land ? "land" : wall ? "wall" : flat ? "flat" : "box";
                Log.Warn($"[clip-order] mobile {mx},{my},{mz} body {body} vs {cls} 0x{o.Graphic:X4} {tx},{ty},{z} h {height}{(buried ? " buried" : "")}: " +
                         $"samples {samples}, changed {toFront + toBack} (now in front {toFront}, now behind {toBack})");
            }
        }
    }
}
#endif
