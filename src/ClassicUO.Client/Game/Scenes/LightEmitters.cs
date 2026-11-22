// SPDX-License-Identifier: BSD-2-Clause

using System.Runtime.CompilerServices;
using ClassicUO.Assets;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// view_margin: which static graphics emit light. A graphic emits when its tiledata entry has the Light flag AND its light
    /// index (the entry's Layer) names a light with a shape (LightsLoader.HasShape: below 100, a non-empty texture). Built
    /// once from the loaded pack (tiledata and lights never change at runtime); the chunks' light lists (Chunk.LightSources)
    /// hold only emitters, so a flagged graphic without a shape takes no slot. LightSlotDiag lists the flagged graphics
    /// without one.
    /// </summary>
    internal static class LightEmitters
    {
        private static bool[] _static;

        /// <summary>Tests: a table to use instead of the pack's.</summary>
        internal static bool[] OverrideForTests;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Static(ushort graphic)
        {
            bool[] t = OverrideForTests ?? _static ?? Build();

            return graphic < t.Length && t[graphic];
        }

        /// <summary>The light index <paramref name="id"/> has a drawable shape.</summary>
        public static bool HasShape(int id) => Client.Game.UO.FileManager.Lights.HasShape(id);

        private static bool[] Build()
        {
            StaticTiles[] data = Client.Game.UO.FileManager.TileData.StaticData;
            LightsLoader lights = Client.Game.UO.FileManager.Lights;
            bool[] t = new bool[data.Length];

            for (int g = 0; g < data.Length; g++)
            {
                t[g] = data[g].IsLight && lights.HasShape(data[g].Layer);
            }

            _static = t;

            return t;
        }

        /// <summary>The pack was reloaded: build again on the next use.</summary>
        public static void Reset()
        {
            _static = null;
        }
    }
}
