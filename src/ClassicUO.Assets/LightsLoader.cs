// SPDX-License-Identifier: BSD-2-Clause

using System;
using Uo.Pack;

namespace ClassicUO.Assets
{
    public sealed class LightsLoader : UOFileLoader
    {
        private LightEntry[] _lights = Array.Empty<LightEntry>();
        private byte[] _data = Array.Empty<byte>();
        private byte[] _classes = Array.Empty<byte>();

        public const int MAX_LIGHTS_DATA_INDEX_COUNT = 100;

        public LightsLoader(UOFileManager fileManager) : base(fileManager) { }

        public int Count => _lights.Length;

        /// <summary>The largest width or height of the lights below <paramref name="below"/> (0 before Load).</summary>
        public int MaxExtent(int below)
        {
            int max = 0;

            for (int i = 0, n = Math.Min(below, _lights.Length); i < n; i++)
            {
                max = Math.Max(max, Math.Max((int)_lights[i].Width, _lights[i].Height));
            }

            return max;
        }

        /// <summary>Light <paramref name="id"/> has a shape the light pass can draw (a light below the classic 100 with a
        /// non-empty texture): view_margin's emitter tables admit only these.</summary>
        public bool HasShape(int id) => (uint)id < (uint)Math.Min(MAX_LIGHTS_DATA_INDEX_COUNT, _lights.Length) && _lights[id].Width != 0 && _lights[id].Height != 0;

        /// <summary>Light <paramref name="id"/>'s texture size (0 x 0 without a shape).</summary>
        public void Extent(int id, out int width, out int height)
        {
            if ((uint)id < (uint)_lights.Length)
            {
                width = _lights[id].Width;
                height = _lights[id].Height;
            }
            else
            {
                width = height = 0;
            }
        }

        /// <summary>The pack has lightclass.bin (light_alpha_fade); without it every light is <see cref="LightClass.Unlisted"/>.</summary>
        public bool HasClasses { get; private set; }

        /// <summary>light_alpha_fade: light <paramref name="id"/>'s class from the pack's lightclass.bin (Unlisted when the
        /// table does not list it or the pack has none).</summary>
        public LightClass Class(int id) => (uint)id < (uint)_classes.Length ? (LightClass)_classes[id] : LightClass.Unlisted;

        /// <summary>Tests: the classes table (null: none).</summary>
        public void SetClasses(byte[] classes)
        {
            _classes = classes ?? Array.Empty<byte>();
            HasClasses = classes != null;
        }

        public override void Load()
        {
            (_lights, _data) = LightFile.Read(FileManager.Pack.PathOf(TableConst.LightsFile));
            // optional: packs converted before it load without it (no built-in table; the client falls back to tiledata flags)
            string classes = FileManager.Pack.PathOf(LightClassFile.FileName);
            SetClasses(System.IO.File.Exists(classes) ? LightClassFile.Read(classes) : null);
        }

        public LightInfo GetLight(uint idx)
        {
            if (idx >= _lights.Length)
            {
                return default;
            }

            ref readonly var entry = ref _lights[idx];

            if (entry.Width == 0 || entry.Height == 0)
            {
                return default;
            }

            var texels = _data.AsSpan((int)entry.Offset, entry.Width * entry.Height);
            var buffer = new uint[texels.Length];

            for (int i = 0; i < texels.Length; i++)
            {
                buffer[i] = LightFile.ToRgba8(texels[i]);
            }

            return new LightInfo()
            {
                Pixels = buffer,
                Width = entry.Width,
                Height = entry.Height
            };
        }
    }

    public ref struct LightInfo
    {
        public Span<uint> Pixels;
        public int Width;
        public int Height;
    }
}
