// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// world_fill_diamond: the world list fill walks only the tiles of its square tile range (GameScene.GetViewPort) that can
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
    /// Chunk-level work (mesh builds, the visible chunk list, the Gpu prepare masks) still runs for every chunk of the square.
    /// </para>
    /// What the switch drops: the work the old walk did on a skipped tile's objects before culling them by y, which no frame
    /// shows: the fade steps of unmeshed objects more than 512 px above or below the view, opening name-plate handles for
    /// them, and the present-on-change "animated art in the walk" flag for them. CUO_FILL_DIAMOND=1|0 overrides the setting
    /// in developer builds; CUO_FILL_DIAMOND_CHECK=1|N walks the whole square on every Nth frame (default 10) and logs what
    /// the skipped tiles produced (drawn objects, hidden overrides, the selection: mismatches; alpha and handle changes:
    /// counted as dropped side effects).
    /// </summary>
    internal static class FillDiamond
    {
        // GameScene.GPU_WIDE_ART: CollectGpuHidden's own cut beside the Gpu view
        private const int GPU_WIDE_ART = 320;
        // -4 * Z for Z in sbyte: an object's anchor y is the tile's plus -508..+512
        private const int Z_UP = 127 * 4, Z_DOWN = 128 * 4;

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_FILL_DIAMOND=1|0)
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_FILL_DIAMOND") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        // CLEANUP(release): CUO_FILL_DIAMOND_CHECK=1|N
        private static readonly int _checkEvery = ParseCheck(Environment.GetEnvironmentVariable("CUO_FILL_DIAMOND_CHECK"));
#else
        private static readonly bool? _env = null;
        private const int _checkEvery = 0;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? Settings.GlobalSettings.WorldFillDiamond;

        internal static int ParseCheck(string v)
        {
            if (string.IsNullOrEmpty(v) || v == "0")
            {
                return 0;
            }

            return int.TryParse(v, out int n) && n > 1 ? n : v == "1" ? 10 : 0;
        }

        /// <summary>The tile bands, inclusive: D = x - y, S = x + y.</summary>
        public struct Bounds : IEquatable<Bounds>
        {
            public int DLo, DHi, SLo, SHi;

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
            // walk: minPixel.Y <= S * 22 - offY - 22 - 4Z <= maxPixel.Y for some Z
            double sLo = minPixel.Y + offY + 22 - Z_DOWN, sHi = maxPixel.Y + offY + 22 + Z_UP;

            if (gpu)
            {
                // CollectGpuHidden: gpuView.Left - GPU_WIDE_ART <= D * 22 - offX - 22 <= gpuView.Right + GPU_WIDE_ART
                dLo = Math.Min(dLo, gpuView.Left - GPU_WIDE_ART + offX + 22);
                dHi = Math.Max(dHi, gpuView.Right + GPU_WIDE_ART + offX + 22);
                // GpuLandVisible / GpuStaticVisible with base = S * 22 - offY - 22: some sprite bottom (at most base + 556)
                // below the view's top, some sprite top (land: base - 636, statics: base - 464 - art height) above its bottom
                sLo = Math.Min(sLo, gpuView.Top - (Z_DOWN + 44) + offY + 22);
                sHi = Math.Max(sHi, gpuView.Bottom + Math.Max(Z_UP + 128 + 1, Z_UP - 44 + Math.Max(0, maxStaticArtH)) + offY + 22);
            }

            return new Bounds
            {
                DLo = FloorDiv22(dLo) - 1,
                DHi = FloorDiv22(dHi) + 1,
                SLo = FloorDiv22(sLo) - 1,
                SHi = FloorDiv22(sHi) + 1
            };
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

        /// <summary>The fill's tile mask for a chunk: the diamond with the switch on, every tile with it off.</summary>
        public static ulong WalkMask(bool enabled, int chunkX, int chunkY, in Bounds b)
        {
            return enabled ? ChunkMask(chunkX, chunkY, in b) : ulong.MaxValue;
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

#if CUO_DEVTOOLS
        // ---- CUO_FILL_DIAMOND_CHECK (CLEANUP(release)) ----

        private static int _frame;
        private static long _checkFrames, _tiles, _outside, _drawn, _hidden, _selection, _side, _at;
        private static int _logged;

        /// <summary>This fill walks the whole square and judges the diamond (switch on, checker on, every Nth fill).</summary>
        public static bool BeginCheckFrame()
        {
            if (_checkEvery <= 0 && CheckEveryForTests <= 0)
            {
                return false;
            }

            int every = CheckEveryForTests > 0 ? CheckEveryForTests : _checkEvery;

            if (++_frame % every != 0)
            {
                return false;
            }

            _checkFrames++;

            return true;
        }

        /// <summary>Tests: check every Nth fill (0: CUO_FILL_DIAMOND_CHECK).</summary>
        internal static int CheckEveryForTests;

        internal static long Mismatches => _drawn + _hidden + _selection;
        internal static long SideEffects => _side;

        public static void CountTiles(int tiles, int outside)
        {
            _tiles += tiles;
            _outside += outside;
        }

        public static void Drawn(object obj, int x, int y, int z, in Bounds b)
        {
            _drawn++;
            Mismatch($"drawn {obj?.GetType().Name} at {x},{y},{z} on a tile outside the diamond ({b})");
        }

        public static void Hidden(int x, int y, int calls, in Bounds b)
        {
            _hidden += calls;
            Mismatch($"{calls} hidden override(s) from tile {x},{y} outside the diamond ({b})");
        }

        public static void Selected(object obj, int x, int y, int z, in Bounds b)
        {
            _selection++;
            Mismatch($"selection {obj?.GetType().Name} at {x},{y},{z} outside the diamond ({b})");
        }

        public static void SideEffect(int x, int y)
        {
            _side++;
        }

        private static void Mismatch(string s)
        {
            if (_logged < 20)
            {
                _logged++;
                Log.Warn($"[fill-diamond-check] mismatch: {s}");
            }
        }

        /// <summary>Once per fill on a check run: the periodic summary.</summary>
        public static void Report(bool gpu)
        {
            long now = Environment.TickCount64;

            if (_at == 0)
            {
                _at = now;

                return;
            }

            if (now - _at < 5000)
            {
                return;
            }

            _at = now;
            double pct = _tiles > 0 ? 100.0 * _outside / _tiles : 0;
            Log.Trace(
                $"[fill-diamond-check] switch {(Enabled ? "on" : "off")}, {(gpu ? "gpu" : "legacy")}: {_checkFrames} check frames, " +
                $"{_tiles} square tiles walked, {_outside} outside the diamond ({pct:0.0}%), mismatches {Mismatches} " +
                $"(drawn {_drawn}, hidden overrides {_hidden}, selection {_selection}), dropped side effects {_side} tiles");
        }

        internal static void ResetForTests()
        {
            OverrideForTests = null;
            CheckEveryForTests = 0;
            _frame = 0;
            _checkFrames = _tiles = _outside = _drawn = _hidden = _selection = _side = _at = 0;
            _logged = 0;
        }
#else
        public static bool BeginCheckFrame() => false;
#endif
    }
}
