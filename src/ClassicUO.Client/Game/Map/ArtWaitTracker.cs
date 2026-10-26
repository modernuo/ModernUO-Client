// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// What the chunk loader remembers about a chunk waiting for its first-sight art (Map.RequestBlockArt and
    /// Map.RequestChunkArt), so that waiting costs O(1) per frame:
    /// <list type="bullet">
    /// <item>the first check (or the first after the generation changed: a season change, an atlas reload) scans
    /// everything and requests every missing sprite at once;</item>
    /// <item>later checks resume at the remembered token (the first sprite still missing for a block, the art progress
    /// counter for a loaded chunk) instead of starting over.</item>
    /// </list>
    /// Sprites before a block's position were ready when it was scanned and stay ready: the atlases only lose sprites on
    /// a reload, which changes the generation. Main thread only.
    /// </summary>
    internal sealed class ArtWaitTracker
    {
        private struct State
        {
            public int Token;
            public int Generation;
        }

        private readonly Dictionary<long, State> _states = new Dictionary<long, State>();

        public int Count => _states.Count;

        /// <summary>
        /// The token remembered for <paramref name="key"/> under <paramref name="generation"/>; <paramref name="full"/> is
        /// true (and the token 0) when there is none: scan everything and request all.
        /// </summary>
        public int Begin(long key, int generation, out bool full)
        {
            if (_states.TryGetValue(key, out var s) && s.Generation == generation)
            {
                full = false;

                return s.Token;
            }

            full = true;

            return 0;
        }

        /// <summary>Remembers <paramref name="token"/> for the next check; a negative token (ready) forgets the key.</summary>
        public void End(long key, int generation, int token)
        {
            if (token < 0)
            {
                _states.Remove(key);
            }
            else
            {
                _states[key] = new State { Token = token, Generation = generation };
            }
        }

        public void Forget(long key) => _states.Remove(key);

        /// <summary>Drops everything once more than <paramref name="max"/> keys are kept (chunks that left the queue).</summary>
        public void Trim(int max)
        {
            if (_states.Count > max)
            {
                _states.Clear();
            }
        }

        public void Clear() => _states.Clear();
    }
}
