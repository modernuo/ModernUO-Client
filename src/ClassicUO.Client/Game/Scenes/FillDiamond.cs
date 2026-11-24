// SPDX-License-Identifier: BSD-2-Clause

using System;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// The world list fill walks only the tiles of its square tile range (GameScene.GetViewPort) that can
    /// reach the screen. A tile's screen anchor depends on D = x - y (horizontal) and S = x + y (vertical, minus 4 px per Z),
    /// so the screen is a band in D crossed with a band in S: a diamond in tile space. The square range's corners and
    /// its rows far above and below a wide view are outside it.
    /// <para>
    /// The bands are what the walk (AddTileToRenderList) and the Gpu hidden pass (GameScene.CollectGpuHidden) can still
    /// act on, for any Z (sbyte) and any art:
    /// - D: the walk leaves a tile whose anchor is beside the culling rectangle (_minPixel/_maxPixel) at its first object;
    ///   CollectGpuHidden returns for a tile more than GPU_WIDE_ART beside the Gpu view.
    /// - S: the walk culls each object by its anchor's y, which Z moves by -508..+512 px (stretched land: MinZ, the same
    ///   range); CollectGpuHidden hides a baked sprite only where it reaches the Gpu view (land: 44 px plus stretch, statics:
    ///   the tallest static art).
    /// perf-18: the walk visits only the walk's bands (on the Gpu world too); the hidden pass the wider ones, where the walk
    /// would draw nothing. A chunk with no tile in the hidden bands gets no chunk-level work (mesh build, visibility reset, the
    /// visible chunk list, the Gpu prepare and item cache): nothing of it can be drawn or hidden.
    /// </para>
    /// What this drops: the work the old walk did on a skipped tile's objects before culling them by y, which no frame
    /// shows: the fade steps of unmeshed objects more than 512 px above or below the view, opening name-plate handles for
    /// them, and the present-on-change "animated art in the walk" flag for them. Always on (baked in after quiet-1115).
    /// </summary>
    internal static class FillDiamond
    {
        // GameScene.GPU_WIDE_ART: CollectGpuHidden's own cut beside the Gpu view
        private const int GPU_WIDE_ART = 320;
        // -4 * Z for Z in sbyte: an object's anchor y is the tile's plus -508..+512
        private const int Z_UP = 127 * 4, Z_DOWN = 128 * 4;

        /// <summary>The tile bands, inclusive: D = x - y, S = x + y.</summary>
        public struct Bounds : IEquatable<Bounds>
        {
            public int DLo, DHi, SLo, SHi;
            /// <summary>The S band's edges in pixels before the Z term (SLo = floor((SLoRaw + 4 * zLo) / 22) - 1,
            /// SHi = floor((SHiRaw + 4 * zHi) / 22) + 1), for the per-chunk bands of <see cref="ForZ"/>.</summary>
            public double SLoRaw, SHiRaw;

            public bool RawEquals(in Bounds o) => SLoRaw == o.SLoRaw && SHiRaw == o.SHiRaw;

            public bool Contains(int x, int y)
            {
                int d = x - y, s = x + y;

                return d >= DLo && d <= DHi && s >= SLo && s <= SHi;
            }

            public bool Equals(Bounds o) => DLo == o.DLo && DHi == o.DHi && SLo == o.SLo && SHi == o.SHi;

            public override bool Equals(object obj) => obj is Bounds b && Equals(b);

            public override int GetHashCode() => HashCode.Combine(DLo, DHi, SLo, SHi);

            public override string ToString() => $"D {DLo}..{DHi} S {SLo}..{SHi}";
        }

        private static int FloorDiv22(double v) => (int)Math.Floor(v / 22.0);

        /// <summary>
        /// The bands for this frame. offX/offY: GameScene._offset (anchor x = D * 22 - offX - 22, anchor y = S * 22 - 4Z -
        /// offY - 22). minPixel/maxPixel: the walk's culling rectangle. gpu: the Gpu world (gpuView: its view in the same
        /// coordinates; maxStaticArtH: the tallest static art). Each band gets a tile of slack on both sides.
        /// </summary>
        public static Bounds Compute(int offX, int offY, Vector2 minPixel, Vector2 maxPixel, bool gpu, Rectangle gpuView, int maxStaticArtH)
        {
            // walk: minPixel.X <= D * 22 - offX - 22 <= maxPixel.X
            double dLo = minPixel.X + offX + 22, dHi = maxPixel.X + offX + 22;
            // walk: minPixel.Y <= S * 22 - offY - 22 - 4Z <= maxPixel.Y for some Z (the Z term added below)
            double sLo = minPixel.Y + offY + 22, sHi = maxPixel.Y + offY + 22;

            if (gpu)
            {
                // CollectGpuHidden: gpuView.Left - GPU_WIDE_ART <= D * 22 - offX - 22 <= gpuView.Right + GPU_WIDE_ART
                dLo = Math.Min(dLo, gpuView.Left - GPU_WIDE_ART + offX + 22);
                dHi = Math.Max(dHi, gpuView.Right + GPU_WIDE_ART + offX + 22);
                // GpuLandVisible / GpuStaticVisible with base = S * 22 - offY - 22: some sprite bottom (at most base + 556)
                // below the view's top, some sprite top (land: base - 636, statics: base - 464 - art height) above its bottom
                // (the Z term is the same for both, added below)
                sLo = Math.Min(sLo, gpuView.Top - 44 + offY + 22);
                sHi = Math.Max(sHi, gpuView.Bottom + Math.Max(128 + 1, -44 + Math.Max(0, maxStaticArtH)) + offY + 22);
            }

            return new Bounds
            {
                DLo = FloorDiv22(dLo) - 1,
                DHi = FloorDiv22(dHi) + 1,
                SLo = FloorDiv22(sLo - Z_DOWN) - 1,
                SHi = FloorDiv22(sHi + Z_UP) + 1,
                SLoRaw = sLo,
                SHiRaw = sHi
            };
        }

        /// <summary>
        /// <paramref name="b"/> with its S band for anchors at Z <paramref name="zLo"/>..<paramref name="zHi"/>
        /// only (a chunk's recorded range, Chunk.WalkZLo/Hi) instead of every sbyte Z. The D band and the art and land-stretch
        /// terms are unchanged; zLo -128 and zHi 127 give <paramref name="b"/> itself.
        /// </summary>
        public static Bounds ForZ(in Bounds b, int zLo, int zHi)
        {
            Bounds r = b;
            r.SLo = FloorDiv22(b.SLoRaw + (zLo << 2)) - 1;
            r.SHi = FloorDiv22(b.SHiRaw + (zHi << 2)) + 1;

            return r;
        }

        /// <summary>The chunk's tiles inside the bands, bit (x &lt;&lt; 3 | y) as in the fill's tile loop.</summary>
        public static ulong ChunkMask(int chunkX, int chunkY, in Bounds b)
        {
            int bx = chunkX << 3, by = chunkY << 3;
            int d0 = bx - by, s0 = bx + by;

            if (d0 + 7 < b.DLo || d0 - 7 > b.DHi || s0 + 14 < b.SLo || s0 > b.SHi)
            {
                return 0;
            }

            if (d0 - 7 >= b.DLo && d0 + 7 <= b.DHi && s0 >= b.SLo && s0 + 14 <= b.SHi)
            {
                return ulong.MaxValue;
            }

            ulong mask = 0;

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    int d = d0 + x - y, s = s0 + x + y;

                    if (d >= b.DLo && d <= b.DHi && s >= b.SLo && s <= b.SHi)
                    {
                        mask |= 1UL << (x << 3 | y);
                    }
                }
            }

            return mask;
        }

        /// <summary>
        /// perf-18: the walk's bands and the hidden pass's. On the Gpu world the hidden pass reaches further than the walk
        /// (GPU_WIDE_ART beside the view, the tallest static art below it), and the tiles only it reaches need no walk: the
        /// walk draws nothing there, so CollectGpuHidden reads the same DrawnFrame stamps without it. Legacy: the same bands.
        /// </summary>
        public struct Bands : IEquatable<Bands>
        {
            public Bounds Walk, Hidden;

            public bool Equals(Bands o) => Walk.Equals(o.Walk) && Hidden.Equals(o.Hidden);

            public override bool Equals(object obj) => obj is Bands b && Equals(b);

            public override int GetHashCode() => HashCode.Combine(Walk, Hidden);

            public override string ToString() => $"walk {Walk}, hidden {Hidden}";
        }

        /// <summary>The walk's bands (Compute without the Gpu reach) and, on the Gpu world, the hidden pass's (a superset).</summary>
        public static Bands ComputeBands(int offX, int offY, Vector2 minPixel, Vector2 maxPixel, bool gpu, Rectangle gpuView, int maxStaticArtH)
        {
            Bounds walk = Compute(offX, offY, minPixel, maxPixel, false, gpuView, 0);

            return new Bands
            {
                Walk = walk,
                Hidden = gpu ? Compute(offX, offY, minPixel, maxPixel, true, gpuView, maxStaticArtH) : walk
            };
        }

        /// <summary>
        /// The fill's tile masks for a chunk: the tiles it walks (returned) and the tiles its Gpu hidden pass visits
        /// (<paramref name="hidden"/>, a superset; Legacy: the same). A chunk whose hidden mask is
        /// empty gets no chunk-level work at all (perf-18).
        /// </summary>
        /// <summary><see cref="ChunkMasks(bool, int, int, in Bands, out ulong)"/> for a chunk whose objects are
        /// anchored at Z <paramref name="zLo"/>..<paramref name="zHi"/> (empty range: every Z).</summary>
        public static ulong ChunkMasks(bool gpu, int chunkX, int chunkY, in Bands b, int zLo, int zHi, out ulong hidden)
        {
            if (zLo > zHi)
            {
                return ChunkMasks(gpu, chunkX, chunkY, in b, out hidden);
            }

            Bands t = new Bands { Walk = ForZ(in b.Walk, zLo, zHi), Hidden = gpu ? ForZ(in b.Hidden, zLo, zHi) : default };

            return ChunkMasks(gpu, chunkX, chunkY, in t, out hidden);
        }

        public static ulong ChunkMasks(bool gpu, int chunkX, int chunkY, in Bands b, out ulong hidden)
        {
            if (!gpu)
            {
                hidden = ChunkMask(chunkX, chunkY, in b.Walk);

                return hidden;
            }

            hidden = ChunkMask(chunkX, chunkY, in b.Hidden);

            return hidden == 0 ? 0 : ChunkMask(chunkX, chunkY, in b.Walk);
        }

        private static Uo.Pack.SpriteEntry[] _artRef;
        private static int _artH;

        /// <summary>The tallest static art (OrigH of entries 0x4000 on), cached per sprite table.</summary>
        public static int MaxStaticArtHeight(Uo.Pack.SpriteEntry[] art)
        {
            if (art == null)
            {
                return 1024;
            }

            if (!ReferenceEquals(_artRef, art))
            {
                int h = 0;

                for (int i = 0x4000; i < art.Length; i++)
                {
                    h = Math.Max(h, art[i].OrigH);
                }

                _artRef = art;
                _artH = h;
            }

            return _artH;
        }
    }
}
