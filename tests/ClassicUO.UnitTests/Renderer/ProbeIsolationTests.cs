using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    /// <summary>The tests that use AnimationGroupRules' static roll tape (RollMode, RollTape) or the global random state:
    /// serialized, and not run alongside any other collection.</summary>
    [CollectionDefinition("AnimationRolls", DisableParallelization = true)]
    public class AnimationRollsCollection
    {
    }

    // E12 (CUO_GPU_ITEM_CHECK): what the probe pass relies on to leave no trace in the drawn frame.
    [Collection("AnimationRolls")]
    public class ProbeIsolationTests
    {
        private static PendingOverrideQueue.Entry E(int x, uint value = 128) => new PendingOverrideQueue.Entry { X = x, Y = 1, Z = 2, Graphic = 3, IsStatic = true, Value = value };

        [Fact]
        public void Restoring_a_mark_forgets_the_probes_overrides_so_none_is_queued_twice()
        {
            var q = new PendingOverrideQueue(16);
            q.Add(E(1));
            PendingOverrideQueue.Mark mark = q.GetMark();

            // probe pass
            q.Add(E(10));
            q.Add(E(11));
            q.Restore(mark);

            // reference pass queues the same overrides
            q.Add(E(10));
            q.Add(E(11));

            q.Count.Should().Be(3);
            q[0].X.Should().Be(1);
            q[1].X.Should().Be(10);
            q[2].X.Should().Be(11);
            q.Dropped.Should().Be(0);
        }

        [Fact]
        public void The_probe_filling_the_queue_does_not_make_the_reference_drop_overrides()
        {
            var q = new PendingOverrideQueue(4);
            PendingOverrideQueue.Mark mark = q.GetMark();

            for (int i = 0; i < 6; i++)
            {
                q.Add(E(i));
            }

            q.Dropped.Should().Be(2);
            q.Restore(mark);

            for (int i = 0; i < 4; i++)
            {
                q.Add(E(100 + i));
            }

            q.Count.Should().Be(4);
            q.Dropped.Should().Be(0);
            q[3].X.Should().Be(103);
        }

        [Fact]
        public void Restoring_after_a_clear_throws()
        {
            var q = new PendingOverrideQueue(4);
            q.Add(E(1));
            PendingOverrideQueue.Mark mark = q.GetMark();
            q.Clear();

            q.Invoking(x => x.Restore(mark)).Should().Throw<System.InvalidOperationException>();
        }

        // ---- the probe's end (TakeSince: what GameScene.GpuItemProbeRun calls through WorldRenderer.TakePendingOverrides)

        [Fact]
        public void Taking_the_probes_overrides_keeps_them_and_their_drops_and_empties_the_queue_back_to_the_mark()
        {
            var q = new PendingOverrideQueue(4);
            q.Add(E(1));
            PendingOverrideQueue.Mark mark = q.GetMark();

            for (int i = 0; i < 5; i++)
            {
                q.Add(E(10 + i));
            }

            var taken = new List<PendingOverrideQueue.Entry> { E(99) };
            int dropped = q.TakeSince(mark, taken);

            dropped.Should().Be(2);
            taken.ConvertAll(e => e.X).Should().Equal(10, 11, 12);
            // the restoration: without it the reference pass's overrides would be queued after the probe's (or dropped)
            q.Count.Should().Be(1);
            q.Dropped.Should().Be(0);
            q[0].X.Should().Be(1);
        }

        // ---- the comparator (PendingOverrideQueue.Differ, as GpuItemProbeCompare calls it)

        private static (bool Differ, int RefCount, int RefDropped) Compare(List<PendingOverrideQueue.Entry> probe, int probeDropped, PendingOverrideQueue.Entry[] before,
                                                                           PendingOverrideQueue.Entry[] reference, int capacity = 16)
        {
            var q = new PendingOverrideQueue(capacity);

            foreach (var e in before)
            {
                q.Add(e);
            }

            PendingOverrideQueue.Mark mark = q.GetMark();

            foreach (var e in reference)
            {
                q.Add(e);
            }

            bool differ = PendingOverrideQueue.Differ(probe, probeDropped, in q, mark, out int refCount, out int refDropped);

            return (differ, refCount, refDropped);
        }

        [Fact]
        public void Identical_overrides_after_existing_entries_compare_equal_and_the_entries_before_the_mark_are_not_compared()
        {
            var probe = new List<PendingOverrideQueue.Entry> { E(10), E(11) };

            (bool differ, int refCount, int refDropped) = Compare(probe, 0, new[] { E(1), E(2), E(3) }, new[] { E(10), E(11) });

            differ.Should().BeFalse();
            refCount.Should().Be(2);
            refDropped.Should().Be(0);
        }

        [Fact]
        public void Every_entry_field_is_compared()
        {
            PendingOverrideQueue.Entry b = E(10);
            var changed = new List<PendingOverrideQueue.Entry>();
            PendingOverrideQueue.Entry c;
            c = b; c.X++; changed.Add(c);
            c = b; c.Y++; changed.Add(c);
            c = b; c.Z++; changed.Add(c);
            c = b; c.Graphic++; changed.Add(c);
            c = b; c.IsStatic = !c.IsStatic; changed.Add(c);
            c = b; c.Value++; changed.Add(c);

            foreach (var e in changed)
            {
                Compare(new List<PendingOverrideQueue.Entry> { E(5), b }, 0, new[] { E(1) }, new[] { E(5), e }).Differ.Should().BeTrue();
            }
        }

        [Fact]
        public void Order_and_count_are_compared()
        {
            Compare(new List<PendingOverrideQueue.Entry> { E(10), E(11) }, 0, new[] { E(1) }, new[] { E(11), E(10) }).Differ.Should().BeTrue();
            Compare(new List<PendingOverrideQueue.Entry> { E(10), E(11) }, 0, new[] { E(1) }, new[] { E(10) }).Differ.Should().BeTrue();
            Compare(new List<PendingOverrideQueue.Entry> { E(10) }, 0, new[] { E(1) }, new[] { E(10), E(11) }).Differ.Should().BeTrue();
            Compare(new List<PendingOverrideQueue.Entry>(), 0, new[] { E(1) }, new PendingOverrideQueue.Entry[0]).Differ.Should().BeFalse();
        }

        [Fact]
        public void Overflow_compares_both_passes_drops()
        {
            // capacity 3, one entry before the mark: two fit
            var fitted = new List<PendingOverrideQueue.Entry> { E(10), E(11) };

            // the probe overflowed by one, the reference fitted: different (round 2 reported these as equal)
            (bool differ, int refCount, int refDropped) = Compare(fitted, 1, new[] { E(1) }, new[] { E(10), E(11) }, 3);
            differ.Should().BeTrue();
            refDropped.Should().Be(0);

            // the same overflow in both passes: equal (round 2 reported a difference), and reported as an overflow
            (differ, refCount, refDropped) = Compare(fitted, 1, new[] { E(1) }, new[] { E(10), E(11), E(12) }, 3);
            differ.Should().BeFalse();
            refCount.Should().Be(2);
            refDropped.Should().Be(1);

            // the reference overflowed more
            Compare(fitted, 1, new[] { E(1) }, new[] { E(10), E(11), E(12), E(13) }, 3).Differ.Should().BeTrue();
        }

        // ---- the random rolls

        private const AnimationFlags F = AnimationFlags.CalculateOffsetLowGroupExtended;

        // a pass: rolls for each group in turn, recorded on the tape
        private static List<AnimationGroupRules.RollRecord> Pass(params ushort[] groups)
        {
            var tape = new List<AnimationGroupRules.RollRecord>();
            AnimationGroupRules.RollTape = tape;
            AnimationGroupRules.RollMode = 1;

            try
            {
                foreach (ushort g in groups)
                {
                    AnimationGroupRules.Roll(F, g);
                }
            }
            finally
            {
                AnimationGroupRules.RollMode = 0;
                AnimationGroupRules.RollTape = null;
            }

            return tape;
        }

        // a frame: the reference pass's rolls, then the frame's other random consumers (Weather.Draw: wind, particles)
        private static (List<AnimationGroupRules.RollRecord> Rolls, int[] Weather, RandomHelper.State After) Frame(ushort[] reference)
        {
            var rolls = Pass(reference);
            int[] weather = { RandomHelper.GetValue(0, 100), RandomHelper.GetValue(-5, 5), RandomHelper.GetValue() };

            return (rolls, weather, RandomHelper.Save());
        }

        public static IEnumerable<object[]> ProbeSequences => new[]
        {
            new object[] { new ushort[] { 5, 6 }, new ushort[] { 5, 6 }, false },          // same
            new object[] { new ushort[] { 5 }, new ushort[] { 5, 6 }, true },              // the probe rolled fewer
            new object[] { new ushort[] { 5, 6, 5 }, new ushort[] { 5, 6 }, true },        // the probe rolled more (round 2: the extra roll reached Weather)
            new object[] { new ushort[] { 6, 5 }, new ushort[] { 5, 6 }, true },           // reordered (round 2: replayed onto unrelated requests)
            new object[] { new ushort[0], new ushort[0], false }
        };

        [Theory]
        [MemberData(nameof(ProbeSequences))]
        public void The_probes_rolls_never_reach_the_drawn_frame_and_a_different_sequence_is_a_difference(ushort[] probe, ushort[] reference, bool differ)
        {
            RandomHelper.State start = RandomHelper.Save();

            // the frame without a probe
            var plain = Frame(reference);

            // the check frame: the probe between Save and Restore (as GpuItemProbeRun), then the drawn reference pass
            RandomHelper.Restore(in start);
            RandomHelper.State rng = RandomHelper.Save();
            var probeTape = Pass(probe);
            RandomHelper.Restore(in rng);
            var check = Frame(reference);

            check.Rolls.Should().Equal(plain.Rolls, "the reference pass rolls what a frame without the probe rolls");
            check.Weather.Should().Equal(plain.Weather, "the rest of the frame sees the same random numbers");
            check.After.Should().Be(plain.After, "the global random state after the frame equals the no-probe state");
            AnimationGroupRules.RollsDiffer(probeTape, check.Rolls, out _).Should().Be(differ);
        }

        [Fact]
        public void The_same_request_from_the_same_state_gets_the_same_value_and_a_changed_value_is_a_difference()
        {
            RandomHelper.State rng = RandomHelper.Save();
            var a = Pass(5, 6, 5);
            RandomHelper.Restore(in rng);
            var b = Pass(5, 6, 5);

            AnimationGroupRules.RollsDiffer(a, b, out _).Should().BeFalse();
            b[1] = b[1] with { Value = b[1].Value + 1 };
            AnimationGroupRules.RollsDiffer(a, b, out int first).Should().BeTrue();
            first.Should().Be(1);
        }

        [Fact]
        public void Rolling_without_a_tape_records_nothing()
        {
            AnimationGroupRules.RollMode.Should().Be(0);
            AnimationGroupRules.Roll(F, 5).Should().BeGreaterOrEqualTo(0);
        }

        [Fact]
        public void The_random_helper_keeps_its_ranges()
        {
            for (int i = 0; i < 2000; i++)
            {
                RandomHelper.GetValue(3, 7).Should().BeInRange(3, 7);
                RandomHelper.GetValue(-2, -2).Should().Be(-2);
                RandomHelper.GetValue().Should().BeInRange(0, int.MaxValue - 1);
                RandomHelper.RandomList(4, 9).Should().BeOneOf(4, 9);
            }

            int trues = 0;

            for (int i = 0; i < 2000; i++)
            {
                trues += RandomHelper.RandomBool() ? 1 : 0;
            }

            trues.Should().BeInRange(800, 1200);
            FluentActions.Invoking(() => RandomHelper.GetValue(5, 3)).Should().Throw<System.ArgumentOutOfRangeException>();
        }

        // runs body on a new thread (its own random state) and waits for it
        private static void OnThread(System.Action body)
        {
            System.Exception error = null;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    body();
                }
                catch (System.Exception e)
                {
                    error = e;
                }
            });
            t.Start();
            t.Join();

            if (error != null)
            {
                throw new System.Exception("on the other thread", error);
            }
        }

        [Fact]
        public void A_roll_on_another_thread_during_the_probes_save_and_restore_survives()
        {
            // Codex round 3: SDL's raw-input thread (or a plugin) rolls while the render thread's probe runs between Save and
            // Restore; the restore must not undo that roll
            RandomHelper.State before = default, afterRoll = default, afterRestore = default;
            int rolled = 0, next = 0, expectedNext = 0;
            using var rolledEvt = new System.Threading.ManualResetEventSlim();
            using var restoredEvt = new System.Threading.ManualResetEventSlim();

            var other = new System.Threading.Thread(() =>
            {
                before = RandomHelper.Save();
                rolled = RandomHelper.GetValue(); // the war-music choice, say
                afterRoll = RandomHelper.Save();
                rolledEvt.Set();
                restoredEvt.Wait();
                afterRestore = RandomHelper.Save();
                next = RandomHelper.GetValue();
                // the same thread's stream continued from afterRoll, as if no probe had run
                RandomHelper.Restore(in afterRoll);
                expectedNext = RandomHelper.GetValue();
            });

            RandomHelper.State rng = RandomHelper.Save();
            int probeRoll = RandomHelper.GetValue();
            other.Start();
            rolledEvt.Wait();
            RandomHelper.GetValue();
            RandomHelper.Restore(in rng);
            restoredEvt.Set();
            other.Join();

            afterRoll.Should().NotBe(before, "the other thread's roll advanced its state");
            afterRestore.Should().Be(afterRoll, "the render thread's restore leaves the other thread's state alone");
            next.Should().Be(expectedNext);
            RandomHelper.Save().Should().Be(rng, "the render thread is back where its probe started");
            RandomHelper.GetValue().Should().Be(probeRoll, "and rolls what the probe rolled first");
        }

        [Fact]
        public void Two_threads_get_independent_streams()
        {
            RandomHelper.State mine = RandomHelper.Save();
            RandomHelper.State a = default, b = default, bAfterA = default;
            int[] fromA = new int[8], fromB = new int[8];

            OnThread(() => a = RandomHelper.Save());

            OnThread(() =>
            {
                b = RandomHelper.Save();

                // another thread rolling meanwhile doesn't move this thread's state
                OnThread(() =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        RandomHelper.GetValue();
                    }
                });

                bAfterA = RandomHelper.Save();
            });

            a.Should().NotBe(b, "each thread is seeded on its own");
            bAfterA.Should().Be(b);
            RandomHelper.Save().Should().Be(mine, "rolls on other threads never touch this thread's state");

            // each thread's stream depends only on its own state: two threads put in the same state roll the same values
            OnThread(() =>
            {
                RandomHelper.Restore(in a);

                for (int i = 0; i < fromA.Length; i++)
                {
                    fromA[i] = RandomHelper.GetValue();
                }
            });

            OnThread(() =>
            {
                RandomHelper.Restore(in a);
                OnThread(() => RandomHelper.GetValue());

                for (int i = 0; i < fromB.Length; i++)
                {
                    fromB[i] = RandomHelper.GetValue();
                }
            });

            fromB.Should().Equal(fromA);
        }

        // ---- unmapped sprites: the full request, through the capture (GpuWorldRenderer.Draw/DrawSitted with an unmapped texture)

        private static GpuWorldRenderer Capture(out List<GpuWorldRenderer.FallbackDraw> fallbacks)
        {
            var r = (GpuWorldRenderer)RuntimeHelpers.GetUninitializedObject(typeof(GpuWorldRenderer));
            var obj = (Static)RuntimeHelpers.GetUninitializedObject(typeof(Static));
            typeof(GpuWorldRenderer).GetField("_object", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(r, obj);
            typeof(GpuWorldRenderer).GetField("_wobble", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(r, true);
            fallbacks = new List<GpuWorldRenderer.FallbackDraw>();
            r.Fallbacks = fallbacks;
            r.RecordFallbacks = true;
            r.Probe = true;

            return r;
        }

        [Fact]
        public void An_unmapped_seated_sprite_records_its_deformation_and_differs_when_it_changes()
        {
            GpuWorldRenderer r = Capture(out var fallbacks);
            var pos = new Vector2(10, 20);
            var src = new Rectangle(0, 0, 44, 60);

            r.DrawSitted(null, pos, src, new Vector3(0.3f, 0.5f, 0.2f), Vector3.Zero, false, 0.5f).Should().BeTrue();
            r.DrawSitted(null, pos, src, new Vector3(0.3f, 0.6f, 0.1f), Vector3.Zero, false, 0.5f).Should().BeTrue();
            r.Draw(null, pos, src, Vector3.Zero, false, false, 0.5f, SpriteCaptureMode.Normal).Should().BeTrue();
            r.Draw(null, pos, src, Vector3.Zero, false, true, 0.5f, SpriteCaptureMode.Normal).Should().BeTrue();

            fallbacks.Should().HaveCount(4);
            fallbacks[0].Kind.Should().Be(GpuWorldRenderer.FallbackKind.Seated);
            fallbacks[0].Mod.Should().Be(new Vector3(0.3f, 0.5f, 0.2f));
            fallbacks[0].Should().NotBe(fallbacks[1], "the same arguments with other section fractions draw other geometry");
            fallbacks[2].Kind.Should().Be(GpuWorldRenderer.FallbackKind.Normal);
            fallbacks[2].Mod.Should().Be(Vector3.Zero, "the seated draw's deformation doesn't leak into the next draw");
            fallbacks[3].Kind.Should().Be(GpuWorldRenderer.FallbackKind.Scaled);
            fallbacks[2].Should().NotBe(fallbacks[3], "a scaled draw is not the unscaled one");
            fallbacks[2].Should().NotBe(fallbacks[0]);
        }
    }
}
