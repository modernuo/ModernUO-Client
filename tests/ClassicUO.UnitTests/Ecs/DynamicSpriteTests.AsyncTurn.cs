using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Game;
using Uo.Pack;
using Xunit;
using EcsEntity = Friflo.Engine.ECS.Entity;
using RendererAnimations = ClassicUO.Renderer.Animations.Animations;

namespace ClassicUO.UnitTests.Ecs
{
    // anim_async_turn: a turn of a body already drawn decodes on a worker; the draw keeps the previous look until it is
    // uploaded (the hold, also with asset_prefetch_on_packet off), but a first sight is not waited on without that switch.
    public partial class DynamicSpriteTests
    {
        private static void WithAsyncTurn(bool on, Action body)
        {
            LookPrefetch.OverrideForTests = false;
            AnimAsyncTurn.OverrideForTests = on;
            LookPrefetch.TicksForTests = 1000;

            try
            {
                body();
            }
            finally
            {
                LookPrefetch.OverrideForTests = null;
                AnimAsyncTurn.OverrideForTests = null;
                LookPrefetch.TicksForTests = null;
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Legacy_Async_Turn_Keeps_The_Previous_Look_Only_With_The_Switch(bool on)
        {
            WithAsyncTurn(on, () =>
            {
                Assert.Equal(on, LookPrefetch.HoldEnabled);
                (AnimationsLoader anims, _) = AnimFixture(5);
                var atlas = new FakeAtlas(anims, Array.Empty<SpriteEntry>());
                EcsEntity e = MobileCase(new WorldMirror(), "equipped");
                var extract = new MobileSpriteExtract(anims);
                var cache = new MobileFrameCache { SelectValid = true, SelectGen = 1 };

                List<LegacyDraw> before = DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache);

                // the turn's direction is on a worker
                atlas.PendingBody = HUMAN;
                Turn(e, cache);
                List<LegacyDraw> partial = FreshLegacyCalls(extract, atlas, e, 600, 400, 812.25f, out _, out _);
                AssertSameCalls(on ? before : partial, DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache), on ? "held" : "partial");

                // uploaded: the new direction in one frame
                atlas.PendingBody = 0xFFFF;
                cache.SelectGen++;
                List<LegacyDraw> fresh = FreshLegacyCalls(extract, atlas, e, 600, 400, 812.25f, out _, out _);
                AssertSameCalls(fresh, DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache), "switched");
                Assert.Equal(0u, cache.HoldSince);
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Legacy_Async_Turn_Alone_Does_Not_Wait_On_A_First_Sight(bool on)
        {
            WithAsyncTurn(on, () =>
            {
                (AnimationsLoader anims, _) = AnimFixture(5);
                var atlas = new FakeAtlas(anims, Array.Empty<SpriteEntry>()) { PendingBody = HUMAN };
                EcsEntity e = MobileCase(new WorldMirror(), "equipped");
                var extract = new MobileSpriteExtract(anims);
                var cache = new MobileFrameCache { SelectValid = true, SelectGen = 1 };

                List<LegacyDraw> partial = FreshLegacyCalls(extract, atlas, e, 600, 400, 812.25f, out _, out _);
                Assert.NotEmpty(partial);
                AssertSameCalls(partial, DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache), "first sight");
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Async_Turn_Sends_A_Drawn_Bodys_Uncached_Direction_To_A_Worker_Only_With_The_Switch(bool on)
        {
            Assert.True(RendererAnimations.AsyncFirstSight);

            // a turn (body drawn, chunks not cached): the worker with the switch, else the main thread (the stall)
            Assert.Equal(on, RendererAnimations.DecodesOnWorker(sync: false, bodyDrawn: true, evicted: false, asyncTurn: on, resident: false));

            // unchanged by the switch: first sight and evicted directions on a worker; cached chunks and sync callers here
            Assert.True(RendererAnimations.DecodesOnWorker(false, bodyDrawn: false, evicted: false, asyncTurn: on, resident: false));
            Assert.True(RendererAnimations.DecodesOnWorker(false, bodyDrawn: true, evicted: true, asyncTurn: on, resident: false));
            Assert.False(RendererAnimations.DecodesOnWorker(false, bodyDrawn: true, evicted: false, asyncTurn: on, resident: true));
            Assert.False(RendererAnimations.DecodesOnWorker(sync: true, bodyDrawn: true, evicted: false, asyncTurn: on, resident: false));
            Assert.False(RendererAnimations.DecodesOnWorker(sync: true, bodyDrawn: false, evicted: false, asyncTurn: on, resident: false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Async_Turn_Notes_Only_With_The_Switch_And_Bounds_The_Queue(bool on)
        {
            AnimAsyncTurn.OverrideForTests = on;
            AnimAsyncTurn.ResetForTests();

            try
            {
                for (uint s = 1; s <= AnimAsyncTurn.Capacity + 5; s++)
                {
                    AnimAsyncTurn.Note(s);
                }

                AnimAsyncTurn.Note(0);

                if (!on)
                {
                    Assert.Equal(0, AnimAsyncTurn.Count);
                    Assert.False(AnimAsyncTurn.TryTake(out _));

                    return;
                }

                Assert.Equal(AnimAsyncTurn.Capacity, AnimAsyncTurn.Count);
                Assert.Equal(5L, AnimAsyncTurn.Dropped);
                Assert.True(AnimAsyncTurn.TryTake(out uint first));
                Assert.Equal(6u, first); // the oldest went
            }
            finally
            {
                AnimAsyncTurn.ResetForTests();
                AnimAsyncTurn.OverrideForTests = null;
            }
        }

        [Fact]
        public void Async_Turn_Requests_The_Facing_Direction_Then_Its_Neighbours()
        {
            Span<byte> d = stackalloc byte[3];
            AnimAsyncTurn.Directions(0, d);
            Assert.Equal(new byte[] { 0, 1, 7 }, d.ToArray());
            AnimAsyncTurn.Directions(7, d);
            Assert.Equal(new byte[] { 7, 0, 6 }, d.ToArray());
            AnimAsyncTurn.Directions(4, d);
            Assert.Equal(new byte[] { 4, 5, 3 }, d.ToArray());
        }
    }
}
