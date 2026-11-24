using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Game.Scenes;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// FrameLights past its bound (light_range's budget, view_margin's safety bound): Commit finds the kept entry of largest
    /// priority with a heap and FadeLowest picks its ranks in one pass. Both must give exactly what the scans they replaced
    /// gave: the same kept entries at the same indices, the same ties, the same Refused, MinRefusedPriority and shares.
    /// </summary>
    public sealed class FrameLightsRankingTests
    {
        /// <summary>The scans FrameLights used before the heap, kept as written: Commit scans every kept entry for the
        /// largest priority (first index on ties); FadeLowest picks by (priority, index), largest first, one scan per rank.</summary>
        private sealed class Reference
        {
            public readonly List<int> Ids = new List<int>();
            public readonly List<long> Priority = new List<long>();
            public readonly List<float> Share = new List<float>();
            public int Limit, Refused;
            public long MinRefusedPriority = long.MaxValue;

            public void BeginFrame(int limit)
            {
                Ids.Clear();
                Priority.Clear();
                Share.Clear();
                Limit = Math.Max(limit, 0);
                Refused = 0;
                MinRefusedPriority = long.MaxValue;
            }

            public void Commit(int id, float share, long priority)
            {
                if (Ids.Count < Limit)
                {
                    Ids.Add(id);
                    Share.Add(share);
                    Priority.Add(priority);

                    return;
                }

                Refused++;
                int worst = -1;
                long worstP = priority;

                for (int i = 0; i < Ids.Count; i++)
                {
                    if (Priority[i] > worstP)
                    {
                        worstP = Priority[i];
                        worst = i;
                    }
                }

                if (worst >= 0)
                {
                    Ids[worst] = id;
                    Share[worst] = share;
                    Priority[worst] = priority;
                }

                MinRefusedPriority = Math.Min(MinRefusedPriority, worstP);
            }

            public void Truncate(int count)
            {
                int n = Math.Min(Ids.Count, Math.Max(count, 0));
                Ids.RemoveRange(n, Ids.Count - n);
                Share.RemoveRange(n, Share.Count - n);
                Priority.RemoveRange(n, Priority.Count - n);
            }

            public void FadeLowest(int ranks)
            {
                int count = Ids.Count;
                ranks = Math.Min(ranks, count);
                long prevP = long.MaxValue;
                int prevI = int.MaxValue;

                for (int r = 0; r < ranks; r++)
                {
                    int best = -1;
                    long bestP = long.MinValue;

                    for (int i = 0; i < count; i++)
                    {
                        long p = Priority[i];

                        if ((p < prevP || p == prevP && i < prevI) && (p > bestP || p == bestP && i > best))
                        {
                            bestP = p;
                            best = i;
                        }
                    }

                    if (best < 0)
                    {
                        return;
                    }

                    Share[best] *= LightRange.BudgetShare(r, ranks);
                    prevP = bestP;
                    prevI = best;
                }
            }
        }

        private static long NextPriority(Random rng, int mode) =>
            mode switch
            {
                // many equal priorities
                0 => rng.Next(0, 4),
                1 => rng.Next(0, 40),
                // FrameLights.Priority's two bands: in view (squared distance) or past it (+2^40), with ties
                2 => rng.Next(0, 50) * 25L + (rng.Next(3) == 0 ? 1L << 40 : 0),
                _ => rng.NextInt64(0, long.MaxValue / 4)
            };

        private static void Offer(FrameLights lights, Reference reference, Random rng, int mode, ref int id)
        {
            float share = rng.Next(4) == 0 ? (float)rng.NextDouble() : 1f;
            long priority = NextPriority(rng, mode);
            ref FrameLights.Entry e = ref lights.Next();
            e.DrawX = id;
            e.Share = share;
            lights.Commit(priority);
            reference.Commit(id, share, priority);
            id++;
        }

        private static void AssertSame(FrameLights lights, Reference reference, uint frame, string because)
        {
            int n = lights.CountFor(frame);
            n.Should().Be(reference.Ids.Count, because);
            lights.Refused.Should().Be(reference.Refused, because);
            lights.MinRefusedPriority.Should().Be(reference.MinRefusedPriority, because);

            for (int i = 0; i < n; i++)
            {
                lights[i].DrawX.Should().Be(reference.Ids[i], "{0}, entry {1}", because, i);
                lights.PriorityAt(i).Should().Be(reference.Priority[i], "{0}, entry {1}", because, i);
                BitConverter.SingleToInt32Bits(lights[i].Share).Should().Be(BitConverter.SingleToInt32Bits(reference.Share[i]),
                    "{0}, entry {1} share {2} vs {3}", because, i, lights[i].Share, reference.Share[i]);
            }
        }

        public static IEnumerable<object[]> Limits() =>
            new[] { 0, 1, 2, 3, 5, 16, 17, 40, 64, 65, 256 }.Select(l => new object[] { l });

        [Theory]
        [MemberData(nameof(Limits))]
        public void Commit_and_FadeLowest_match_the_scans(int limit)
        {
            int[] ranksChoices = { LightRange.FadeRanks, 0, 1, 3, limit, limit + 1, 100 };

            for (int seed = 0; seed < 150; seed++)
            {
                var rng = new Random(seed * 7919 + limit);
                // one list reused over frames, as GameScene reuses it (grown arrays, the heap of the previous frame)
                var lights = new FrameLights(4);
                var reference = new Reference();
                int id = 0;

                for (uint frame = 1; frame <= 6; frame++)
                {
                    int mode = rng.Next(4);
                    int[] offeredChoices = { 0, Math.Max(limit - 1, 0), limit, limit + 1, limit * 3 + 7, rng.Next(0, limit * 2 + 2) };
                    int offered = offeredChoices[rng.Next(offeredChoices.Length)];
                    lights.BeginFrame(frame, limit, true);
                    reference.BeginFrame(limit);

                    for (int k = 0; k < offered; k++)
                    {
                        Offer(lights, reference, rng, mode, ref id);
                    }

                    // the Gpu probe's restore truncates the list back to where it began, and the walk goes on
                    if (rng.Next(4) == 0)
                    {
                        int keep = rng.Next(0, lights.CountFor(frame) + 1);
                        lights.Truncate(keep);
                        reference.Truncate(keep);
                        int more = rng.Next(0, limit * 2 + 2);

                        for (int k = 0; k < more; k++)
                        {
                            Offer(lights, reference, rng, mode, ref id);
                        }
                    }

                    string because = $"limit {limit}, seed {seed}, frame {frame}, mode {mode}";
                    AssertSame(lights, reference, frame, because + " after commits");

                    int ranks = ranksChoices[rng.Next(ranksChoices.Length)];
                    lights.FadeLowest(ranks);
                    reference.FadeLowest(ranks);
                    AssertSame(lights, reference, frame, because + $" after FadeLowest({ranks})");
                }
            }
        }

        [Fact]
        public void Equal_priorities_keep_the_first_index_and_fade_the_last()
        {
            var lights = new FrameLights(4);
            var reference = new Reference();
            lights.BeginFrame(1, 8, true);
            reference.BeginFrame(8);
            var rng = new Random(1);
            int id = 0;

            for (int k = 0; k < 40; k++)
            {
                float share = 1f;
                long priority = k < 8 ? 5 : 5 - (k & 1);
                ref FrameLights.Entry e = ref lights.Next();
                e.DrawX = id;
                e.Share = share;
                lights.Commit(priority);
                reference.Commit(id++, share, priority);
            }

            lights.FadeLowest(LightRange.FadeRanks);
            reference.FadeLowest(LightRange.FadeRanks);
            AssertSame(lights, reference, 1, "all ties");
        }

        [Fact]
        public void Commit_past_the_budget_and_FadeLowest_do_not_allocate()
        {
            var lights = new FrameLights(4);
            var rng = new Random(3);
            long[] priorities = new long[2048];

            for (int i = 0; i < priorities.Length; i++)
            {
                priorities[i] = rng.Next(0, 5000);
            }

            uint frame = 0;

            void Run()
            {
                lights.BeginFrame(++frame, LightRange.DefaultBudget, true);

                for (int i = 0; i < priorities.Length; i++)
                {
                    ref FrameLights.Entry e = ref lights.Next();
                    e.DrawX = i;
                    e.Share = 1f;
                    lights.Commit(priorities[i]);
                }

                lights.FadeLowest(LightRange.FadeRanks);
            }

            // the same calls once first (grown arrays, type initialisers, first-call stubs)
            for (int i = 0; i < 20; i++)
            {
                Run();
            }

            long best = long.MaxValue;

            // Tier-up during the loop can show as a few KB on this thread (0 with DOTNET_TieredCompilation=0 and in
            // NativeAOT), so the loop is measured again until it settles; a real per-call allocation never reaches 0.
            for (int attempt = 0; attempt < 40 && best > 0; attempt++)
            {
                if (attempt > 0)
                {
                    System.Threading.Thread.Sleep(50);
                }

                long before = GC.GetAllocatedBytesForCurrentThread();

                for (int i = 0; i < 200; i++)
                {
                    Run();
                }

                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            lights.Refused.Should().Be(priorities.Length - LightRange.DefaultBudget);
            best.Should().Be(0);
        }
    }
}
