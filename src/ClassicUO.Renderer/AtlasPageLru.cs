// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Renderer.Animations;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// atlas_lru: the page LRU of one atlas (Art, Gump, Texmap, Light, font glyphs) with the anim atlas's policy
    /// (<see cref="AtlasLruPolicy"/>). Pages are tagged <see cref="AtlasPage"/> and touched by every draw that uses them
    /// (the batcher, the Gpu world's emit, chunk meshes). <see cref="Tick"/>, once per frame before anything is drawn, frees
    /// the least recently used pages above the budget; the owner's drop callback first forgets every entry on the page (its
    /// next lookup decodes and uploads it again). Main thread only.
    /// </summary>
    public sealed class AtlasPageLru
    {
        public readonly string Name;
        public readonly AtlasLruPolicy Policy;
        private readonly IAtlasPages _pages;
        private readonly long _pageBytes;
        // forgets the entries of a page about to be freed: the page and the keys uploaded into it (Uploaded)
        private readonly Action<Texture2D, List<uint>> _drop;
        // keys of the evicted entries not loaded again yet, with the eviction time (the thrash test of the next upload)
        private readonly Dictionary<uint, long> _evictedAt = new Dictionary<uint, long>();
        private long[] _use = Array.Empty<long>();
        private int[] _victims = Array.Empty<int>();
        private long _logAt, _logged = -1;

        public AtlasPageLru(string name, IAtlasPages pages, long pageBytes, AtlasLruPolicy policy, Action<Texture2D, List<uint>> drop)
        {
            Name = name;
            _pages = pages;
            _pageBytes = pageBytes;
            Policy = policy;
            _drop = drop;
        }

        public int Pages => _pages.TexturesCount;

        /// <summary>Evicted keys remembered for the thrash test (pruned past the thrash window at each eviction pass).</summary>
        public int RememberedEvictions => _evictedAt.Count;

        /// <summary>
        /// The entry <paramref name="key"/> was uploaded into <paramref name="page"/>: the page lists it and counts as used now;
        /// an evicted key loaded again is a reload (a thrash reload within the policy's window may grow the budget). With
        /// CUO_ATLAS_LRU_CHECK, a reloaded entry must have its size before eviction.
        /// </summary>
        public void Uploaded(Texture2D page, uint key, int width, int height)
        {
            if (page?.Tag is AtlasPage p)
            {
                if (p.LastKey != key)
                {
                    p.LastKey = key;
                    p.Directions.Add(key);
                }

                p.LastUseMs = AtlasPageClock.NowMs;
            }

            if (_evictedAt.Count != 0 && _evictedAt.Remove(key, out long evictedAt))
            {
                Policy.OnReload(evictedAt, AtlasPageClock.NowMs);
#if CUO_DEVTOOLS
                if (Check)
                {
                    CheckReloaded(key, width, height);
                }
#endif
            }
        }

        /// <summary>The drop callback forgot entry <paramref name="key"/> (it was on the page being freed).</summary>
        public void Forgot(uint key, int width, int height)
        {
            _evictedAt[key] = AtlasPageClock.NowMs;
            Policy.EvictedDirections++;
#if CUO_DEVTOOLS
            if (Check)
            {
                _evictedSizes[(Name, key)] = (width, height);
            }
#endif
        }

        /// <summary>The drop callback forgot an entry it has no key for (font glyphs): counted, not remembered for reloads.</summary>
        public void ForgotUnkeyed() => Policy.EvictedDirections++;

        /// <summary>Once per frame, before anything is drawn: moves the budget and frees the pages above it.</summary>
        public void Tick(long nowMs, bool unfocused)
        {
            Policy.Tick(nowMs, unfocused);
            int count = _pages.TexturesCount;

            if (count > Policy.BudgetPages)
            {
                if (_use.Length < count)
                {
                    _use = new long[count * 2];
                    _victims = new int[count * 2];
                }

                for (int i = 0; i < count; i++)
                {
                    _use[i] = _pages.GetPage(i).Tag is AtlasPage page ? page.LastUseMs : nowMs;
                }

                int n = Policy.SelectVictims(_use.AsSpan(0, count), count - 1, nowMs, _victims);

                if (n > 0)
                {
                    // highest index first: removing a page shifts the ones after it
                    Array.Sort(_victims, 0, n);

                    for (int k = n - 1; k >= 0; k--)
                    {
                        EvictPage(_victims[k]);
                    }

                    PruneEvicted(nowMs);
                }
            }

            Log(nowMs);
        }

        private void EvictPage(int index)
        {
            Texture2D texture = _pages.GetPage(index);
            var page = texture.Tag as AtlasPage;
            _drop(texture, page?.Directions);
            page?.Directions.Clear();
            PackSpriteMap.Unregister(texture);
            _pages.RemovePage(index);
            Policy.EvictedPages++;
            AtlasPageClock.FreedEpoch++;
            TextureUploads.Changed();
        }

        // an entry evicted longer ago than the thrash window can't make a thrash reload: forget it (bounds the map)
        private void PruneEvicted(long nowMs)
        {
            if (_evictedAt.Count < 1024)
            {
                return;
            }

            foreach (var pair in _evictedAt)
            {
                if (nowMs - pair.Value >= Policy.ThrashWindowMs)
                {
                    _evictedAt.Remove(pair.Key);
                }
            }
        }

        public string State()
        {
            int pages = _pages.TexturesCount;
            long mib = _pageBytes >> 20;

            return $"{Name}: pages {pages} ({pages * mib} MiB), budget {Policy.BudgetPages} (low {Policy.LowWater}, floor {Policy.FloorPages}, ceiling {Policy.CeilingPages}; page {mib} MiB), " +
                   $"evicted {Policy.EvictedPages} pages / {Policy.EvictedDirections} sprites, reloads {Policy.Reloads} (thrash {Policy.ThrashReloads}), grows {Policy.Grows}, shrinks {Policy.Shrinks}";
        }

        private void Log(long nowMs)
        {
            long changes = Policy.EvictedPages + Policy.Reloads + Policy.Grows + Policy.Shrinks + _pages.TexturesCount;
            bool check = false;
#if CUO_DEVTOOLS
            check = Check;
#endif
            if (nowMs < _logAt || (changes == _logged && !check))
            {
                return;
            }

            _logAt = nowMs + 5000;
            _logged = changes;
            Utility.Logging.Log.Info($"[atlas-lru] {State()}");
        }

#if CUO_DEVTOOLS
        // CUO_ATLAS_LRU_CHECK: an evicted entry loads again with the same size, and no draw names a freed page
        public static bool Check = Environment.GetEnvironmentVariable("CUO_ATLAS_LRU_CHECK") == "1";

        public static long CheckCompared, CheckUses, CheckMismatches;

        /// <summary>Tests: sees each mismatch line.</summary>
        public static Action<string> MismatchObserver;

        public static string CheckSummary() =>
            $"[atlas-lru-check] {CheckCompared} reloads compared, {CheckUses} uses validated, {CheckMismatches} mismatches (atlas_lru=on)";

        private static readonly Dictionary<(string, uint), (int W, int H)> _evictedSizes = new Dictionary<(string, uint), (int, int)>();

        private void CheckReloaded(uint key, int width, int height)
        {
            if (!_evictedSizes.Remove((Name, key), out var size))
            {
                return;
            }

            CheckCompared++;

            if (size != (width, height))
            {
                Mismatch($"[atlas-lru-check] {Name} 0x{key:X}: reloaded {width}x{height}, {size.W}x{size.H} before eviction");
            }
        }

        /// <summary>CUO_ATLAS_LRU_CHECK: a texture about to be drawn must not be a freed atlas page.</summary>
        public static void CheckUse(Texture texture, string where)
        {
            if (texture == null)
            {
                return;
            }

            CheckUses++;

            if (texture.IsDisposed && texture.Tag is AtlasPage page)
            {
                Mismatch($"[atlas-lru-check] {where}: draws from a freed atlas page (serial {page.Serial})");
            }
        }

        private static void Mismatch(string line)
        {
            CheckMismatches++;
            MismatchObserver?.Invoke(line);

            if (CheckMismatches <= 50)
            {
                Utility.Logging.Log.Warn(line);
            }
        }
#endif
    }
}
