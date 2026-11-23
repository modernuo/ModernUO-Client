// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Configuration;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// The world beyond the walked view that can still reach it. The walk culls each object by its anchor against the view
    /// padded by one tile (GetViewPort: _minPixel/_maxPixel), but a light's halo reaches up to half the largest light texture
    /// (175 px in the classic lights) past its source and tall art reaches the view from beside it.
    /// view_margin (light-edges, default off; <see cref="Enabled"/>) is the sprite margin: the walk grows by the chunks' art
    /// reach. Its light part is chunk_lights since round 6c (<see cref="ChunkLights"/>): GameScene.AddChunkLights adds the
    /// light sources whose anchor is in the band between that rectangle and the rectangle grown by <see cref="LightMargin"/>
    /// (the chunks' recorded light statics and multi components, Chunk.LightSources, and their ground items) and every
    /// emitter the walk drew. CUO_VIEW_MARGIN_CHECK=1 checks the sprite margin.
    /// The pure geometry lives here for the unit tests.
    /// </summary>
    internal static class ViewMargin
    {
        // anchor y = S * 22 - 4 * Z - offY - 22 for Z in sbyte: -508..+512 px
        private const int Z_UP = 127 * 4, Z_DOWN = 128 * 4;

#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_VIEW_MARGIN") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_VIEW_MARGIN_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.ViewMargin ?? false);

        /// <summary>Sprite margin: how far a mobile's frame (its shadow included) may reach past its anchor box, beside it and
        /// above it (the large bodies' frames). Items, statics and multis use the chunks' recorded art (Chunk.StaticOver*).</summary>
        public const int MobileSide = 160, MobileUp = 260;

        /// <summary>A chunk's overhang record grew after its load (a multi or an item with larger art joined): the walk
        /// recomputes its positions once (tiles joining the walk kept stale ones).</summary>
        public static bool OverhangGrew;

        private static int _artSide = -1, _artUp;

        /// <summary>The largest static art's reach past its anchor box (beside, above), computed once from the pack.</summary>
        public static void ArtReach(ClassicUO.Assets.ArtLoader arts, out int side, out int up)
        {
            if (_artSide < 0)
            {
                int s = 0, u = 0;

                for (int g = 0; g < 0x10000; g++)
                {
                    if (arts.TrySize(g + 0x4000, out int w, out int h))
                    {
                        s = Math.Max(s, w - (w >> 1) - 22);
                        u = Math.Max(u, h - 44);
                    }
                }

                _artUp = u;
                _artSide = s;
            }

            side = Math.Max(_artSide, MobileSide);
            up = Math.Max(_artUp, MobileUp);
        }

        /// <summary>The walk's culling rectangle for one chunk: the view's (vMin..vMax, the walk's tile of slack included)
        /// grown by what the chunk's sprites can reach into it from beside it: objects left of the view by their right
        /// overhang, right of it by their left one, below it by their height. Mobiles may stand anywhere.</summary>
        public static void ChunkRect(float vMinX, float vMinY, float vMaxX, float vMaxY, int overLeft, int overRight, int overUp,
            out float minX, out float minY, out float maxX, out float maxY)
        {
            minX = vMinX - Math.Max(overRight, MobileSide);
            maxX = vMaxX + Math.Max(overLeft, MobileSide);
            minY = vMinY;
            maxY = vMaxY + Math.Max(overUp, MobileUp);
        }

        /// <summary>The light margin for lights whose largest texture side is <paramref name="maxExtent"/> px: the halo's
        /// radius (the light is drawn centred on anchor + 22, and the walked rectangle already has 44 px of slack).</summary>
        public static int LightMargin(int maxExtent) => (maxExtent + 1) >> 1;

        /// <summary>The tile bands (D = x - y, S = x + y, inclusive, one tile of slack) whose anchors can fall in the
        /// rectangle (left, top, right, bottom) for some Z, at draw offset (offX, offY).</summary>
        public static void Bands(int left, int top, int right, int bottom, int offX, int offY, out int dLo, out int dHi, out int sLo, out int sHi)
        {
            dLo = FloorDiv22(left + offX + 22) - 1;
            dHi = FloorDiv22(right + offX + 22) + 1;
            sLo = FloorDiv22(top + offY + 22 - Z_DOWN) - 1;
            sHi = FloorDiv22(bottom + offY + 22 + Z_UP) + 1;
        }

        /// <summary>The chunk rectangle holding every tile of the bands (clamped at 0).</summary>
        public static void ChunkRange(int dLo, int dHi, int sLo, int sHi, out int cx0, out int cy0, out int cx1, out int cy1)
        {
            // x = (S + D) / 2, y = (S - D) / 2
            cx0 = Math.Max(0, (sLo + dLo) >> 1) >> 3;
            cx1 = Math.Max(0, (sHi + dHi + 1) >> 1) >> 3;
            cy0 = Math.Max(0, (sLo - dHi) >> 1) >> 3;
            cy1 = Math.Max(0, (sHi - dLo + 1) >> 1) >> 3;
        }

        /// <summary>Whether chunk (cx, cy) has a tile in the bands.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ChunkTouches(int cx, int cy, int dLo, int dHi, int sLo, int sHi)
        {
            int d0 = (cx - cy) * 8 - 7, s0 = (cx + cy) * 8;

            return d0 + 14 >= dLo && d0 <= dHi && s0 + 14 >= sLo && s0 <= sHi;
        }

        /// <summary>The anchor (ax, ay) is outside the walked rectangle (the walk's own) and inside it grown by
        /// <paramref name="margin"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool InBand(int ax, int ay, int left, int top, int right, int bottom, int margin)
        {
            if (ax >= left && ax <= right && ay >= top && ay <= bottom)
            {
                return false;
            }

            return ax >= left - margin && ax <= right + margin && ay >= top - margin && ay <= bottom + margin;
        }

        /// <summary>A light drawn centred on (x, y) with a <paramref name="width"/> x <paramref name="height"/> texture touches
        /// the view (left, top, right, bottom), edges included.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool LightReaches(int x, int y, int width, int height, int left, int top, int right, int bottom)
        {
            int hw = (width + 1) >> 1, hh = (height + 1) >> 1;

            return width > 0 && height > 0 && x + hw >= left && x - hw <= right && y + hh >= top && y - hh <= bottom;
        }

        /// <summary>An object's anchor (GameObject.RealScreenPosition) at draw offset (offX, offY).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Anchor(int x, int y, int z, int offX, int offY, out int ax, out int ay)
        {
            ax = (x - y) * 22 - offX - 22;
            ay = (x + y) * 22 - (z << 2) - offY - 22;
        }

        // floor(v / 22) for any sign
        private static int FloorDiv22(int v) => v >= 0 ? v / 22 : -((-v + 21) / 22);

        /// <summary>CUO_VIEW_MARGIN_CHECK: an order-independent hash of one candidate.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Mix(int x, int y, int z, ushort graphic, bool item)
        {
            uint h = (uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)(z + 128) * 83492791u ^ (uint)graphic * 2654435761u ^ (item ? 0x9E3779B9u : 0u);
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;

            return h;
        }
    }
}
