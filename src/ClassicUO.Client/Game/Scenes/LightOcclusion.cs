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
    /// pass (GameScene.DrawLight) writes the art of every occluder drawn in front of a light's source into the light target's
    /// stencil and draws that light where the stencil does not hold it.
    /// Owner rule (2026-11-22): a light's glow passes through its own emitter only. Any other object drawn in front of the
    /// source hides it, whatever its kind: another window (lit or not), a wall, a light-flagged static, a ground item. In
    /// front means drawn after the source (<see cref="InFront"/>: a later diagonal, or the source's tile higher up). The
    /// exceptions are the tiles that never hid anything: translucent ones (water, glass: drawn see-through), internal
    /// (no-draw) ones, and flat walkable surfaces (floors), which receive the glow rather than cover the source.
    /// Mobiles do not occlude.
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

        /// <summary>An object at (dx, dy, dz) from the light's source is drawn after it: a later diagonal (dx + dy greater),
        /// or the source's own tile higher up.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool InFront(int dx, int dy, int dz) => dx + dy > 0 || dx == 0 && dy == 0 && dz > 0;

        /// <summary>The tiledata of an object that hides a light drawn behind it: anything but a translucent or internal tile
        /// or a flat walkable surface (a roof hides).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool OccluderData(ref StaticTiles data) =>
            !data.IsTranslucent && !data.IsInternal && (data.IsRoof || !(data.IsSurface && data.Height == 0));

        /// <summary>The light pass masks a light's halo over an object with tiledata <paramref name="data"/> at (dx, dy, dz)
        /// from its source: only with the switch on (<paramref name="on"/>), never over the light's own emitter
        /// (<paramref name="ownEmitter"/>), in front of the source, an occluder.</summary>
        public static bool Hides(bool on, bool ownEmitter, int dx, int dy, int dz, ref StaticTiles data) =>
            on && !ownEmitter && InFront(dx, dy, dz) && OccluderData(ref data);

        /// <summary>The tile window (S = dx + dy, D = dx - dy, from the light's source tile) whose occluders, with the art
        /// and Z <see cref="ArtSide"/>/<see cref="ArtUp"/>/<see cref="ZBelow"/>/<see cref="ZAbove"/> assume, can overlap the
        /// light quad (left, top, right, bottom) of a source anchored at (anchorX, anchorY) (GameObject.RealScreenPosition).
        /// The walk takes S from 1 (later diagonals) and the source's own tile.</summary>
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
