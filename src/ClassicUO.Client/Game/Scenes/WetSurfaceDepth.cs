// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using Uo.Render;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// Switch <c>wet_surface_depth</c> (default off): a mobile under a water surface (a wet static or land above its z on its
    /// own tile; Felucca 1675 1705, water statics at z -5 over sea-bed land at z -15, where a plain <c>[go</c> puts the player)
    /// is drawn submerged instead of V-cut with floating feet.
    /// <list type="bullet">
    /// <item>depth_model on (both renderers): wet flats (<see cref="SpriteInstance.ZGeoWet"/>) drop <see cref="GeoDepth.WetDrop"/>
    /// instead of <see cref="GeoDepth.FlatDrop"/>, so the water cuts the mobile's billboard near the true waterline (4 px per z
    /// above the centre row) instead of 33 px lower. Gpu: FrameUniforms.DepthModelWet; Legacy: LegacyGeoDepth.WetMask.</item>
    /// <item>depth_model off (the depth key, both renderers): a submerged mobile's strips all take the upper strip's + 1
    /// (MobileSpriteExtract), so the water ahead hides the feet with the legs; the + 3 kept them drawn below the cut.</item>
    /// </list>
    /// <c>CUO_WET_SURFACE_DEPTH=1|0</c> overrides the setting and <c>CUO_WET_DEPTH_CHECK=1</c> logs the mobiles under a wet
    /// surface and the rule each got, with a 5 s summary, in developer builds.
    /// </summary>
    internal static class WetSurfaceDepth
    {
        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

#if CUO_DEVTOOLS
        private static readonly bool? s_env = Environment.GetEnvironmentVariable("CUO_WET_SURFACE_DEPTH") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        /// <summary>CUO_WET_DEPTH_CHECK=1: the scan runs with the switch off too, and every mobile found under a wet surface
        /// is logged with the rule it got.</summary>
        internal static readonly bool Check = Environment.GetEnvironmentVariable("CUO_WET_DEPTH_CHECK") == "1";
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
                return Settings.GlobalSettings != null && Settings.GlobalSettings.WetSurfaceDepth;
            }
        }

        /// <summary>The highest wet surface (a wet static's top, or wet land) in the tile list from <paramref name="head"/> that
        /// lies above <paramref name="z"/>; false when there is none.</summary>
        public static bool WetSurfaceAbove(TileListObject head, int z, out int surfaceZ)
        {
            surfaceZ = int.MinValue;

            for (TileListObject o = head; o != null; o = o.TNext)
            {
                Consider(o, z, ref surfaceZ);
            }

            return surfaceZ != int.MinValue;
        }

        private static void Consider(TileListObject o, int z, ref int surfaceZ)
        {
            int top;

            if (o is Static st)
            {
                // Static.ItemData's row (Chunk.StaticTileData is the loaded array; unit tests set their own)
                StaticTiles[] data = Map.Chunk.StaticTileData;

                if (st.Graphic >= data.Length)
                {
                    return;
                }

                ref StaticTiles td = ref data[st.Graphic];

                if (!td.IsWet)
                {
                    return;
                }

                top = st.Z + td.Height;
            }
            else if (o is Land land)
            {
                if (!land.TileData.IsWet)
                {
                    return;
                }

                top = land.Z;
            }
            else
            {
                return;
            }

            if (top > z && top > surfaceZ)
            {
                surfaceZ = top;
            }
        }

        /// <summary>
        /// Whether the key-mode rule applies to <paramref name="m"/> this frame (switch on, depth_model off, a wet surface above
        /// it on its tile). The tile scan is cached in <paramref name="cache"/> by position and the tile chunk's content
        /// version (the mobile's own tile list cell, GameObject.TileCell), so it runs once per position or tile change.
        /// </summary>
        public static bool KeySubmerged(Mobile m, MobileFrameCache cache)
        {
            bool on = On;
            bool rule = on && !DepthModel.On;

            if (!rule && !Check)
            {
                return false;
            }

            Map.Chunk chunk = m.TileChunk;
            uint version = chunk?.ContentVersion ?? 0;

            if (!cache.WetValid || cache.WetX != m.X || cache.WetY != m.Y || cache.WetZ != m.Z ||
                !ReferenceEquals(cache.WetChunk, chunk) || cache.WetVersion != version)
            {
                cache.WetValid = true;
                cache.WetX = m.X;
                cache.WetY = m.Y;
                cache.WetZ = m.Z;
                cache.WetChunk = chunk;
                cache.WetVersion = version;
                cache.WetUnder = chunk != null && WetSurfaceAbove(chunk.HeadAt(m.TileCell), m.Z, out cache.WetSurfaceZ);

#if CUO_DEVTOOLS
                if (Check)
                {
                    Note(m, cache.WetUnder, cache.WetSurfaceZ, on);
                }
#endif
            }

#if CUO_DEVTOOLS
            if (Check)
            {
                Tick(on);
            }
#endif

            return rule && cache.WetUnder;
        }

#if CUO_DEVTOOLS
        private static long _scans, _under, _ruleOff, _ruleGeo, _ruleKey, _nextSummary;
        private static int _logged;

        private static void Note(Mobile m, bool under, int surfaceZ, bool on)
        {
            _scans++;

            if (!under)
            {
                return;
            }

            _under++;
            string rule;

            if (!on)
            {
                _ruleOff++;
                rule = "off (key + 3 feet strip, or FlatDrop with depth_model)";
            }
            else if (DepthModel.On)
            {
                _ruleGeo++;
                rule = $"depth_model: wet flats drop {GeoDepth.WetDrop} (waterline {4 * (surfaceZ - m.Z)} px above the centre row)";
            }
            else
            {
                _ruleKey++;
                rule = "key: every strip + 1";
            }

            if (_logged++ < 50)
            {
                Log.Info($"[wet-depth-check] mobile 0x{m.Serial:X8} g=0x{m.Graphic:X4} at {m.X},{m.Y},{m.Z} under a wet surface at z {surfaceZ}: {rule}");
            }
        }

        private static void Tick(bool on)
        {
            long now = Environment.TickCount64;

            if (now < _nextSummary)
            {
                return;
            }

            if (_nextSummary != 0)
            {
                Log.Info($"[wet-depth-check] summary: wet_surface_depth={on}, depth_model={DepthModel.On}, scans {_scans}, under a wet surface {_under} " +
                    $"(rule off {_ruleOff}, depth_model {_ruleGeo}, key {_ruleKey})");
            }

            _nextSummary = now + 5000;
        }
#endif
    }
}
