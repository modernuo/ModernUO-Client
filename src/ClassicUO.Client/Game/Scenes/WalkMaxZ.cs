// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// clip-1: the walk's max-Z cut. GameScene.AddTileToRenderList ends a tile's whole walk at the first unmeshed static,
    /// multi, item or mobile whose PriorityZ + height exceeds the walk's max Z (150). The tile list is ordered by PriorityZ,
    /// not by PriorityZ + height, so a later object that is lower overall (PriorityZ 102, height 1 after PriorityZ 101,
    /// height 60) was dropped with it. Switch <c>walk_maxz_continue</c> (default off): on, the walk skips only the object
    /// over the cut and goes on with the tile. <c>CUO_WALK_MAXZ=1|0</c> overrides the switch and <c>CUO_WALK_MAXZ_CHECK=1</c>
    /// compares the drawn set with the old walk's, in developer builds.
    /// </summary>
    internal static class WalkMaxZ
    {
        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

#if CUO_DEVTOOLS
        private static readonly bool? s_env = Environment.GetEnvironmentVariable("CUO_WALK_MAXZ") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_WALK_MAXZ_CHECK") == "1";
#else
        public const bool Check = false;
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
                return Settings.GlobalSettings != null && Settings.GlobalSettings.WalkMaxZContinue;
            }
        }

        /// <summary>The walk met an object over the max Z: whether it ends the tile's walk (off: yes; on: only this object is skipped).</summary>
        public static bool EndsWalk() => !On;

        /// <summary>
        /// The tile's objects after a cut (in walk order: <paramref name="walk"/>, a copy of the merged walk, else the list from
        /// <paramref name="next"/>, the cut's TNext; null for a single-object walk) that are not over <paramref name="maxZ"/>
        /// themselves, so the old walk dropped them only because of the cut.
        /// </summary>
        public static void AfterCut(TileListObject next, bool merged, TileWalk walk, int maxZ, StaticTiles[] data, List<GameObject> into)
        {
            for (GameObject o = merged ? walk.Next() : next; o != null; o = merged ? walk.Next() : ((TileListObject)o).TNext)
            {
                if (TopZ(o, data) <= maxZ)
                {
                    into.Add(o);
                }
            }
        }

        /// <summary>PriorityZ + the walk's height of the object (GameScene.CalculateObjectHeight; mobiles: the character height).</summary>
        public static int TopZ(GameObject o, StaticTiles[] data)
        {
            int z = o.PriorityZ;

            switch (o)
            {
                case Mobile:
                    return z + Constants.DEFAULT_CHARACTER_HEIGHT;
                case Static or Multi:
                    GameScene.CalculateObjectHeight(ref z, ref data[o.Graphic]);

                    return z;
                case Item item:
                    GameScene.CalculateObjectHeight(ref z, ref data[item.IsMulti ? item.MultiGraphic : item.Graphic]);

                    return z;
                default:
                    return z;
            }
        }

#if CUO_DEVTOOLS
        // CUO_WALK_MAXZ_CHECK: the objects behind each cut this frame, judged after the walk (FrameEnd): drawn = the new walk
        // drew it (switch on: a mismatch with the old walk, which dropped it); not drawn with the switch off = dropped by the cut
        private static readonly List<GameObject> _pending = new List<GameObject>();
        private static readonly List<GameObject> _pendingCut = new List<GameObject>();
        private static long _cuts, _behind, _mismatches, _dropped;
        private static long _nextSummary;

        public static void NoteCut(GameObject cut, int topZ, TileListObject next, bool merged, TileWalk walk, int maxZ)
        {
            _cuts++;
            int start = _pending.Count;
            AfterCut(next, merged, walk, maxZ, Client.Game.UO.FileManager.TileData.StaticData, _pending);

            for (int i = start; i < _pending.Count; i++)
            {
                _pendingCut.Add(cut);
            }

            if (_pending.Count > start && _cuts <= 20)
            {
                Log.Info($"[walk-maxz] cut at {cut.GetType().Name} 0x{cut.Graphic:X4} {cut.X},{cut.Y},{cut.Z} pz {cut.PriorityZ} top {topZ} > {maxZ}: {_pending.Count - start} lower object(s) behind it");
            }
        }

        public static void FrameEnd(uint drawFrame)
        {
            if (!Check)
            {
                return;
            }

            bool on = On;

            for (int i = 0; i < _pending.Count; i++)
            {
                GameObject o = _pending[i];
                GameObject cut = _pendingCut[i];
                bool drawn = o.DrawnFrame == drawFrame;
                _behind++;

                if (drawn && on)
                {
                    if (++_mismatches <= 50)
                    {
                        Log.Warn($"[walk-maxz] mismatch: {o.GetType().Name} 0x{o.Graphic:X4} {o.X},{o.Y},{o.Z} pz {o.PriorityZ} drawn by the new walk, dropped by the old (cut by 0x{cut.Graphic:X4} {cut.X},{cut.Y},{cut.Z} pz {cut.PriorityZ})");
                    }
                }
                else if (!drawn && !on)
                {
                    if (++_dropped <= 50)
                    {
                        Log.Warn($"[walk-maxz] dropped by the cut: {o.GetType().Name} 0x{o.Graphic:X4} {o.X},{o.Y},{o.Z} pz {o.PriorityZ} (cut by 0x{cut.Graphic:X4} {cut.X},{cut.Y},{cut.Z} pz {cut.PriorityZ})");
                    }
                }
            }

            _pending.Clear();
            _pendingCut.Clear();

            long now = Environment.TickCount64;

            if (now >= _nextSummary)
            {
                _nextSummary = now + 10000;
                Log.Info($"[walk-maxz] summary: walk_maxz_continue={on}, cuts {_cuts}, lower objects behind cuts {_behind}, mismatches (drawn by new, dropped by old) {_mismatches}, dropped by the old cut {_dropped}");
            }
        }
#endif
    }
}
