using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using Microsoft.Xna.Framework;
using Uo.Pack;
using Uo.Render;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.UnitTests.Ecs
{
    // asset_prefetch_on_packet: a drawn mobile whose new look is not loaded keeps its previous complete look (Legacy: an atlas
    // direction still decoding; Gpu: a page not resident), then switches in one frame; switch off: the new look is drawn
    // as far as it is loaded (the behaviour before the switch).
    public partial class DynamicSpriteTests
    {
        private sealed class FakeResidency : IDynamicPageResidency
        {
            public bool AllMissing;
            public int Requests;

            public bool Resident(in SpriteInstance instance) => !AllMissing;

            public void Request(in SpriteInstance instance) => Requests++;
        }

        private static void WithHold(bool on, Action body)
        {
            LookPrefetch.OverrideForTests = on;
            LookPrefetch.TicksForTests = 1000;

            try
            {
                body();
            }
            finally
            {
                LookPrefetch.OverrideForTests = null;
                LookPrefetch.TicksForTests = null;
            }
        }

        // the new look: the mobile turned to a direction it never drew (its selection changed)
        private static void Turn(EcsEntity e, MobileFrameCache cache)
        {
            e.GetComponent<AnimationState>().Dir = 3;
            e.GetComponent<AnimationState>().Facing = 3;
            cache.SelectGen++;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Legacy_New_Look_Decoding_Keeps_The_Previous_Look_Only_With_The_Switch(bool on)
        {
            WithHold(on, () =>
            {
                (AnimationsLoader anims, _) = AnimFixture(5);
                var atlas = new FakeAtlas(anims, Array.Empty<SpriteEntry>());
                EcsEntity e = MobileCase(new WorldMirror(), "equipped");
                var extract = new MobileSpriteExtract(anims);
                var cache = new MobileFrameCache { SelectValid = true, SelectGen = 1 };

                List<LegacyDraw> before = DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache);
                Assert.True(cache.LegacyValid);

                // the body's new direction is decoding
                atlas.PendingBody = HUMAN;
                Turn(e, cache);
                List<LegacyDraw> partial = FreshLegacyCalls(extract, atlas, e, 600, 400, 812.25f, out _, out _);
                List<LegacyDraw> held = DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache);
                AssertSameCalls(on ? before : partial, held, on ? "held" : "partial");
                Assert.True(partial.Count < before.Count);

                // decoded: the new look at once
                atlas.PendingBody = 0xFFFF;
                cache.SelectGen++;
                List<LegacyDraw> fresh = FreshLegacyCalls(extract, atlas, e, 600, 400, 812.25f, out _, out _);
                AssertSameCalls(fresh, DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache), "switched");
                Assert.Equal(0u, cache.HoldSince);
            });
        }

        [Fact]
        public void Legacy_Hold_Ends_After_The_Bound_And_Never_Holds_Without_A_Previous_Look()
        {
            WithHold(true, () =>
            {
                (AnimationsLoader anims, _) = AnimFixture(5);
                var atlas = new FakeAtlas(anims, Array.Empty<SpriteEntry>()) { PendingBody = HUMAN };
                EcsEntity e = MobileCase(new WorldMirror(), "equipped");
                var extract = new MobileSpriteExtract(anims);
                var cache = new MobileFrameCache { SelectValid = true, SelectGen = 1 };

                // first sight: nothing to hold, the loaded layers are drawn
                List<LegacyDraw> partial = FreshLegacyCalls(extract, atlas, e, 600, 400, 812.25f, out _, out _);
                AssertSameCalls(partial, DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache), "first sight");
                Assert.False(cache.LegacyValid);

                atlas.PendingBody = 0xFFFF;
                cache.SelectGen++;
                List<LegacyDraw> before = DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache);

                atlas.PendingBody = HUMAN;
                Turn(e, cache);
                AssertSameCalls(before, DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache), "held");

                long timeouts = LookPrefetch.HoldTimeouts;
                LookPrefetch.TicksForTests = 1000 + LookPrefetch.HoldMaxMs + 1;
                cache.SelectGen++;
                partial = FreshLegacyCalls(extract, atlas, e, 600, 400, 812.25f, out _, out _);
                AssertSameCalls(partial, DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache), "bound");
                Assert.True(LookPrefetch.HoldTimeouts > timeouts);
            });
        }

        private static SpriteInstance[] EmitCached(MobileSpriteExtract x, in GpuSpriteTables t, EcsEntity e, MobileFrameCache c)
        {
            x.Begin(in t, 37, -12);
            EcsRow data = EcsRead.Row(e);
            x.Emit(e, data, in data.Get<AnimationState>(), in data.Get<MobileRenderState>(), 600, 400, 812.25f, c);

            return x.Instances.ToArray();
        }

        private static SpriteInstance[] EmitFresh(MobileSpriteExtract x, in GpuSpriteTables t, EcsEntity e)
        {
            x.Begin(in t, 37, -12);
            x.Emit(e, 600, 400, 812.25f);

            return x.Instances.ToArray();
        }

        private static void AssertSameInstances(SpriteInstance[] want, SpriteInstance[] got, string name)
        {
            Assert.True(want.Length > 0, name);
            Assert.Equal(want.Length, got.Length);

            for (int i = 0; i < want.Length; i++)
            {
                Assert.True(want[i].X == got[i].X && want[i].Y == got[i].Y && want[i].Depth == got[i].Depth && want[i].Whf == got[i].Whf &&
                    want[i].Uv == got[i].Uv && want[i].PageHue == got[i].PageHue, $"{name}: instance {i}");
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Gpu_New_Look_Not_Resident_Keeps_The_Previous_Look_Only_With_The_Switch(bool on)
        {
            WithHold(on, () =>
            {
                (AnimationsLoader anims, GpuSpriteTables tables) = AnimFixture(5);
                EcsEntity e = MobileCase(new WorldMirror(), "equipped");
                var extract = new MobileSpriteExtract(anims);
                var pages = new FakeResidency();
                extract.PageResidency = pages;
                var cache = new MobileFrameCache { SelectValid = true, SelectGen = 1 };

                SpriteInstance[] before = EmitCached(extract, in tables, e, cache);
                Assert.True(cache.EmitValid);

                pages.AllMissing = true;
                Turn(e, cache);
                SpriteInstance[] turned = EmitFresh(extract, in tables, e);
                SpriteInstance[] got = EmitCached(extract, in tables, e, cache);
                // switch off: the new look's instances (the renderer draws them as their pages arrive)
                AssertSameInstances(on ? before : turned, got, on ? "held" : "new look");
                Assert.Equal(on ? turned.Length : 0, pages.Requests);

                pages.AllMissing = false;
                cache.SelectGen++;
                AssertSameInstances(turned, EmitCached(extract, in tables, e, cache), "switched");
                Assert.Equal(0u, cache.HoldSince);
                // and the recording is the new look's
                AssertSameInstances(turned, EmitCached(extract, in tables, e, cache), "replayed");
            });
        }

        [Fact]
        public void Gpu_Hold_Ends_After_The_Bound()
        {
            WithHold(true, () =>
            {
                (AnimationsLoader anims, GpuSpriteTables tables) = AnimFixture(5);
                EcsEntity e = MobileCase(new WorldMirror(), "plain");
                var extract = new MobileSpriteExtract(anims);
                var pages = new FakeResidency();
                extract.PageResidency = pages;
                var cache = new MobileFrameCache { SelectValid = true, SelectGen = 1 };

                SpriteInstance[] before = EmitCached(extract, in tables, e, cache);
                pages.AllMissing = true;
                Turn(e, cache);
                AssertSameInstances(before, EmitCached(extract, in tables, e, cache), "held");

                LookPrefetch.TicksForTests = 1000 + LookPrefetch.HoldMaxMs + 1;
                cache.SelectGen++;
                AssertSameInstances(EmitFresh(extract, in tables, e), EmitCached(extract, in tables, e, cache), "bound");
            });
        }
    }
}
