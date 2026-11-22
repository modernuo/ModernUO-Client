// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// The lights one drawn frame's world walk collects (GameScene.AddLight), in that frame's screen positions (world pixel
    /// minus the frame's draw offset). The light pass draws them after the walk of the same frame: a list drawn a frame later
    /// was placed with the previous frame's offset, so while the camera moves the lights trail the world by the distance it
    /// moved in between, which changes with every frame's length and flickers when draws are short and uneven (uncapped FPS).
    /// </summary>
    internal sealed class FrameLights
    {
        internal struct Entry
        {
            public byte ID;
            public ushort Color;
            public bool IsHue;
            public int DrawX,
                DrawY;
        }

        /// <summary>view_margin: the lights a frame may hold (the classic cap is LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT, 100,
        /// which a full-size town view exceeds: the walk's first 100 were kept, so lamps came and went with the position).</summary>
        public const int WideCapacity = 512;

        private readonly Entry[] _entries;
        private int _count, _limit;
        private uint _frame;

        public FrameLights(int capacity)
        {
            _entries = new Entry[capacity];
            _limit = capacity;
        }

        public int Count => _count;

        /// <summary>The lights this frame may hold (BeginFrame).</summary>
        public int Limit => _limit;

        public bool Full => _count >= _limit;

        /// <summary>Lights offered (GameScene.AddLight) while the frame was full: dropped by the cap.</summary>
        public int Refused;

        public ref Entry this[int index] => ref _entries[index];

        /// <summary>A drawn frame starts: the lights of earlier frames are dropped.</summary>
        public void BeginFrame(uint frame) => BeginFrame(frame, _entries.Length);

        /// <summary><see cref="BeginFrame(uint)"/> with at most <paramref name="limit"/> lights (clamped to the capacity).</summary>
        public void BeginFrame(uint frame, int limit)
        {
            _frame = frame;
            _count = 0;
            _limit = System.Math.Clamp(limit, 0, _entries.Length);
            Refused = 0;
        }

        /// <summary>The next free entry; <see cref="Commit"/> keeps it. Callers check <see cref="Full"/> first.</summary>
        public ref Entry Next() => ref _entries[_count];

        public void Commit() => _count++;

        public void Truncate(int count) => _count = System.Math.Min(_count, System.Math.Max(count, 0));

        /// <summary>The lights to draw in frame <paramref name="frame"/>: only the ones its own walk collected.</summary>
        public int CountFor(uint frame) => _frame == frame ? _count : 0;
    }
}
