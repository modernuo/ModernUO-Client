// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Uo.Pack;

namespace ClassicUO.Assets
{
    public sealed class AnimDataLoader : UOFileLoader
    {
        private readonly Dictionary<ushort, AnimDataFrame> _frames = new Dictionary<ushort, AnimDataFrame>();

        public AnimDataLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        public int Count => _frames.Count;

        public override unsafe void Load()
        {
            _frames.Clear();

            foreach (var e in AnimDataFile.Read(FileManager.Pack.PathOf("anim/animdata.bin")))
            {
                var frame = new AnimDataFrame
                {
                    Unknown = e.Unknown,
                    FrameCount = e.FrameCount,
                    FrameInterval = e.FrameInterval,
                    FrameStart = e.FrameStart
                };

                ReadOnlySpan<sbyte> src = e.Frames;

                for (int i = 0; i < 64; i++)
                {
                    frame.FrameData[i] = src[i];
                }

                _frames[e.Graphic] = frame;
            }
        }

        public bool TryGetAnimData(ushort graphic, out AnimDataFrame frame) => _frames.TryGetValue(graphic, out frame);

        public AnimDataFrame CalculateCurrentGraphic(ushort graphic)
        {
            _frames.TryGetValue(graphic, out var frame);

            return frame;
        }
    }


    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct AnimDataFrame
    {
        public fixed sbyte FrameData[64];
        public byte Unknown;
        public byte FrameCount;
        public byte FrameInterval;
        public byte FrameStart;
    }
}
