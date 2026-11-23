// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Configuration;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// chunk_lights: which static graphics emit light. A graphic emits when its tiledata entry has the Light flag AND the light
    /// GameScene.AddLightCore draws for it has a shape (LightsLoader.HasShape: below 100, a non-empty texture) in either
    /// colored-lights state (<see cref="ShapeOf"/>: light 2 for the fire-field graphics, shape 1 for a light index above 200
    /// with colored lights, else the index). Built once from the loaded pack (tiledata and lights never change at runtime);
    /// the chunks' light lists (Chunk.LightSources) hold only emitters, so a flagged graphic without a shape takes no slot.
    /// Round W2a: the table took only the index itself, so the 175 graphics with an index above 200 (arrow loops 0x00CB at
    /// light 255 and others), which the classic walk lights with colored lights on (the default), had no light with
    /// chunk_lights on. LightSlotDiag lists the flagged graphics without a shape.
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

        private static readonly Func<int, bool> _hasShape = HasShape;

        /// <summary>AddLightCore with colored lights (Profile.UseColoredLights): a light index above this is light 1 tinted
        /// with hue (index - this).</summary>
        public const int ColoredIndexBase = 200;

        /// <summary>The graphics AddLightCore gives light 2 whatever their tiledata light index (fire fields, 0x0B1D).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ForcedLight2(ushort graphic) => graphic >= 0x3E02 && graphic <= 0x3E0B || graphic >= 0x3914 && graphic <= 0x3929 || graphic == 0x0B1D;

        /// <summary>AddLightCore's colored-lights rule: true when <paramref name="index"/> is a colored index, with the shape
        /// (1) and the tint (index - 200) it draws.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ColoredIndex(int index, out byte shape, out ushort color)
        {
            if (index > ColoredIndexBase)
            {
                shape = 1;
                color = (ushort)(index - ColoredIndexBase);

                return true;
            }

            shape = 0;
            color = 0;

            return false;
        }

        /// <summary>The light shape AddLightCore draws for a static or multi component of <paramref name="graphic"/> with
        /// tiledata light index <paramref name="index"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ShapeOf(ushort graphic, int index, bool colored)
        {
            if (ForcedLight2(graphic))
            {
                return 2;
            }

            return colored && ColoredIndex(index, out byte shape, out _) ? shape : index;
        }

        /// <summary>A light-flagged static emits in some colored-lights state (the table's rule).</summary>
        public static bool StaticEmits(ushort graphic, int index, Func<int, bool> hasShape) =>
            hasShape(ShapeOf(graphic, index, false)) || hasShape(ShapeOf(graphic, index, true));

        /// <summary>The band pass: a light-flagged static's light draws a shape with this
        /// frame's colored-lights setting (the light add gives it a slot), independent of the table.</summary>
        public static bool StaticDrawsNow(ushort graphic, int index) => _hasShape(ShapeOf(graphic, index, ColoredNow));

        /// <summary>A light-flagged ground item's light (its LightID) draws a shape with this frame's setting.</summary>
        public static bool ItemDrawsNow(int lightId) => HasShape(ColoredNow && ColoredIndex(lightId, out byte shape, out _) ? shape : lightId);

        private static bool ColoredNow => ProfileManager.CurrentProfile?.UseColoredLights ?? false;

        private static bool[] Build()
        {
            UOFileManager files = Client.Game?.UO?.FileManager;
            StaticTiles[] data = files?.TileData?.StaticData;
            LightsLoader lights = files?.Lights;

            if (data == null || lights == null)
            {
                // no pack loaded (unit tests without one): nothing emits, and the table is built once one is
                return System.Array.Empty<bool>();
            }

            bool[] t = BuildTable(data, lights.HasShape);
            _static = t;

            return t;
        }

        /// <summary>The emitter table for static tiledata <paramref name="data"/> (tests: the pack's tiledata and lights).</summary>
        internal static bool[] BuildTable(StaticTiles[] data, Func<int, bool> hasShape)
        {
            bool[] t = new bool[data.Length];

            for (int g = 0; g < data.Length; g++)
            {
                t[g] = data[g].IsLight && StaticEmits((ushort)g, data[g].Layer, hasShape);
            }

            return t;
        }

        /// <summary>The pack was reloaded: build again on the next use.</summary>
        public static void Reset()
        {
            _static = null;
        }
    }
}
