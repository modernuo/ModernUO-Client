// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using ClassicUO.Game.Scenes;
using ClassicUO.UnitTests.Ecs;
using FluentAssertions;
using Uo.Render;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// clip-2: the mobile composite anchor (SpriteInstance.GeoMobile, DynamicSprite.Geo*) is written only with depth_model on,
    /// so the default path's records and instances are those of the build before the switch (golden bench6-000020 caught
    /// the anchor in the off path). The frame cache keys its recording on the switch.
    /// </summary>
    [Collection(nameof(DepthModelSwitchCollection))]
    public class DepthModelExtractTests
    {
        private static int Stamped(ReadOnlySpan<SpriteInstance> instances)
        {
            int n = 0;

            foreach (SpriteInstance i in instances)
            {
                if (i.TryGeoMobile(out _, out _, out _))
                {
                    n++;
                }
            }

            return n;
        }

        private static (int Stamped, int Count, DynamicSprite[] Selected) Run(bool on, string name)
        {
            (AnimationsLoader anims, GpuSpriteTables tables) = DynamicSpriteTests.AnimFixture(5);
            var mirror = new WorldMirror();
            EcsEntity e = DynamicSpriteTests.MobileCase(mirror, name);
            var extract = new MobileSpriteExtract(anims);

            try
            {
                DepthModel.OverrideForTests = on;
                extract.Begin(in tables, 37, -12);
                extract.Emit(e, 600, 400, 812.25f);
                int stamped = Stamped(extract.Instances);
                int count = extract.Instances.Length;
                extract.Begin(in tables, 37, -12);
                extract.Select(e, 600, 400, 812.25f);

                return (stamped, count, extract.Selected.ToArray());
            }
            finally
            {
                DepthModel.OverrideForTests = null;
            }
        }

        [Theory]
        [MemberData(nameof(DynamicSpriteTests.MobileCases), MemberType = typeof(DynamicSpriteTests))]
        public void Anchor_Is_Written_Only_With_The_Switch_On(string name)
        {
            (int offStamped, int offCount, DynamicSprite[] offSel) = Run(false, name);
            offCount.Should().BeGreaterThan(0);
            offStamped.Should().Be(0, "the default path carries no anchor");

            foreach (DynamicSprite s in offSel)
            {
                s.Geo.Should().BeFalse();
                s.GeoAx.Should().Be(0);
                s.GeoAy.Should().Be(0);
                s.GeoZ.Should().Be(0);
            }

            (int onStamped, int onCount, DynamicSprite[] onSel) = Run(true, name);
            onCount.Should().Be(offCount);

            // seated sections keep Corners for the deform; shadows are not stamped
            if (name == "sitting")
            {
                onStamped.Should().Be(0);
            }
            else
            {
                onStamped.Should().BeGreaterThan(0);
            }

            onSel.Should().Contain(s => s.Geo);
        }

        // audit clip-12 P2: the mount was emitted before drawY += mountOffset and the rider after, so their anchors differed by
        // the seat offset. One composite anchor (the unmounted draw point) for every layer.
        [Fact]
        public void Mounted_Layers_Share_One_Composite_Anchor()
        {
            (_, _, DynamicSprite[] mounted) = Run(true, "mounted");
            (_, _, DynamicSprite[] plain) = Run(true, "plain");
            int geo = 0;
            (int X, int Y)? anchor = null;
            var bodies = new System.Collections.Generic.HashSet<ushort>();

            foreach (DynamicSprite s in mounted)
            {
                if (!s.Geo)
                {
                    continue;
                }

                geo++;
                bodies.Add(s.Body);
                (int X, int Y) a = (s.X + s.GeoAx, s.Y + s.GeoAy);
                anchor ??= a;
                a.Should().Be(anchor.Value, $"body {s.Body}");
            }

            geo.Should().BeGreaterThan(1);
            bodies.Should().HaveCountGreaterThan(1, "mount and rider");

            DynamicSprite p = Array.Find(plain, s => s.Geo);
            anchor.Should().Be((p.X + p.GeoAx, p.Y + p.GeoAy), "the anchor is taken before the seat offset (-9 here)");
        }

        [Fact]
        public void Frame_Cache_Records_Again_When_The_Switch_Flips()
        {
            (AnimationsLoader anims, GpuSpriteTables tables) = DynamicSpriteTests.AnimFixture(5);
            var mirror = new WorldMirror();
            EcsEntity e = DynamicSpriteTests.MobileCase(mirror, "equipped");
            var extract = new MobileSpriteExtract(anims);
            var cache = new MobileFrameCache { SelectValid = true };

            int Emit(bool on)
            {
                DepthModel.OverrideForTests = on;
                EcsRow data = EcsRead.Row(e);
                extract.Begin(in tables, 37, -12);
                extract.Emit(e, data, in data.Get<AnimationState>(), in data.Get<MobileRenderState>(), 600, 400, 812.25f, cache);

                return Stamped(extract.Instances);
            }

            try
            {
                Emit(false).Should().Be(0);
                Emit(false).Should().Be(0);
                cache.Geo.Should().BeFalse();
                Emit(true).Should().BeGreaterThan(0);
                cache.Geo.Should().BeTrue();
                Emit(true).Should().BeGreaterThan(0, "a replay keeps the anchors");
                Emit(false).Should().Be(0);
            }
            finally
            {
                DepthModel.OverrideForTests = null;
            }
        }
    }
}
