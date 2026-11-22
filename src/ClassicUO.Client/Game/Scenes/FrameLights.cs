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

            // light_occlusion: the source (its own emitter: the glow passes through it) and its tile (LightOcclusion)
            public GameObjects.GameObject Source;
            public ushort SrcX,
                SrcY;
            public sbyte SrcZ;
        }

        /// <summary>view_margin: the safety bound on one frame's lights. The list grows to its high-water mark and is reused
        /// (no per-frame allocation); past the bound a light replaces the entry of lowest priority (in view first, then
        /// nearest the player: <see cref="Priority"/>). The classic cap (LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT, 100) kept
        /// the walk's first 100, so in a full-size town view one lamp turned off let another, unlit before, take its place.</summary>
        public const int SafetyBound = 4096;

        private Entry[] _entries;
        private long[] _priority;
        private int _count, _limit;
        private uint _frame;
        private bool _ranked;

        public FrameLights(int capacity)
        {
            _entries = new Entry[capacity + 1];
            _limit = capacity;
        }

        public int Count => _count;

        /// <summary>The lights this frame may hold (BeginFrame).</summary>
        public int Limit => _limit;

        /// <summary>Without priorities (the classic cap): the frame holds its limit and refuses more lights.</summary>
        public bool Full => !_ranked && _count >= _limit;

        /// <summary>The entries allocated (the high-water mark plus the scratch entry).</summary>
        public int Capacity => _entries.Length;

        /// <summary>Lights offered (GameScene.AddLight) while the frame was full: dropped by the cap (with priorities: the
        /// lights replaced or not taken past the bound).</summary>
        public int Refused;

        public ref Entry this[int index] => ref _entries[index];

        /// <summary>A drawn frame starts: the lights of earlier frames are dropped.</summary>
        public void BeginFrame(uint frame) => BeginFrame(frame, _entries.Length - 1, false);

        /// <summary><see cref="BeginFrame(uint)"/> with at most <paramref name="limit"/> lights. <paramref name="ranked"/>
        /// (view_margin): the list grows up to the limit and keeps the best lights past it (<see cref="Commit(long)"/>);
        /// otherwise the limit is clamped to the entries allocated and the frame refuses lights past it.</summary>
        public void BeginFrame(uint frame, int limit, bool ranked = false)
        {
            // light_occlusion: no source kept past the frame that collected it (review P2 #7); the scratch entry too
            for (int i = 0, end = System.Math.Min(_count + 1, _entries.Length); i < end; i++)
            {
                _entries[i].Source = null;
            }

            _entries[_entries.Length - 1].Source = null;
            _frame = frame;
            _count = 0;
            _ranked = ranked;
            _limit = ranked ? System.Math.Max(limit, 0) : System.Math.Clamp(limit, 0, _entries.Length - 1);
            Refused = 0;
        }

        /// <summary>The next free entry (past a ranked frame's bound: a scratch entry); <see cref="Commit()"/> keeps it.
        /// Unranked callers check <see cref="Full"/> first.</summary>
        public ref Entry Next()
        {
            int i = System.Math.Min(_count, _limit);

            if (i >= _entries.Length)
            {
                // grows to the high-water mark (doubling, at most the bound and its scratch entry); reused afterwards
                System.Array.Resize(ref _entries, System.Math.Max(i + 1, System.Math.Min(_entries.Length * 2, _limit + 1)));
            }

            return ref _entries[i];
        }

        public void Commit() => Commit(0);

        /// <summary>Keeps the entry <see cref="Next"/> returned. A ranked frame past its bound replaces the kept entry of
        /// largest <paramref name="priority"/> when this one's is smaller, else drops it (both count as <see cref="Refused"/>).</summary>
        public void Commit(long priority)
        {
            if (!_ranked)
            {
                _count++;

                return;
            }

            if (_count < _limit)
            {
                EnsurePriority(_count + 1);
                _priority[_count++] = priority;

                return;
            }

            Refused++;
            int worst = -1;
            long worstP = priority;

            for (int i = 0; i < _count; i++)
            {
                if (_priority[i] > worstP)
                {
                    worstP = _priority[i];
                    worst = i;
                }
            }

            if (worst >= 0)
            {
                _entries[worst] = _entries[_limit];
                _priority[worst] = priority;
            }
        }

        /// <summary>A light's rank past the bound (smaller is kept first): lights whose centre is in the view (left, top,
        /// right, bottom) before the others, then by squared distance from (px, py).</summary>
        public static long Priority(int x, int y, int left, int top, int right, int bottom, int px, int py)
        {
            long dx = x - px, dy = y - py;
            long d = dx * dx + dy * dy;

            return x >= left && x <= right && y >= top && y <= bottom ? d : d + (1L << 40);
        }

        private void EnsurePriority(int n)
        {
            if (_priority == null || _priority.Length < n)
            {
                System.Array.Resize(ref _priority, System.Math.Max(n, System.Math.Max(16, (_priority?.Length ?? 0) * 2)));
            }
        }

        public void Truncate(int count) => _count = System.Math.Min(_count, System.Math.Max(count, 0));

        /// <summary>The lights to draw in frame <paramref name="frame"/>: only the ones its own walk collected.</summary>
        public int CountFor(uint frame) => _frame == frame ? _count : 0;
    }
}
