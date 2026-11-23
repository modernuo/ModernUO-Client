// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Uo.Pack;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// Switch <c>mobile_wall_order</c> (default off), depth key mode only (depth_model off): a mobile's strips below its anchor
    /// row (MobileSpriteExtract: depth + 1 + 2i, i ≥ 1: feet, robe hem, hooves, a large body's legs) are capped just behind a
    /// wall in front of the mobile, so they no longer show on its face (Felucca 1453 1668 z 10, the studio's south and east
    /// walls; clip-1 design note section 2).
    /// <list type="bullet">
    /// <item>A wall here is a static, multi piece or ground item with tiledata Wall whose [z, z + height) holds the mobile's z
    /// (the mobile is inside it, not standing on its top), on one of the tiles one or two diagonals ahead of the mobile's depth
    /// tile: (1,0), (0,1), (1,1), (2,0), (0,2). Further walls beat the + 2i strips already.</item>
    /// <item>Only the half of the wall's art that is in front of the mobile counts (walls stand on their tile's back edges: the
    /// north-west edge X = x is the left half from the north vertex, the north-east edge Y = y the right half). For (1,0) the
    /// north-west half is in front, the north-east half (a wall line running past the mobile's side) is behind it; (0,1) the
    /// reverse; (1,1) both; (2,0) and (0,2) like (1,0) and (0,1). That half, less 3 px at the vertex column and cut to the
    /// art's opaque columns (the pack sprite's trim), is the wall's column range relative to the depth tile's centre column.</item>
    /// <item>A strip whose frame columns overlap a wall's range takes the depth key one below the lowest such wall's
    /// (D − 1, D = 100 (x + y) + 127 + PriorityZ), never less than the upper strip's + 1, and never more than before.</item>
    /// </list>
    /// The scan runs per mobile per frame while the rule (or the checker) is on; its result is part of the mobile's select key
    /// (MobileFrameCache), so recorded strips follow it. <c>CUO_MOBILE_WALL_ORDER=1|0</c> overrides the setting and
    /// <c>CUO_MOBILE_WALL_ORDER_CHECK=1</c> logs each strip the rule moves (or would move, with the switch off) with the mobile,
    /// the wall and the keys, with a 5 s summary, in developer builds.
    /// </summary>
    internal static class MobileWallOrder
    {
        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

        /// <summary>Tests: the art sprite table (null: the pack's).</summary>
        internal static SpriteEntry[] SpritesForTests;

#if CUO_DEVTOOLS
        private static readonly bool? s_env = Environment.GetEnvironmentVariable("CUO_MOBILE_WALL_ORDER") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        /// <summary>CUO_MOBILE_WALL_ORDER_CHECK=1: the scan runs with the switch off too, and every strip the rule moves or would
        /// move is logged.</summary>
        internal static readonly bool Check = Environment.GetEnvironmentVariable("CUO_MOBILE_WALL_ORDER_CHECK") == "1";
#else
        internal const bool Check = false;
#endif

        public static bool On
        {
            get
            {
                if (OverrideForTests.HasValue)
                {
                    return OverrideForTests.Value;
                }

#if CUO_DEVTOOLS
                if (s_env.HasValue)
                {
                    return s_env.Value;
                }
#endif
                return Settings.GlobalSettings != null && Settings.GlobalSettings.MobileWallOrder;
            }
        }

        /// <summary>The rule applies: the switch is on and the depth key draws (depth_model has its own per-pixel walls).</summary>
        public static bool Rule => On && !DepthModel.On;

        public const int MaxWalls = 6;

        /// <summary>Pixels of each half next to the north vertex column that do not count (a wall's art is a few pixels thick
        /// across its vertex column).</summary>
        public const int VertexMargin = 3;

        public struct Wall
        {
            /// <summary>The wall's depth key D (100 (x + y) + 127 + PriorityZ, as baked).</summary>
            public int D;

            /// <summary>Its in-front columns [Lo, Hi) relative to the depth tile's centre column.</summary>
            public short Lo, Hi;

            public ushort Graphic, X, Y;
            public sbyte Z;
            public byte Height;
        }

        [InlineArray(MaxWalls)]
        public struct WallList
        {
            private Wall _e;
        }

        /// <summary>One mobile's scan: the walls in front of it whose height holds its z.</summary>
        public struct Scan : IEquatable<Scan>
        {
            /// <summary>The rule applies (<see cref="Rule"/>); false with only the checker on.</summary>
            public bool Active;

            /// <summary>Walls found (at most <see cref="MaxWalls"/>; <see cref="Dropped"/> more were not kept).</summary>
            public byte Count, Dropped;

            /// <summary>The depth tile's column less the mobile tile's, in tiles (a step's bump, View.CalculateDepthZ).</summary>
            public sbyte Du;

            public WallList Walls;

            public bool Equals(Scan o)
            {
                if (Active != o.Active || Count != o.Count || Du != o.Du)
                {
                    return false;
                }

                for (int i = 0; i < Count; i++)
                {
                    ref readonly Wall a = ref Walls[i];
                    ref readonly Wall b = ref o.Walls[i];

                    if (a.D != b.D || a.Lo != b.Lo || a.Hi != b.Hi)
                    {
                        return false;
                    }
                }

                return true;
            }

            public override bool Equals(object obj) => obj is Scan s && Equals(s);

            public override int GetHashCode() => Count | (Active ? 256 : 0);
        }

        // the tiles ahead of the depth tile and the in-front half of each (columns relative to the depth tile's centre column)
        private static readonly sbyte[] s_dx = { 1, 0, 1, 2, 0 };
        private static readonly sbyte[] s_dy = { 0, 1, 1, 0, 2 };
        private static readonly short[] s_lo = { 0, -22 + VertexMargin, -22, 22, -44 + VertexMargin };
        private static readonly short[] s_hi = { 22 - VertexMargin, 0, 22, 44 - VertexMargin, -22 };

        /// <summary>
        /// The scan for <paramref name="m"/> this frame (MobileAnimationSystem.SelectCached; part of the select key): empty
        /// unless the rule or the checker is on.
        /// </summary>
        public static Scan KeyScan(Mobile m)
        {
            bool rule = Rule;
            Scan s = default;

            if (!rule && !Check)
            {
                return s;
            }

            s.Active = rule;
            Map.Map map = m.World?.Map;

            if (map != null)
            {
                DepthTile(m.X, m.Y, m.Offset, out int bx, out int by);
                s.Du = (sbyte)((bx - by) - (m.X - m.Y));
                ScanAt(map, bx, by, m.Z, ref s);
            }

#if CUO_DEVTOOLS
            if (Check)
            {
                Tick(rule, s.Count != 0, s.Dropped);
            }
#endif

            return s;
        }

        /// <summary>The tile View.CalculateDepthZ sorts a mobile on: its own, or the next one while it steps south or east.</summary>
        public static void DepthTile(int x, int y, Vector3 offset, out int bx, out int by)
        {
            bx = x;
            by = y;

            if (offset.X > 0 && offset.Y >= 0)
            {
                // north-east, east
                bx++;
            }
            else if (offset.X == 0 && offset.Y > 0)
            {
                // south-east
                bx++;
                by++;
            }
            else if (offset.X < 0 && offset.Y >= 0)
            {
                // south, south-west
                by++;
            }
        }

        /// <summary>Fills <paramref name="s"/> with the walls ahead of depth tile (<paramref name="bx"/>, <paramref name="by"/>)
        /// that hold z <paramref name="mz"/>.</summary>
        internal static void ScanAt(Map.Map map, int bx, int by, int mz, ref Scan s)
        {
            StaticTiles[] data = Chunk.StaticTileData;
            SpriteEntry[] sprites = SpritesForTests ?? Client.Game?.UO?.FileManager?.Arts?.PackSpriteEntries;

            for (int t = 0; t < s_dx.Length; t++)
            {
                int tx = bx + s_dx[t], ty = by + s_dy[t];
                Chunk chunk = map.GetChunk(tx, ty, false);

                if (chunk == null)
                {
                    continue;
                }

                int c = (s_dx[t] - s_dy[t]) * 22;

                for (GameObject o = TileWalk.Begin(chunk, (tx & 7) << 3 | (ty & 7), out TileWalk walk); o != null; o = walk.Next())
                {
                    if (o is Item it)
                    {
                        if (it.IsDestroyed || !it.OnGround || it.IsCorpse || it.IsMulti)
                        {
                            continue;
                        }
                    }
                    else if (o is not Static && o is not Multi)
                    {
                        continue;
                    }

                    ushort g = o.Graphic;

                    if (g >= data.Length)
                    {
                        continue;
                    }

                    ref StaticTiles td = ref data[g];

                    if (!td.IsWall || mz < o.Z || mz >= o.Z + td.Height)
                    {
                        continue;
                    }

                    int lo = s_lo[t], hi = s_hi[t];

                    // the art's opaque columns: View.DrawStatic puts the art's centre column (OrigW >> 1) on the north vertex
                    int idx = PackConst.StaticSpriteBase + g;

                    if (sprites != null && idx < sprites.Length && sprites[idx].Present && sprites[idx].OrigW != 0)
                    {
                        ref readonly SpriteEntry sp = ref sprites[idx];
                        int left = c - (sp.OrigW >> 1) + sp.TrimX;
                        lo = Math.Max(lo, left);
                        hi = Math.Min(hi, left + sp.W);
                    }

                    if (lo >= hi)
                    {
                        continue;
                    }

                    if (s.Count >= MaxWalls)
                    {
                        s.Dropped++;

                        continue;
                    }

                    ref Wall w = ref s.Walls[s.Count++];
                    w.D = 100 * (tx + ty) + 127 + o.PriorityZ;
                    w.Lo = (short)lo;
                    w.Hi = (short)hi;
                    w.Graphic = g;
                    w.X = (ushort)tx;
                    w.Y = (ushort)ty;
                    w.Z = o.Z;
                    w.Height = td.Height;
                }
            }
        }

        /// <summary>
        /// The add (MobileSpriteExtract.DepthOf kind 1: depth + 1 + add) of a strip below the anchor whose frame covers columns
        /// [<paramref name="lo"/>, <paramref name="hi"/>) relative to the depth tile's centre column: <paramref name="add"/>, or
        /// less so the strip's key is one below the lowest wall of <paramref name="s"/> it overlaps (never below 0, the upper
        /// strip's). <paramref name="wall"/>: that wall's index, −1 when none overlaps.
        /// </summary>
        public static float CapAdd(float add, in Scan s, int lo, int hi, float depth, out int wall)
        {
            wall = -1;
            int minD = int.MaxValue;

            for (int i = 0; i < s.Count; i++)
            {
                ref readonly Wall w = ref s.Walls[i];

                if (lo < w.Hi && hi > w.Lo && w.D < minD)
                {
                    minD = w.D;
                    wall = i;
                }
            }

            if (wall < 0)
            {
                return add;
            }

            // DepthOf's key is round((depth + 1 + add) * 100) − 50; D − 1 needs depth + 1 + add = (D + 49) / 100
            float cap = (float)((minD + 49) / 100.0 - (double)(depth + 1f));

            if (cap < 0f)
            {
                cap = 0f;
            }

            return cap < add ? cap : add;
        }

#if CUO_DEVTOOLS
        private static long _scans, _withWalls, _dropped, _moved, _movedApplied, _nextSummary;
        private static int _logged;

        private static void Tick(bool rule, bool walls, int dropped)
        {
            _scans++;
            _dropped += dropped;

            if (walls)
            {
                _withWalls++;
            }

            long now = Environment.TickCount64;

            if (now < _nextSummary)
            {
                return;
            }

            if (_nextSummary != 0)
            {
                Log.Info($"[mobile-wall-check] summary: mobile_wall_order={On}, depth_model={DepthModel.On}, rule={rule}, scans {_scans}, " +
                    $"with a wall ahead {_withWalls}, walls dropped {_dropped}, strips moved {_moved} (applied {_movedApplied})");
            }

            _nextSummary = now + 5000;
        }

        /// <summary>A strip the rule moves (<paramref name="applied"/>) or would move: logged (first 50) and counted.</summary>
        internal static void NoteMoved(in Scan s, int wall, int mx, int my, int mz, ushort body, int strip, int rows, int lo, int hi, float depth,
            float oldAdd, float newAdd)
        {
            _moved++;

            if (s.Active)
            {
                _movedApplied++;
            }

            if (_logged++ >= 50)
            {
                return;
            }

            ref readonly Wall w = ref s.Walls[wall];
            int oldD = (int)MathF.Round((depth + 1f + oldAdd) * 100f) - 50;
            int newD = (int)MathF.Round((depth + 1f + newAdd) * 100f) - 50;
            Log.Info($"[mobile-wall-check] mobile {mx},{my},{mz} body {body} strip {strip} ({rows} rows, columns {lo}..{hi}) " +
                $"{(s.Active ? "moved" : "would move")} D {oldD} -> {newD}: wall 0x{w.Graphic:X4} at {w.X},{w.Y},{w.Z} h {w.Height} D {w.D} " +
                $"columns {w.Lo}..{w.Hi}");
        }
#endif
    }
}
