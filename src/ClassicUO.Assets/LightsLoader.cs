// SPDX-License-Identifier: BSD-2-Clause

using System;
using Uo.Pack;

namespace ClassicUO.Assets
{
    public sealed class LightsLoader : UOFileLoader
    {
        private LightEntry[] _lights = Array.Empty<LightEntry>();
        private byte[] _data = Array.Empty<byte>();

        public const int MAX_LIGHTS_DATA_INDEX_COUNT = 100;

        public LightsLoader(UOFileManager fileManager) : base(fileManager) { }

        public int Count => _lights.Length;

        public override void Load()
        {
            (_lights, _data) = LightFile.Read(FileManager.Pack.PathOf(TableConst.LightsFile));
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
