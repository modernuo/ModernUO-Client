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

        private readonly Entry[] _entries;
        private int _count;
        private uint _frame;

        public FrameLights(int capacity)
        {
            _entries = new Entry[capacity];
        }

        public int Count => _count;

        public bool Full => _count >= _entries.Length;

        public ref Entry this[int index] => ref _entries[index];

        /// <summary>A drawn frame starts: the lights of earlier frames are dropped.</summary>
        public void BeginFrame(uint frame)
        {
            _frame = frame;
            _count = 0;
        }

        /// <summary>The next free entry; <see cref="Commit"/> keeps it. Callers check <see cref="Full"/> first.</summary>
        public ref Entry Next() => ref _entries[_count];

        public void Commit() => _count++;

        public void Truncate(int count) => _count = System.Math.Min(_count, System.Math.Max(count, 0));

        /// <summary>The lights to draw in frame <paramref name="frame"/>: only the ones its own walk collected.</summary>
        public int CountFor(uint frame) => _frame == frame ? _count : 0;
    }
}
