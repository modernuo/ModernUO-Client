using System;
using System.Collections.Generic;
using System.IO;
using ClassicUO.Assets;
using ClassicUO.Game;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    [CollectionDefinition(nameof(AnimFrameCountCollection), DisableParallelization = true)]
    public class AnimFrameCountCollection
    {
    }

    // anim_frame_count_tables: the animation advance's frame count from the pack's tables (on, while the Gpu world draws) or the
    // Legacy frame cache (off, or the Legacy renderer); CUO_ANIM_FRAME_COUNT_CHECK computes both and reports differences.
    [Collection(nameof(AnimFrameCountCollection))]
    public class AnimFrameCountTests : IDisposable
    {
        private const ushort HUMAN = 400, ALIAS = 401, EMPTY = 402;

        private sealed class Calls
        {
            public int Count;
        }

        // the Legacy cache stand-in: counts its calls and answers the tables' count plus a skew
        private readonly struct FakeLegacy : ILegacyFrameCount
        {
            private readonly AnimationsLoader _tables;
            private readonly Calls _calls;
            private readonly int _skew;

            public FakeLegacy(AnimationsLoader tables, Calls calls, int skew = 0)
            {
                _tables = tables;
                _calls = calls;
                _skew = skew;
            }

            public int FrameCount(ushort body, byte action, byte dir)
            {
                _calls.Count++;

                return AnimFrameCount.FromTables(_tables, body, action, dir) + _skew;
            }
        }

        private readonly List<string> _mismatches = new List<string>();

        public AnimFrameCountTests()
        {
            AnimFrameCount.MismatchObserver = _mismatches.Add;
            AnimFrameCount.GpuWorld = true;
        }

        public void Dispose()
        {
            AnimFrameCount.OnOverride = null;
            AnimFrameCount.CheckOverride = null;
            AnimFrameCount.MismatchObserver = null;
            AnimFrameCount.GpuWorld = false;
        }

        private static AnimationsLoader Tables()
        {
            var bodies = new AnimBodyEntry[1024];
            var dirs = new List<AnimDirEntry>();
            var frames = new List<AnimFrameEntry>();

            for (int b = 0; b < bodies.Length; b++)
            {
                bodies[b].AliasBody = AnimConst.NoBody;
                bodies[b].CorpseBody = AnimConst.NoBody;
            }

            foreach (ushort body in new[] { HUMAN, EMPTY })
            {
                bodies[body].Type = AnimType.Human;
                bodies[body].FirstDir = (uint)dirs.Count;
                bodies[body].ActionCount = 35;

                for (int a = 0; a < 35; a++)
                {
                    for (int d = 0; d < AnimConst.Directions; d++)
                    {
                        // EMPTY has no frames anywhere; HUMAN a count per (action, dir), none for action 7
                        int count = body == EMPTY || a == 7 ? 0 : 1 + (a * 3 + d) % 9;
                        dirs.Add(new AnimDirEntry { FirstFrame = (uint)frames.Count, FrameCount = (ushort)count });

                        for (int f = 0; f < count; f++)
                        {
                            frames.Add(default);
                        }
                    }
                }
            }

            bodies[ALIAS].AliasBody = HUMAN;

            var anims = new AnimationsLoader(new UOFileManager(default, Path.GetTempPath(), null));
            anims.SetTablesForTests(bodies, dirs.ToArray(), frames.ToArray());

            return anims;
        }

        [Fact]
        public void FromTables_Is_The_Stored_Directions_Frame_Count()
        {
            AnimationsLoader t = Tables();

            for (byte action = 0; action < 35; action++)
            {
                for (byte dir = 0; dir < 5; dir++)
                {
                    int expected = t.TryGetDirection(HUMAN, action, dir + 3, out AnimDirEntry e) ? e.FrameCount : 0;
                    AnimFrameCount.FromTables(t, HUMAN, action, dir).Should().Be(expected);
                    // a Body.def alias follows to its target, as GetAnimationFrames resolves it
                    AnimFrameCount.FromTables(t, ALIAS, action, dir).Should().Be(expected);
                }
            }

            AnimFrameCount.FromTables(t, HUMAN, 0, 0).Should().BeGreaterThan(0);
            AnimFrameCount.FromTables(t, HUMAN, 7, 2).Should().Be(0);
            AnimFrameCount.FromTables(t, EMPTY, 0, 0).Should().Be(0);
            AnimFrameCount.FromTables(t, 5, 0, 0).Should().Be(0, "a body without actions");
            AnimFrameCount.FromTables(t, 60000, 0, 0).Should().Be(0, "a body past the table");
            AnimFrameCount.FromTables(t, HUMAN, 200, 0).Should().Be(0, "an action past MAX_ACTIONS");
            AnimFrameCount.FromTables(t, HUMAN, 0, 5).Should().Be(0, "a stored direction past 4");
        }

        [Fact]
        public void Off_Reads_The_Legacy_Cache()
        {
            AnimationsLoader t = Tables();
            var calls = new Calls();
            AnimFrameCount.OnOverride = false;
            AnimFrameCount.CheckOverride = false;

            AnimFrameCount.Get(t, new FakeLegacy(t, calls, skew: 2), HUMAN, 1, 3).Should().Be(AnimFrameCount.FromTables(t, HUMAN, 1, 3) + 2);
            calls.Count.Should().Be(1);
            _mismatches.Should().BeEmpty();
        }

        [Fact]
        public void On_Reads_The_Tables_And_Not_The_Legacy_Cache()
        {
            AnimationsLoader t = Tables();
            var calls = new Calls();
            AnimFrameCount.OnOverride = true;
            AnimFrameCount.CheckOverride = false;

            for (byte action = 0; action < 35; action++)
            {
                for (byte dir = 0; dir < 5; dir++)
                {
                    AnimFrameCount.Get(t, new FakeLegacy(t, calls, skew: 2), HUMAN, action, dir).Should().Be(AnimFrameCount.FromTables(t, HUMAN, action, dir));
                }
            }

            calls.Count.Should().Be(0, "the switch on loads nothing into the Legacy cache");
        }

        [Fact]
        public void On_With_The_Legacy_Renderer_Reads_The_Legacy_Cache()
        {
            // the Legacy renderer draws from the cache the advance's read fills ahead of the mobile walking into view
            AnimationsLoader t = Tables();
            var calls = new Calls();
            AnimFrameCount.OnOverride = true;
            AnimFrameCount.CheckOverride = false;
            AnimFrameCount.GpuWorld = false;

            AnimFrameCount.UseTables.Should().BeFalse();
            AnimFrameCount.Get(t, new FakeLegacy(t, calls, skew: 2), HUMAN, 1, 3).Should().Be(AnimFrameCount.FromTables(t, HUMAN, 1, 3) + 2);
            calls.Count.Should().Be(1);

            AnimFrameCount.GpuWorld = true;
            AnimFrameCount.UseTables.Should().BeTrue();
            AnimFrameCount.Get(t, new FakeLegacy(t, calls, skew: 2), HUMAN, 1, 3).Should().Be(AnimFrameCount.FromTables(t, HUMAN, 1, 3));
            calls.Count.Should().Be(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_Checker_Compares_Both_Counts_In_Both_Switch_States(bool on)
        {
            AnimationsLoader t = Tables();
            var calls = new Calls();
            AnimFrameCount.OnOverride = on;
            AnimFrameCount.CheckOverride = true;
            long checks = AnimFrameCount.Checks, mismatches = AnimFrameCount.Mismatches, tableReads = AnimFrameCount.TableReads;

            // agreeing sources: compared, no mismatch, the switch's source answers
            AnimFrameCount.Get(t, new FakeLegacy(t, calls), HUMAN, 2, 1).Should().Be(AnimFrameCount.FromTables(t, HUMAN, 2, 1));
            calls.Count.Should().Be(1);
            AnimFrameCount.Checks.Should().Be(checks + 1);
            AnimFrameCount.Mismatches.Should().Be(mismatches);
            _mismatches.Should().BeEmpty();

            // a disagreeing Legacy count: reported with body, action and direction; the switch's source still answers
            int tables = AnimFrameCount.FromTables(t, HUMAN, 4, 2);
            AnimFrameCount.Get(t, new FakeLegacy(t, calls, skew: 1), HUMAN, 4, 2).Should().Be(on ? tables : tables + 1);
            calls.Count.Should().Be(2);
            AnimFrameCount.Mismatches.Should().Be(mismatches + 1);
            AnimFrameCount.TableReads.Should().Be(tableReads + (on ? 2 : 0));
            _mismatches.Should().ContainSingle().Which.Should().Be($"[anim-frame-count-check] body 0x0190 action 4 dir 2: tables {tables}, legacy {tables + 1}");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Both_Switch_States_Give_The_Same_Counts_When_The_Sources_Agree(bool on)
        {
            // LiveAdvanceSource.FrameCount is AnimFrameCount.Get: with the Legacy cache answering the tables'
            // counts (as the checker verifies in the client) the advance sees the same counts in both states
            AnimationsLoader t = Tables();
            var calls = new Calls();
            AnimFrameCount.CheckOverride = false;

            AnimFrameCount.OnOverride = on;
            int count = 0;

            for (byte action = 0; action < 35; action++)
            {
                for (byte dir = 0; dir < 5; dir++)
                {
                    count += AnimFrameCount.Get(t, new FakeLegacy(t, calls), HUMAN, action, dir);
                }
            }

            AnimFrameCount.OnOverride = !on;
            int other = 0;

            for (byte action = 0; action < 35; action++)
            {
                for (byte dir = 0; dir < 5; dir++)
                {
                    other += AnimFrameCount.Get(t, new FakeLegacy(t, calls), HUMAN, action, dir);
                }
            }

            count.Should().Be(other).And.BeGreaterThan(0);
            calls.Count.Should().Be(35 * 5, "only the off state reads the Legacy cache");
        }
    }
}
