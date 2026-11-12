// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer.Animations
{
    /// <summary>
    /// anim_atlas_lru: one page of the Legacy animation atlas (set as the page texture's <see cref="GraphicsResource.Tag"/>
    /// while the LRU is on): when it was last drawn from and which cached directions have frames on it.
    /// </summary>
    public sealed class AtlasPage
    {
        public readonly int Serial;
        public long LastUseMs;

        /// <summary>(body, action, dir) keys of the directions uploaded into this page (a direction can also have frames on
        /// the page after it).</summary>
        public readonly List<uint> Directions = new List<uint>();

        internal uint LastKey = uint.MaxValue;

        public AtlasPage(int serial, long nowMs)
        {
            Serial = serial;
            LastUseMs = nowMs;
        }
    }

    /// <summary>The frame clock of the anim atlas LRU: one store per use of a page.</summary>
    public static class AtlasPageClock
    {
        /// <summary>The client's frame time in ms (Animations.Tick sets it once per frame).</summary>
        public static long NowMs;

        /// <summary>Marks the page of <paramref name="texture"/> used this frame; nothing for a texture that is not a tracked
        /// anim atlas page (art, gumps, or the LRU off).</summary>
        public static void Touch(Texture2D texture)
        {
            if (texture?.Tag is AtlasPage page)
            {
                page.LastUseMs = NowMs;
            }
        }

        /// <summary>
        /// Marks every page a direction's frames sit on. Frames are uploaded in order, so the pages form consecutive runs: one
        /// store per run (a direction of large frames on small pages can span more than two pages). No allocation.
        /// </summary>
        public static void TouchFrames(ReadOnlySpan<SpriteInfo> frames)
        {
            Texture2D last = null;

            for (int i = 0; i < frames.Length; i++)
            {
                Texture2D texture = frames[i].Texture;

                if (texture != null && !ReferenceEquals(texture, last))
                {
                    last = texture;
                    Touch(texture);
                }
            }
        }

        /// <summary>The LRU clock: monotonic 64-bit milliseconds (Time.Ticks is a uint that wraps after ~49.7 days).</summary>
        public static long MonotonicMs() => Environment.TickCount64;
    }

    /// <summary>
    /// anim_atlas_lru: the budget and eviction policy of the Legacy animation atlas, without the GPU (unit-tested). Pages are
    /// the eviction unit (the stb packer can't free a rect). Above the budget (high water), the least recently used pages
    /// that are not the open page and were not used for <see cref="MinAgeMs"/> are evicted down to <see cref="LowWater"/>.
    /// The budget grows a page when a direction evicted less than <see cref="ThrashWindowMs"/> ago is loaded again (thrash)
    /// and memory allows it, up to the ceiling; it shrinks a page after <see cref="ShrinkQuietMs"/> without thrash or resize
    /// (sooner while the client is unfocused or minimized), down to the floor.
    /// </summary>
    public sealed class AnimAtlasLruPolicy
    {
        public int BudgetPages, FloorPages, CeilingPages;
        public long MinAgeMs = 45_000, ThrashWindowMs = 60_000, GrowCooldownMs = 5_000;
        public long ShrinkQuietMs = 180_000, ShrinkQuietUnfocusedMs = 45_000;

        /// <summary>Asked only when a thrash reload would grow the budget (it may allocate: GC.GetGCMemoryInfo).</summary>
        public Func<bool> MemoryAllowsGrowth;

        public long Reloads, ThrashReloads, Grows, Shrinks, EvictedPages, EvictedDirections;

        private long _lastThrashMs, _lastResizeMs;
        private bool _started;

        public AnimAtlasLruPolicy(int budgetPages, int floorPages, int ceilingPages)
        {
            FloorPages = Math.Max(1, Math.Min(floorPages, budgetPages));
            CeilingPages = Math.Max(budgetPages, ceilingPages);
            BudgetPages = Math.Max(1, budgetPages);
        }

        /// <summary>Evictions stop at this page count: a quarter of the budget (at least one page) below it.</summary>
        public int LowWater => Math.Max(1, BudgetPages - Math.Max(1, BudgetPages / 4));

        public bool Evictable(long lastUseMs, long nowMs) => nowMs - lastUseMs >= MinAgeMs;

        /// <summary>
        /// The pages to evict, oldest first, into <paramref name="victims"/>: none while <paramref name="lastUse"/>.Length
        /// (the resident pages) is within the budget, otherwise down to <see cref="LowWater"/> among the pages that may go
        /// (not <paramref name="openPage"/>, not used for <see cref="MinAgeMs"/>).
        /// </summary>
        public int SelectVictims(ReadOnlySpan<long> lastUse, int openPage, long nowMs, Span<int> victims)
        {
            int resident = lastUse.Length;

            if (resident <= BudgetPages)
            {
                return 0;
            }

            int want = Math.Min(resident - LowWater, victims.Length);
            int n = 0;

            while (n < want)
            {
                int best = -1;

                for (int i = 0; i < resident; i++)
                {
                    if (i == openPage || !Evictable(lastUse[i], nowMs) || (best >= 0 && lastUse[i] >= lastUse[best]) || Chosen(victims, n, i))
                    {
                        continue;
                    }

                    best = i;
                }

                if (best < 0)
                {
                    break;
                }

                victims[n++] = best;
            }

            return n;
        }

        private static bool Chosen(Span<int> victims, int n, int i)
        {
            for (int k = 0; k < n; k++)
            {
                if (victims[k] == i)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>An evicted direction was loaded again; true when the budget grew a page.</summary>
        public bool OnReload(long evictedAtMs, long nowMs)
        {
            Reloads++;

            if (nowMs - evictedAtMs >= ThrashWindowMs)
            {
                return false;
            }

            ThrashReloads++;
            _lastThrashMs = nowMs;

            if (BudgetPages >= CeilingPages || (_lastResizeMs != 0 && nowMs - _lastResizeMs < GrowCooldownMs) ||
                (MemoryAllowsGrowth != null && !MemoryAllowsGrowth()))
            {
                return false;
            }

            BudgetPages++;
            Grows++;
            _lastResizeMs = nowMs;

            return true;
        }

        /// <summary>Once per frame: shrinks the budget a page after a quiet period; true when it shrank.</summary>
        public bool Tick(long nowMs, bool unfocused)
        {
            if (!_started)
            {
                _started = true;
                _lastResizeMs = nowMs;
                _lastThrashMs = nowMs;

                return false;
            }

            long quiet = unfocused ? ShrinkQuietUnfocusedMs : ShrinkQuietMs;

            if (BudgetPages <= FloorPages || nowMs - Math.Max(_lastThrashMs, _lastResizeMs) < quiet)
            {
                return false;
            }

            BudgetPages--;
            Shrinks++;
            _lastResizeMs = nowMs;

            return true;
        }
    }
}
