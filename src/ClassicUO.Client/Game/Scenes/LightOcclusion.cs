// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Configuration;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// light_occlusion (light-edges, default off): the light layer is screen-space additive quads without depth, so at the
    /// Moonglow bank the halo of a lit window was drawn over the window and wall in front of it. With the switch on, the light
    /// pass (GameScene.DrawLight) writes the art of every occluder of a light into the light target's stencil and draws that
    /// light where the stencil does not hold it.
    /// Owner rule (2026-11-22, restated for round 4): a light's glow passes through its own emitter only; another object
    /// rendered over the light, between the viewer and the source, hides it, whatever its kind (another window, lit or not,
    /// a wall, a light-flagged static, a ground item). An occluder stands on a later diagonal (<see cref="InFront"/>) and its
    /// art covers the source's light point on screen (<see cref="LightPoint"/>, <see cref="CoverPoint"/> and the art's pixel
    /// mask). Objects the source stands on, or that stand beside it on the same wall line, are drawn after it but do not cover
    /// its point: they are lit by it, not blockers (round 3 masked every object drawn after the source, which cut the
    /// candles' glow on the bank table, the windows' halo on their own wall and the street lamps' halos on fences, roofs and
    /// flowers).
    /// A wall source (a lit window) is also hidden by wall pieces in front whose visible face turns away from it
    /// (<see cref="FacesAway"/>, the line taken from the art, <see cref="Facing"/>): the perpendicular line at an inside
    /// corner of the keep's zig-zag wall, a parallel line in front; never the pieces of its own line.
    /// Tiles that never hide anything: translucent ones (water, glass: drawn see-through), internal (no-draw) ones, and flat
    /// walkable surfaces (floors). Mobiles do not occlude.
    /// CUO_LIGHT_OCCLUSION_CHECK=1 compares each light's occluders (the window <see cref="Window"/> walks) with a reference
    /// scan of every tile up to <see cref="ReferenceReach"/> around the source, in both states, and logs each difference.
    /// </summary>
    internal static class LightOcclusion
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_LIGHT_OCCLUSION") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_LIGHT_OCCLUSION_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.LightOcclusion ?? false);

        /// <summary>The occluder art the window assumes: half width past the anchor's 44 px box, height above its bottom.
        /// Larger art further away is missed (the checker's reference scan reports it).</summary>
        public const int ArtSide = 128, ArtUp = 256;

        /// <summary>The occluder Z the window assumes, relative to the light's source (one floor each way, plus slack).</summary>
        public const int ZBelow = 30, ZAbove = 40;

        /// <summary>CUO_LIGHT_OCCLUSION_CHECK: the reference scan's reach on each axis.</summary>
        public const int ReferenceReach = 32;

        /// <summary>An object at (dx, dy, dz) from the light's source stands in front of it: on a later diagonal (dx + dy
        /// greater), so it is drawn after it. Objects on the source's own tile are at its depth (drawn after it only by Z):
        /// they stand with the emitter, not in front of it (round 4: the fountain piece sharing the Moonglow fountain lamp's
        /// tile masked its glow), so they never hide it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool InFront(int dx, int dy, int dz) => dx + dy > 0;

        /// <summary>The tiledata of an object that hides a light drawn behind it: anything but a translucent or internal tile
        /// or a flat walkable surface (a roof hides).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool OccluderData(ref StaticTiles data) =>
            !data.IsTranslucent && !data.IsInternal && (data.IsRoof || !(data.IsSurface && data.Height == 0));

        /// <summary>The light pass masks a light's halo over an object with tiledata <paramref name="data"/> at (dx, dy, dz)
        /// from its source: only with the switch on (<paramref name="on"/>), never over the light's own emitter
        /// (<paramref name="ownEmitter"/>), in front of the source, an occluder, and either its art covering the source's
        /// light point (<paramref name="coversSource"/>: <see cref="CoverPoint"/> and the art's pixel there) or, for a wall
        /// source, a wall piece whose visible face turns away from it (<paramref name="wallFacesAway"/>,
        /// <see cref="FacesAway"/>).</summary>
        public static bool Hides(bool on, bool ownEmitter, int dx, int dy, int dz, ref StaticTiles data, bool coversSource,
            bool wallFacesAway = false) =>
            on && !ownEmitter && (coversSource || wallFacesAway) && InFront(dx, dy, dz) && OccluderData(ref data);

        /// <summary>A wall piece's line, from its art (44 px wide, its visible bounds bx..bx+bw): the slab on the tile's east
        /// edge (a line along Y, its visible face towards +X, e.g. 0x0162), on its south edge (a line along X, face towards
        /// +Y, e.g. 0x0163), or neither (posts, arches, odd art: <see cref="FacingNone"/>).</summary>
        public const byte FacingNone = 0, FacingEast = 1, FacingSouth = 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Facing(int w, int bx, int bw)
        {
            if (w != 44 || bw <= 0)
            {
                return FacingNone;
            }

            int c = 2 * bx + bw; // twice the bounds' centre

            return c >= 52 ? FacingEast : c <= 36 ? FacingSouth : FacingNone;
        }

        /// <summary>Round 4 (owner 2026-11-22, the Moonglow keep's inside corners): a wall source's glow spilled onto the
        /// perpendicular wall in front of it, which does not cover the window itself. A wall piece facing
        /// <paramref name="occFacing"/> at (dx, dy) from a wall source facing <paramref name="srcFacing"/> turns its visible
        /// face away from the source when the source lies behind that face's plane: a piece of the same line (same facing,
        /// aligned: dx 0 for east-facing, dy 0 for south-facing) is lit, a parallel line in front or the perpendicular line
        /// at a corner is not. Unknown facings never count (the cover test still applies).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool FacesAway(byte srcFacing, byte occFacing, int dx, int dy)
        {
            if (srcFacing == FacingNone)
            {
                return false;
            }

            return occFacing switch
            {
                FacingEast => srcFacing == FacingEast ? dx > 0 : dx >= 0,
                FacingSouth => srcFacing == FacingSouth ? dy > 0 : dy >= 0,
                _ => false
            };
        }

        /// <summary>The light point an occluder must cover: the centre of the emitter's visible art (its art w x h anchored at
        /// (ax, ay), View.DrawStatic; its visible bounds bx, by, bw, bh in the art): a window's glass, a lamp post's upper
        /// post, a candle. Pieces drawn after the emitter that overlap only the edge of its art (the next piece of the same wall
        /// line) and low objects in front of its foot (the counter a candle stands on, the fence before a street lamp) do not
        /// cover it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void LightPoint(int ax, int ay, int w, int h, int bx, int by, int bw, int bh, out int px, out int py)
        {
            px = ax - ((w >> 1) - 22) + bx + (bw >> 1);
            py = ay - (h - 44) + by + (bh >> 1);
        }

        /// <summary>The art (w x h) of a static anchored at (ax, ay) (View.DrawStatic) holds the screen point (px, py): its
        /// pixel there is (lx, ly) in the art.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool CoverPoint(int ax, int ay, int w, int h, int px, int py, out int lx, out int ly)
        {
            lx = px - (ax - ((w >> 1) - 22));
            ly = py - (ay - (h - 44));

            return (uint)lx < (uint)w && (uint)ly < (uint)h;
        }

        /// <summary>The tile window (S = dx + dy, D = dx - dy, from the light's source tile) whose occluders, with the art
        /// and Z <see cref="ArtSide"/>/<see cref="ArtUp"/>/<see cref="ZBelow"/>/<see cref="ZAbove"/> assume, can overlap the
        /// light quad (left, top, right, bottom) of a source anchored at (anchorX, anchorY) (GameObject.RealScreenPosition).
        /// The walk takes S from 1 (later diagonals).</summary>
        public static void Window(int anchorX, int anchorY, int left, int top, int right, int bottom,
            out int sLo, out int sHi, out int dLo, out int dHi)
        {
            // an occluder at (dx, dy, dz) is anchored at (anchorX + D * 22, anchorY + S * 22 - 4 * dz); its art spans
            // x [ax - ArtSide, ax + 44 + ArtSide], y [ay + 44 - ArtUp, ay + 44]
            dLo = FloorDiv22(left - anchorX - 44 - ArtSide);
            dHi = CeilDiv22(right - anchorX + ArtSide);
            sLo = Math.Max(1, FloorDiv22(top - anchorY - 44 - 4 * ZBelow));
            sHi = CeilDiv22(bottom - anchorY - 44 + ArtUp + 4 * ZAbove);
        }

        /// <summary>The art rectangle of a static anchored at (ax, ay) (View.DrawStatic) overlaps the light quad.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ArtOverlaps(int ax, int ay, int w, int h, int left, int top, int right, int bottom)
        {
            int x0 = ax - ((w >> 1) - 22), y0 = ay - (h - 44);

            return x0 < right && x0 + w > left && y0 < bottom && y0 + h > top;
        }

        /// <summary>CUO_LIGHT_OCCLUSION_CHECK: an order-independent hash of one occluder.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Mix(int x, int y, int z, ushort graphic, bool item) => ViewMargin.Mix(x, y, z, graphic, item);

        private static int FloorDiv22(int v) => v >= 0 ? v / 22 : -((-v + 21) / 22);

        private static int CeilDiv22(int v) => -FloorDiv22(-v);
    }
}
