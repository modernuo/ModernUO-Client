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
    // A turn of a body already drawn decodes on a worker; the draw keeps the previous look until it is uploaded (the hold,
    // also with asset_prefetch_on_packet off), but a first sight is not waited on without that switch.
    public partial class DynamicSpriteTests
    {
        [Fact]
        public void Legacy_Async_Turn_Keeps_The_Previous_Look()
        {
            WithHold(false, () =>
            {
                (AnimationsLoader anims, _) = AnimFixture(5);
                var atlas = new FakeAtlas(anims, Array.Empty<SpriteEntry>());
                EcsEntity e = MobileCase(new WorldMirror(), "equipped");
                var extract = new MobileSpriteExtract(anims);
                var cache = new MobileFrameCache { SelectValid = true, SelectGen = 1 };

                List<LegacyDraw> before = DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache);

                // the turn's direction is on a worker
                atlas.PendingBody = HUMAN;
                Turn(e, cache);
                AssertSameCalls(before, DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache), "held");

                // uploaded: the new direction in one frame
                atlas.PendingBody = 0xFFFF;
                cache.SelectGen++;
                List<LegacyDraw> fresh = FreshLegacyCalls(extract, atlas, e, 600, 400, 812.25f, out _, out _);
                AssertSameCalls(fresh, DrawLegacyCalls(extract, atlas, e, 600, 400, 812.25f, cache), "switched");
                Assert.Equal(0u, cache.HoldSince);
            });
        }

        [Fact]
        public void Legacy_Async_Turn_Alone_Does_Not_Wait_On_A_First_Sight()
        {
            WithHold(false, () =>
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

        [Fact]
        public void Async_Turn_Sends_An_Uncached_Direction_To_A_Worker()
        {
            Assert.True(RendererAnimations.AsyncFirstSight);

            // chunks not cached (a first sight, an evicted direction or a turn of a body already drawn): the worker
            Assert.True(RendererAnimations.DecodesOnWorker(sync: false, resident: false));

            // cached chunks and sync callers: here
            Assert.False(RendererAnimations.DecodesOnWorker(sync: false, resident: true));
            Assert.False(RendererAnimations.DecodesOnWorker(sync: true, resident: false));
            Assert.False(RendererAnimations.DecodesOnWorker(sync: true, resident: true));
        }

        [Fact]
        public void Async_Turn_Bounds_The_Queue()
        {
            var queue = new AnimAsyncTurn(null);

            for (uint s = 1; s <= AnimAsyncTurn.Capacity + 5; s++)
            {
                queue.Note(s);
            }

            queue.Note(0);

            Assert.Equal(AnimAsyncTurn.Capacity, queue.Count);
            Assert.Equal(5L, queue.Dropped);
            Assert.True(queue.TryTake(out uint first));
            Assert.Equal(6u, first); // the oldest went
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
