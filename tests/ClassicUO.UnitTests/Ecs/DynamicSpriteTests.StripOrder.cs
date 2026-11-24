// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using Uo.Render;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // mobile_strip_order (MobileStripOrder): the rows of a strip below the anchor on the standing tile's diamond take + 0.75
    public partial class DynamicSpriteTests
    {
        private const float StripBase = 812.25f;

        // Select at (600, 400): the composite anchor row is 400 - 3 + 22 = 419, the band ends at 419 + 25
        private const int StripBandEnd = 419 + MobileStripOrder.Band;

        private static DynamicSprite[] StripRecords(MobileSpriteExtract extract, in GpuSpriteTables tables, string name, bool on)
        {
            var mirror = new WorldMirror();
            EcsEntity e = MobileCase(mirror, name);
            extract.Begin(in tables, 0, 0);
            extract.StripOrderForTests = on;
            extract.Select(e, 600, 400, StripBase);

            return extract.Selected.ToArray();
        }

        private static bool IsShadow(in DynamicSprite s) => (int)s.Hue.Y == ShaderHueTranslator.SHADER_SHADOW;

        // the source rows each layer draws (body, group, dir, frame, equipment) -> sorted row list
        private static Dictionary<(ushort, byte, byte, byte, bool), List<int>> Rows(DynamicSprite[] records)
        {
            var rows = new Dictionary<(ushort, byte, byte, byte, bool), List<int>>();

            foreach (DynamicSprite s in records.Where(r => !IsShadow(r)))
            {
                var key = (s.Body, s.Group, s.Dir, s.Frame, (s.Flags & DynamicSpriteFlags.Equip) != 0);

                if (!rows.TryGetValue(key, out List<int> list))
                {
                    rows[key] = list = new List<int>();
                }

                for (int r = s.SrcY; r < s.SrcY + s.SrcH; r++)
                {
                    list.Add(r);
                }
            }

            foreach (List<int> list in rows.Values)
            {
                list.Sort();
            }

            return rows;
        }

        [Theory]
        [InlineData("plain")]
        [InlineData("mounted")]
        [InlineData("equipped")]
        [InlineData("flipped")]
        public void StripOrder_Off_Keeps_The_Old_Adds(string name)
        {
            for (int seed = 1; seed <= 8; seed++)
            {
                (AnimationsLoader anims, GpuSpriteTables tables) = AnimFixture(seed);
                DynamicSprite[] off = StripRecords(new MobileSpriteExtract(anims), in tables, name, false);

                Assert.All(off.Where(s => !IsShadow(s)), s => Assert.True(s.Depth == StripBase + 1f || s.Depth == StripBase + 3f, $"depth {s.Depth}"));
            }
        }

        [Theory]
        [InlineData("plain")]
        [InlineData("mounted")]
        [InlineData("equipped")]
        [InlineData("flipped")]
        public void StripOrder_On_Splits_The_Lower_Strip_At_The_Band(string name)
        {
            int nearRecords = 0, farRecords = 0;

            for (int seed = 1; seed <= 8; seed++)
            {
                (AnimationsLoader anims, GpuSpriteTables tables) = AnimFixture(seed);
                var extract = new MobileSpriteExtract(anims);
                DynamicSprite[] off = StripRecords(extract, in tables, name, false);
                DynamicSprite[] on = StripRecords(extract, in tables, name, true);

                // the same rows of every layer, each once; the upper strips and the shadows unchanged
                Dictionary<(ushort, byte, byte, byte, bool), List<int>> rowsOff = Rows(off), rowsOn = Rows(on);
                Assert.Equal(rowsOff.Keys.OrderBy(k => k), rowsOn.Keys.OrderBy(k => k));

                foreach (var key in rowsOff.Keys)
                {
                    Assert.Equal(rowsOff[key], rowsOn[key]);
                }

                Assert.Equal(off.Where(s => IsShadow(s) || s.Depth == StripBase + 1f).Select(s => (s.Y, s.SrcY, s.SrcH, s.Depth)),
                    on.Where(s => IsShadow(s) || s.Depth == StripBase + 1f).Select(s => (s.Y, s.SrcY, s.SrcH, s.Depth)));

                foreach (DynamicSprite s in on.Where(r => !IsShadow(r) && r.Depth != StripBase + 1f))
                {
                    if (s.Depth == StripBase + 1f + MobileStripOrder.NearAdd)
                    {
                        nearRecords++;
                        Assert.True(s.Y + s.SrcH <= StripBandEnd, $"near rows {s.Y}..{s.Y + s.SrcH} past the band end {StripBandEnd}");
                    }
                    else
                    {
                        farRecords++;
                        Assert.Equal(StripBase + 3f, s.Depth);
                        Assert.True(s.Y >= StripBandEnd, $"far rows from {s.Y} above the band end {StripBandEnd}");
                    }
                }
            }

            Assert.True(nearRecords > 0, "no lower strip reached the band: the fixture does not exercise the split");
            Assert.True(farRecords > 0, "no lower strip passed the band: the fixture does not exercise the far rows");
        }

        // the key order the near add buys, in both renderers' units: Legacy floats (statics and land + 0.5) and the Gpu's
        // integer D (mobile round(depth * 100) - 50, statics 100 (x + y) + 127 + PriorityZ)
        [Theory]
        [InlineData(0, 0)]
        [InlineData(0, 20)]
        [InlineData(20, 0)]
        [InlineData(-5, 15)]
        public void NearAdd_Lies_Between_The_Ground_Ahead_And_A_Mobile_Ahead(int behindZ, int frontZ)
        {
            const int S = 3000;

            // a mobile's depth: View.CalculateDepthZ with PriorityZ = z + 1 (Chunk.AddGameObject)
            float Mobile(int sum, int z) => sum + (127 + z + 1) * 0.01f;
            float Land(int sum, int z) => sum + (127 + z - 2) * 0.01f + 0.5f;
            float StaticWithHeight(int sum, int z) => sum + (127 + z + 1) * 0.01f + 0.5f;
            int GpuMobile(float depth, float add) => (int)MathF.Round(MobileSpriteExtract.DepthOf(depth, 1, add) * 100f) - 50;
            int GpuStatic(int sum, int pz) => sum * 100 + 127 + pz;

            float behind = Mobile(S, behindZ);
            float near = MobileSpriteExtract.DepthOf(behind, 1, MobileStripOrder.NearAdd);
            float old = MobileSpriteExtract.DepthOf(behind, 1, 2f);
            float frontUpper = MobileSpriteExtract.DepthOf(Mobile(S + 1, frontZ), 1, 0f);

            // the bug: the old lower strip draws over the upper strip of a mobile one diagonal ahead at the same height
            if (behindZ == frontZ)
            {
                Assert.True(old > frontUpper);
            }

            Assert.True(near < frontUpper, $"near {near} >= front upper {frontUpper}");
            Assert.True(near > Land(S + 1, behindZ), "near rows under the land one diagonal ahead");
            Assert.True(near > StaticWithHeight(S + 1, behindZ), "near rows under a static one diagonal ahead at the same z");
            Assert.True(GpuMobile(behind, MobileStripOrder.NearAdd) < GpuMobile(Mobile(S + 1, frontZ), 0f));
            Assert.True(GpuMobile(behind, MobileStripOrder.NearAdd) > GpuStatic(S + 1, behindZ - 2));
            Assert.True(GpuMobile(behind, MobileStripOrder.NearAdd) > GpuStatic(S + 1, behindZ + 1));

            // the far rows keep + 2: over the land two diagonals ahead
            Assert.True(old > Land(S + 2, behindZ));
        }

        [Theory]
        [InlineData(400, 10, 419, 10)]
        [InlineData(440, 10, 419, 4)]
        [InlineData(444, 10, 419, 0)]
        [InlineData(460, 10, 419, 0)]
        [InlineData(380, 30, 419, 30)]
        public void NearRows_Clamps_To_The_Strip(int top, int h, int anchorY, int expected)
        {
            Assert.Equal(expected, MobileStripOrder.NearRows(top, h, anchorY));
        }
    }
}
