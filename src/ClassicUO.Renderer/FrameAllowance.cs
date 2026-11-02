using System;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// A frame's main-thread time for art uploads and prefetch waits (art_container_prefetch; Art.BeginFrame). It lasts
    /// one frame: an allowance nobody renewed expires, so time left over from an earlier frame is never spent later.
    /// While it is active it also bounds the chunk loader's upload drain. All times are Stopwatch ticks. Main thread.
    /// </summary>
    public sealed class FrameAllowance
    {
        private long _left;
        private long _expires;

        public void Begin(long allowanceTicks, long now, long lifetimeTicks)
        {
            _left = allowanceTicks;
            _expires = now + lifetimeTicks;
        }

        /// <summary>A frame granted an allowance and it has not expired (whether or not any is left).</summary>
        public bool IsActive(long now) => now < _expires;

        public long Remaining(long now) => IsActive(now) ? Math.Max(0, _left) : 0;

        public void Spend(long ticks) => _left -= ticks;

        /// <summary>
        /// The deadline of an upload drain due by <paramref name="deadline"/>: unchanged when no allowance is active,
        /// otherwise no later than the allowance's end. False when the allowance is spent: no upload may start.
        /// </summary>
        public bool TryClampDrain(long deadline, long now, out long clamped)
        {
            clamped = deadline;

            if (!IsActive(now))
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
