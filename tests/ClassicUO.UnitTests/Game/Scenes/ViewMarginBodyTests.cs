using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ClassicUO.Assets;
using ClassicUO.Game.Scenes;
using ClassicUO.UnitTests.Assets;
using Uo.Pack;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// view_margin_body (vm-body): the switch in both states, a body's reach from the animation index (trim, centre, both
    /// facings, shadow, the stored directions only), the lazy per-body table (computed once, a frame budget, aliases, unknown
    /// bodies), a mobile's composed reach (mount, seat, walk offset, caps), the per-mobile view test with the top band, the
    /// walked tiles, and a cross-check against bodyreach.py's formulas on the CURRENT pack.
    /// </summary>
    public sealed class ViewMarginBodyTests
    {
        private const float L = 0, T = 0, R = 800, B = 600;
        private const ushort BIG = 10, SMALL = 11, ALIAS = 12, EMPTY = 13, BACK = 14;

        private readonly ITestOutputHelper _output;

        public ViewMarginBodyTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Switch_override_wins_in_both_states(bool on)
        {
            ViewMargin.BodyOverrideForTests = on;

            try
            {
                Assert.Equal(on, ViewMargin.BodyEnabled);
            }
            finally
            {
                ViewMargin.BodyOverrideForTests = null;
            }
        }

        private static AnimFrameEntry Frame(ushort ow, ushort oh, short cx, short cy, short tx, short ty, ushort w, ushort h) => new AnimFrameEntry
        {
            Sprite = new SpriteEntry { Page = 0, OrigW = ow, OrigH = oh, TrimX = tx, TrimY = ty, W = w, H = h },
            CenterX = cx,
            CenterY = cy
        };

        private static AnimationsLoader Tables()
        {
            var bodies = new AnimBodyEntry[64];
            var dirs = new List<AnimDirEntry>();
            var frames = new List<AnimFrameEntry>();

            for (int b = 0; b < bodies.Length; b++)
            {
                bodies[b].AliasBody = AnimConst.NoBody;
                bodies[b].CorpseBody = AnimConst.NoBody;
            }

            void Body(ushort body, int actions, Func<int, int, AnimFrameEntry[]> make)
            {
                bodies[body].Type = AnimType.Monster;
                bodies[body].FirstDir = (uint)dirs.Count;
                bodies[body].ActionCount = (ushort)actions;

                for (int a = 0; a < actions; a++)
                {
                    for (int d = 0; d < AnimConst.Directions; d++)
                    {
                        AnimFrameEntry[] f = make(a, d);
                        dirs.Add(new AnimDirEntry { FirstFrame = (uint)frames.Count, FrameCount = (ushort)f.Length });
                        frames.AddRange(f);
                    }
                }
            }

            // BIG: a 400 x 300 frame, centre (100, -50), trimmed to (20, 30) 300 x 250, action 1 direction 5 only
            Body(BIG, 2, (a, d) => a == 1 && d == 5 ? new[] { Frame(400, 300, 100, -50, 20, 30, 300, 250) } : Array.Empty<AnimFrameEntry>());
            // SMALL: a human-sized frame on every stored direction, plus a huge one on direction 0 the extract never reads
            Body(SMALL, 1, (a, d) => d == 0
                ? new[] { Frame(1000, 1000, 500, 0, 0, 0, 1000, 1000) }
                : new[] { Frame(60, 90, 30, -10, 5, 2, 50, 85), new AnimFrameEntry { Sprite = SpriteEntry.Absent } });
            Body(EMPTY, 0, (a, d) => Array.Empty<AnimFrameEntry>());
            // BACK: a frame whose visible pixels sit low (trimmed bottom rows), so it reaches below its anchor box
            Body(BACK, 1, (a, d) => d >= 3 ? new[] { Frame(100, 200, 50, -120, 0, 150, 100, 50) } : Array.Empty<AnimFrameEntry>());
            bodies[ALIAS].AliasBody = BIG;

            var anims = new AnimationsLoader(new UOFileManager(default, Path.GetTempPath(), null));
            anims.SetTablesForTests(bodies, dirs.ToArray(), frames.ToArray());

            return anims;
        }

        [Fact]
        public void Reach_comes_from_the_trim_rect_both_facings_and_the_shadow()
        {
            BodyReach.Reach r = BodyReach.Compute(Tables(), BIG);
            // visible columns: unmirrored centre - tx - 22 = 58 left, tx + w - centre - 22 = 198 right; mirrored swaps them
            Assert.Equal(198, r.Side);
            // the shadow: half the full frame's height from the top visible row, (300 - 30 + 1) / 2 = 135
            Assert.Equal(198 + 135, r.SideShadow);
            // top: oh + centreY - ty - 19 = 300 - 50 - 30 - 19
            Assert.Equal(201, r.Up);
            // bottom: the frame's ty + h - oh - centreY + 19 - 44 = 5; its shadow, squashed into the frame's lower half from
            // the top visible row, (oh + ty + h + 1) / 2 - oh - centreY + 19 - 44 = 15
            Assert.Equal(15, r.Down);

            BodyReach.Reach u = BodyReach.Compute(Tables(), BIG, trim: false);
            Assert.Equal(Math.Max(100 - 22, 400 - 100 - 22), u.Side);
            Assert.Equal(300 - 50 - 19, u.Up);
        }

        [Fact]
        public void Reach_reads_the_stored_directions_only_and_skips_absent_frames()
        {
            BodyReach.Reach r = BodyReach.Compute(Tables(), SMALL);
            Assert.Equal(Math.Max(30 - 5 - 22, 5 + 50 - 30 - 22), r.Side);
            Assert.Equal(90 - 10 - 2 - 19, r.Up);
            Assert.Equal(0, r.Down);
        }

        [Fact]
        public void Frames_below_the_anchor_box_reach_down()
        {
            BodyReach.Reach r = BodyReach.Compute(Tables(), BACK);
            // bottom row at anchor + 19 - (200 - 120) + 150 + 50 = anchor + 139: 95 below the box
            Assert.Equal(95, r.Down);
            Assert.True(r.Down <= BodyReach.CapTop);
        }

        [Fact]
        public void Table_computes_each_body_once_within_the_budget()
        {
            BodyReach.ResetForTests();

            try
            {
                AnimationsLoader anims = Tables();
                int budget = 1;
                Assert.True(BodyReach.TryGet(anims, BIG, ref budget, out BodyReach.Reach big));
                Assert.Equal(0, budget);
                Assert.Equal(1, BodyReach.Computed);
                Assert.Equal(BodyReach.Compute(anims, BIG).Up, big.Up);

                // cached: no budget needed
                Assert.True(BodyReach.TryGet(anims, BIG, ref budget, out BodyReach.Reach again));
                Assert.Equal(big.SideShadow, again.SideShadow);

                // budget spent: the default stands in, nothing is stored
                Assert.False(BodyReach.TryGet(anims, SMALL, ref budget, out BodyReach.Reach waiting));
                Assert.Equal(BodyReach.Default.Side, waiting.Side);
                Assert.Equal(BodyReach.Default.Down, waiting.Down);
                Assert.Equal(1, BodyReach.Computed);

                budget = BodyReach.BudgetPerFrame;
                // the alias draws BIG
                Assert.True(BodyReach.TryGet(anims, ALIAS, ref budget, out BodyReach.Reach alias));
                Assert.Equal(big.Side, alias.Side);
                // a body without frames, and one past the table: the fixed margin
                Assert.True(BodyReach.TryGet(anims, EMPTY, ref budget, out BodyReach.Reach empty));
                Assert.Equal(ViewMargin.MobileSide, empty.Side);
                Assert.Equal(ViewMargin.MobileUp, empty.Up);
                Assert.False(BodyReach.TryGet(anims, 5000, ref budget, out BodyReach.Reach unknown));
                Assert.Equal(BodyReach.CapTop, unknown.Down);

                // another loader (a new pack in a test): a fresh table
                AnimationsLoader other = Tables();
                Assert.True(BodyReach.TryGet(other, SMALL, ref budget, out _));
                Assert.Equal(1, BodyReach.Computed);
            }
            finally
            {
                BodyReach.ResetForTests();
            }
        }

        [Fact]
        public void Default_is_the_fixed_margin_with_a_top_band()
        {
            BodyReach.Compose(BodyReach.Default, true, false, default, 0, 0, 0, 0, out int left, out int right, out int up, out int down);
            Assert.Equal((160, 160, 260, 200), (left, right, up, down));
        }

        [Fact]
        public void Compose_moves_the_rider_by_its_seat_and_caps_the_reach()
        {
            var rider = new BodyReach.Reach(40, 90, 100, 10);
            var mount = new BodyReach.Reach(120, 180, 80, 30);

            // no shadow: the side for both
            BodyReach.Compose(rider, false, false, default, 0, 0, 0, 0, out int l, out int r, out int u, out int d);
            Assert.Equal((40, 40, 100, 10), (l, r, u, d));

            // mounted with a shadow: the rider 20 px up (offset -20), the mount below it
            BodyReach.Compose(rider, true, true, mount, 0, -20, 0, 0, out l, out r, out u, out d);
            Assert.Equal((120, 180, 120, 30), (l, r, u, d));

            // a walk step 44 px right and 22 px up: the reach moves with the frame
            BodyReach.Compose(rider, false, false, default, 0, 0, 44, -22, out l, out r, out u, out d);
            Assert.Equal((0, 84, 122, 0), (l, r, u, d));

            // caps
            var huge = new BodyReach.Reach(900, 900, 900, 900);
            BodyReach.Compose(huge, true, false, default, 0, 0, 0, 0, out l, out r, out u, out d);
            Assert.Equal((BodyReach.CapSide, BodyReach.CapSide, BodyReach.CapUp, BodyReach.CapTop), (l, r, u, d));
        }

        [Fact]
        public void Equipment_widens_the_rider_without_a_shadow()
        {
            var body = new BodyReach.Reach(40, 90, 100, 10);
            BodyReach.Reach merged = BodyReach.MergeEquipment(body, new BodyReach.Reach(120, 400, 50, 0));
            Assert.Equal(120, merged.Side);
            Assert.Equal(120, merged.SideShadow);
            Assert.Equal(100, merged.Up);
        }

        [Fact]
        public void A_human_beside_the_view_is_left_out_a_large_body_is_kept()
        {
            // anchor 150 px left of the view (box right edge at -106): inside the fixed 160 px margin
            const int ax = -150, ay = 300;
            Assert.False(BodyReach.Meets(ax, ay, 40, 60, 100, 10, L, T, R, B));
            Assert.True(BodyReach.Meets(ax, ay, 40, 299, 240, 190, L, T, R, B));
            // right of the view: the left reach matters
            Assert.False(BodyReach.Meets(900, ay, 60, 300, 100, 10, L, T, R, B));
            Assert.True(BodyReach.Meets(900, ay, 100, 0, 100, 10, L, T, R, B));
            // below the view: the up reach
            Assert.True(BodyReach.Meets(400, 850, 0, 0, 250, 0, L, T, R, B));
            Assert.False(BodyReach.Meets(400, 850, 0, 0, 249, 0, L, T, R, B));
        }

        [Fact]
        public void Top_band_keeps_a_frame_reaching_down_into_the_view()
        {
            // anchor 150 px above the view: its box ends at -106; a frame reaching 106 px below the box meets the top
            Assert.True(BodyReach.Meets(400, -150, 0, 0, 300, 106, L, T, R, B));
            Assert.False(BodyReach.Meets(400, -150, 0, 0, 300, 105, L, T, R, B));
            // the default stands in with a 200 px band
            Assert.True(BodyReach.Meets(400, -240, 0, 0, 0, BodyReach.Default.Down, L, T, R, B));
        }

        [Fact]
        public void Body_rect_and_tiles()
        {
            ViewMargin.BodyRect(L, T, R, B, out float minX, out float minY, out float maxX, out float maxY);
            Assert.Equal((-320f, -200f, 1120f, 900f), (minX, minY, maxX, maxY));

            const ulong inWalk = 0b1111_1111;
            // the static rectangle's tiles, and the caps' rectangle's tiles only where a dynamic is
            Assert.Equal(0b0101_0011UL, ViewMargin.BodyTiles(inWalk, 0b0000_0011, 0b1111_0000, 0b0101_0000));
            Assert.Equal(0b0000_0011UL, ViewMargin.BodyTiles(inWalk, 0b0000_0011, 0b1111_0000, 0));
            // never beyond the fill's own mask
            Assert.Equal(0UL, ViewMargin.BodyTiles(0, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue));
        }

        // bodyreach.py (cuo-poc-state/measure/vm-walk), the untrimmed reach over every direction record, reimplemented
        private static (int Side, int Up, int Down) BodyReachPy(AnimBodyEntry[] bodies, AnimDirEntry[] dirs, AnimFrameEntry[] frames, int b)
        {
            int side = 0, up = 0, down = 0;

            for (int a = 0; a < bodies[b].ActionCount; a++)
            {
                for (int d = 0; d < 8; d++)
                {
                    AnimDirEntry de = dirs[bodies[b].FirstDir + a * 8 + d];

                    for (int f = 0; f < de.FrameCount; f++)
                    {
                        AnimFrameEntry fe = frames[de.FirstFrame + f];

                        if (fe.Sprite.Page == 0xFFFF || fe.Sprite.OrigW == 0)
                        {
                            continue;
                        }

                        side = Math.Max(side, Math.Max(fe.CenterX - 22, fe.Sprite.OrigW - fe.CenterX - 22));
                        down = Math.Max(down, -fe.CenterY - 22);
                        up = Math.Max(up, fe.Sprite.OrigH + fe.CenterY - 22);
                    }
                }
            }

            return (side, up, down);
        }

        [AnimPackFact]
        public void Pack_reach_matches_bodyreach_py_and_trim_only_tightens_it()
        {
            string path = Path.Combine(TileDrawOracleTests.PackDir(), "anim", AnimTableFile.FileName);
            (AnimBodyEntry[] bodies, AnimDirEntry[] dirs, AnimFrameEntry[] frames) = AnimTableFile.Read(path);
            var anims = new AnimationsLoader(new UOFileManager(default, Path.GetTempPath(), null));
            anims.SetTablesForTests(bodies, dirs, frames);

            int n = 0, overSide = 0, overSideTrim = 0, overCapSide = 0, overCapUp = 0, overCapTop = 0, down = 0;
            int maxSide = 0, maxUp = 0, maxDown = 0, maxSideT = 0, maxShadowT = 0, maxUpT = 0, maxDownT = 0;
            long ticks = 0, worst = 0;

            for (int b = 0; b < bodies.Length; b++)
            {
                if (bodies[b].ActionCount == 0 || bodies[b].Source == AnimSource.None)
                {
                    continue;
                }

                n++;
                (int side, int up, int dn) = BodyReachPy(bodies, dirs, frames, b);
                BodyReach.Reach u = BodyReach.Compute(anims, (ushort)b, trim: false);
                BodyReach.Reach r = BodyReach.Compute(anims, (ushort)b);

                // the stored directions (3..7) hold every frame the others mirror; up is measured from the drawn top
                // (anchor + 19, bodyreach.py: + 22), down likewise
                Assert.True(side == u.Side, $"body {b}: side {u.Side}, bodyreach.py {side}");
                Assert.True(up > 0 ? u.Up == up + 3 : u.Up <= 3, $"body {b}: up {u.Up}, bodyreach.py {up}");
                Assert.True(u.Down == Math.Max(0, dn - 3), $"body {b}: down {u.Down}, bodyreach.py {dn}");

                Assert.True(r.Side <= u.Side && r.Up <= u.Up, $"body {b}: trimmed {r} beyond untrimmed {u}");
                Assert.True(r.SideShadow >= r.Side);

                maxSide = Math.Max(maxSide, side);
                maxUp = Math.Max(maxUp, up);
                maxDown = Math.Max(maxDown, dn);
                maxSideT = Math.Max(maxSideT, r.Side);
                maxShadowT = Math.Max(maxShadowT, r.SideShadow);
                maxUpT = Math.Max(maxUpT, r.Up);
                maxDownT = Math.Max(maxDownT, r.Down);
                overSide += side > 160 ? 1 : 0;
                overSideTrim += r.Side > 160 ? 1 : 0;
                overCapSide += r.SideShadow > BodyReach.CapSide ? 1 : 0;
                overCapUp += r.Up > BodyReach.CapUp ? 1 : 0;
                overCapTop += r.Down > BodyReach.CapTop ? 1 : 0;
                down += r.Down > 0 ? 1 : 0;
            }

            // the cost of a body's first sight (a second pass: the first one paid the JIT)
            for (int b = 0; b < bodies.Length; b++)
            {
                if (bodies[b].ActionCount != 0 && bodies[b].Source != AnimSource.None)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    BodyReach.Compute(anims, (ushort)b);
                    long dt = Stopwatch.GetTimestamp() - t0;
                    ticks += dt;
                    worst = Math.Max(worst, dt);
                }
            }

            double us = 1e6 / Stopwatch.Frequency;
            _output.WriteLine($"{n} bodies; bodyreach.py: max side {maxSide} up {maxUp} down {maxDown}, side > 160: {overSide}");
            _output.WriteLine($"trimmed: max side {maxSideT} with shadow {maxShadowT} up {maxUpT} down {maxDownT}; side > 160: {overSideTrim}; " +
                              $"beyond the caps: side+shadow {overCapSide}, up {overCapUp}, down {overCapTop}; reaching below the box {down}");
            _output.WriteLine($"compute: mean {ticks * us / Math.Max(1, n):F1} us/body, worst {worst * us:F1} us");
            Assert.True(n > 0);
        }
    }

    public sealed class AnimPackFactAttribute : FactAttribute
    {
        public AnimPackFactAttribute()
        {
            if (!File.Exists(Path.Combine(TileDrawOracleTests.PackDir(), "anim", AnimTableFile.FileName)))
            {
                Skip = "no pack anim/anim.bin (CUO_TEST_PACK or uo-packs CURRENT)";
            }
        }
    }
}
