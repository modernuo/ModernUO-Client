// SPDX-License-Identifier: BSD-2-Clause

using System.Runtime.ConstrainedExecution;

namespace Uo.Buffers;

/// <summary>
/// A counter that moves once per gen2 GC. Pools read it on their own thread and trim when it changed, so the GC
/// finalizer thread never touches a single-threaded pool.
/// </summary>
public static class Gen2Epoch
{
    private static int _value;

    /// <summary>Changes after each gen2 GC.</summary>
    public static int Value => Volatile.Read(ref _value);

    static Gen2Epoch() => _ = new Sentinel(GC.CollectionCount(2));

    private sealed class Sentinel : CriticalFinalizerObject
    {
        private int _gen2;

        public Sentinel(int gen2) => _gen2 = gen2;

        ~Sentinel()
        {
            if (Environment.HasShutdownStarted)
            {
                return;
            }

            // The sentinel is finalized after gen0/gen1 collections too; only a changed gen2 count moves the epoch.
            int now = GC.CollectionCount(2);

            if (now != _gen2)
            {
                _gen2 = now;
                Interlocked.Increment(ref _value);
            }

            GC.ReRegisterForFinalize(this);
        }
    }
}
