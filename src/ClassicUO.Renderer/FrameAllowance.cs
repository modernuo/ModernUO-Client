using System;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// A frame's main-thread time for art uploads and prefetch waits (art_container_prefetch; Art.BeginFrame). It is valid
    /// only in the frame that granted it: <see cref="AdvanceFrame"/> (the game loop's frame boundary) and <see cref="Clear"/>
    /// (switch off, scene teardown) end it, so time left from an earlier frame is never spent later. Inactive (none granted
    /// this frame) is distinct from active but spent: while it is active it also bounds the chunk loader's upload drain,
    /// and once spent nothing more is uploaded that frame, however long the frame takes. Ticks are Stopwatch ticks. Main thread.
    /// </summary>
    public sealed class FrameAllowance
    {
        private long _left;
        private bool _active;

        public void Begin(long allowanceTicks)
        {
            _left = allowanceTicks;
            _active = true;
        }

        /// <summary>The frame boundary: an allowance nobody renews this frame is gone.</summary>
        public void AdvanceFrame() => _active = false;

        public void Clear()
        {
            _active = false;
            _left = 0;
        }

        /// <summary>This frame granted an allowance (whether or not any is left).</summary>
        public bool IsActive => _active;

        public long Remaining => _active ? Math.Max(0, _left) : 0;

        public void Spend(long ticks)
        {
            if (_active)
            {
                _left -= ticks;
            }
        }

        /// <summary>
        /// The deadline of an upload drain due by <paramref name="deadline"/>: unchanged when no allowance is active,
        /// otherwise no later than the allowance's end. False when the allowance is spent: no upload may start.
        /// </summary>
        public bool TryClampDrain(long deadline, long now, out long clamped)
        {
            clamped = deadline;

            if (!_active)
            {
                return true;
            }

            if (_left <= 0)
            {
                return false;
            }

            clamped = Math.Min(deadline, now + _left);

            return true;
        }
    }
}
