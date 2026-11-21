// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Threading;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// asset_ready_wake: a worker decode (art, texmap, gump, animation direction) was published to its ready queue. Workers
    /// call <see cref="Published"/> as their last step; the client loop (AssetWake) installs <see cref="Ready"/> to wake its
    /// event-loop wait, so a frame that drew a missing sprite is followed by one that draws it. No allocation.
    /// </summary>
    public static class AssetSignal
    {
        private static int _published;

        /// <summary>Any thread: called after each publish (null: nobody listens).</summary>
        public static Action Ready;

        /// <summary>Publishes so far (any thread; wraps).</summary>
        public static int PublishedCount => Volatile.Read(ref _published);

        public static void Published()
        {
            Interlocked.Increment(ref _published);
            Volatile.Read(ref Ready)?.Invoke();
        }
    }
}
