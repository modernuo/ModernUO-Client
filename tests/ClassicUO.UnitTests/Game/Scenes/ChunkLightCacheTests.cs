using System;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Game.Scenes;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    [Collection("ChunkStaticTileDataCollection")]
    public sealed class ChunkLightCacheTests : IDisposable
    {
        private readonly StaticTiles[] _saved = Chunk.StaticTileData;
        private readonly bool[] _emitters = LightEmitters.OverrideForTests;
        private readonly StaticTiles[] _data = new StaticTiles[0x4000];
        private readonly World _world = new World();
        private readonly Chunk _chunk;

        public ChunkLightCacheTests()
        {
            _data[10].Flags = TileFlag.LightSource;
            Chunk.StaticTileData = _data;
            LightEmitters.OverrideForTests = new bool[0x4000];
            LightEmitters.OverrideForTests[10] = true;
            _chunk = Chunk.Create(_world, 0, 0);
        }

        public void Dispose()
        {
            _chunk.Destroy();
            Chunk.StaticTileData = _saved;
            LightEmitters.OverrideForTests = _emitters;
            ChunkLightCache.OverrideForTests = null;
        }

        private Static Lamp(int x = 1)
        {
            var obj = new Static(_world) { Graphic = 10, X = (ushort)x, Y = 1 };
            _chunk.AddGameObject(obj, x, 1);
            return obj;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GraphicChange_preserves_authoritative_order(bool on)
        {
            ChunkLightCache.OverrideForTests = on;
            Static a = Lamp(), b = Lamp(2), c = Lamp(3);
            a.SetGraphic(11);
            if (on)
            {
                using var cache = new ChunkLightCache();
                cache.Refresh(_chunk, _data);
                LightEmitters.InvalidateRows();
                cache.Refresh(_chunk, _data);
            }
            Assert.Equal(3, _chunk.LightSourceCount);
            Assert.Same(a, _chunk.LightSources[0]);
            Assert.Same(b, _chunk.LightSources[1]);
            Assert.Same(c, _chunk.LightSources[2]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EmitterCache_MobileOnlyChurnDoesNotRebuild(bool on)
        {
            ChunkLightCache.OverrideForTests = on;
            Lamp();
            var cache = new ChunkLightCache();
            try
            {
                Assert.True(cache.Refresh(_chunk, _data));
                int builds = cache.Rebuilds;
                var mobile = new Mobile(_world);
                _chunk.OnTileContentChanged(mobile);
                Assert.True(cache.Refresh(_chunk, _data));
                Assert.Equal(builds, cache.Rebuilds);
                Assert.Equal(1, cache.Count);
                Assert.Equal(on, ChunkLightCache.Enabled);
            }
            finally { cache.Dispose(); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EmitterCache_ItemMutationAndLightIdRefresh(bool on)
        {
            ChunkLightCache.OverrideForTests = on;
            Item item = _world.GetOrCreateItem(0x40001000);
            _world.Ecs.CommitItem(item, new ItemState { Serial = item.Serial, Graphic = 11, Container = 0xFFFFFFFF });
            _chunk.AddGameObject(item, 2, 1);
            using var cache = new ChunkLightCache();
            cache.Refresh(_chunk, _data);
            Assert.Equal(0, cache.Count);
            _world.Ecs.CommitItem(item, new ItemState { Serial = item.Serial, Graphic = 10, Container = 0xFFFFFFFF });
            cache.Refresh(_chunk, _data);
            Assert.Equal(1, cache.Count);
            int builds = cache.Rebuilds;
            _world.Ecs.CommitLightId(item, 26);
            cache.Refresh(_chunk, _data);
            Assert.True(cache.Rebuilds > builds);
            Assert.Equal(26, cache.LightId[0]);
            item.RemoveFromTile();
            cache.Refresh(_chunk, _data);
            Assert.Equal(0, cache.Count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EmitterCache_SeasonAndRecycleRefresh(bool on)
        {
            ChunkLightCache.OverrideForTests = on;
            Static first = Lamp(), second = Lamp(2);
            using var cache = new ChunkLightCache();
            cache.Refresh(_chunk, _data);
            first.SetGraphic(11);
            cache.Refresh(_chunk, _data);
            Assert.Equal(2, cache.Count);
            Assert.Same(first, cache.Resolve(0));
            Assert.False(cache.DrawsNow(0));
            Assert.Same(second, cache.Resolve(1));
            first.SetGraphic(10);
            cache.Refresh(_chunk, _data);
            Assert.Same(first, cache.Resolve(0));
            Assert.Same(second, cache.Resolve(1));
            uint life = cache.Incarnation[0];
            first.RemoveFromTile();
            first.ResetForReuse();
            first.Graphic = 10;
            _chunk.AddGameObject(first, 1, 1);
            cache.Refresh(_chunk, _data);
            Assert.NotEqual(life, cache.Incarnation[1]);
            LightEmitters.InvalidateRows();
            int seasonBuilds = cache.Rebuilds;
            cache.Refresh(_chunk, _data);
            Assert.True(cache.Rebuilds > seasonBuilds);
            LightEmitters.Reset();
            int builds = cache.Rebuilds;
            cache.Refresh(_chunk, _data);
            Assert.True(cache.Rebuilds > builds);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EmitterCache_PreservesBandDrawnRoofAndMaxZ(bool on)
        {
            ChunkLightCache.OverrideForTests = on;
            var obj = Lamp();
            using var cache = new ChunkLightCache();
            cache.Refresh(_chunk, _data);
            obj.DrawnFrame = 8;
            Assert.True(ChunkLights.LightsDrawn(cache.Resolve(0).DrawnFrame, 8, false));
            Assert.False(ChunkLights.LightsDrawn(cache.Resolve(0).DrawnFrame, 9, false));
            Assert.True(ChunkLights.EdgeVisible(0, 0, false, false, false, false, 1, 127));
            Assert.False(ChunkLights.EdgeVisible(1, 0, false, false, false, false, 1, 127));
            Assert.False(ChunkLights.EdgeVisible(0, 0, false, true, true, false, 1, 127));
            Assert.False(ChunkLights.EdgeVisible(0, 128, false, false, false, false, 1, 127));
            Assert.True(ViewMargin.InBand(-10, 50, 0, 0, 100, 100, 20));
            Assert.False(ViewMargin.InBand(-30, 50, 0, 0, 100, 100, 20));
            Assert.True(cache.EdgeCandidate(0, obj, 9, 0, 0, 0, 0, 100, 100, 40, false, 127, 127, out int x, out int y));
            Assert.Equal(0, x);
            Assert.Equal(44, y);
            Assert.False(cache.EdgeCandidate(0, obj, 8, 0, 0, 0, 0, 100, 100, 40, false, 127, 127, out _, out _));
            Assert.False(cache.EdgeCandidate(0, obj, 9, 0, 0, 0, 0, 100, 100, 40, false, 0, 127, out _, out _));
            _data[10].Flags |= TileFlag.Roof;
            LightEmitters.Reset();
            cache.Refresh(_chunk, _data);
            Assert.False(cache.EdgeCandidate(0, obj, 9, 0, 0, 0, 0, 100, 100, 40, true, 127, 127, out _, out _));
            Assert.True(cache.EdgeCandidate(0, obj, 9, 0, 0, 0, 0, 100, 100, 40, false, 127, 127, out _, out _));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LightingCache_OverflowFallsBackWithoutAllocation(bool on)
        {
            ChunkLightCache.OverrideForTests = on;
            Static lamp = Lamp();
            _chunk.LightSources = new TileListObject[ChunkLightCache.MaxRows + 1];
            Array.Fill(_chunk.LightSources, lamp);
            _chunk.LightSourceCount = _chunk.LightSources.Length;
            using var cache = new ChunkLightCache();
            _chunk.LightCache = cache;
            Assert.False(cache.Refresh(_chunk, _data));
            int builds = cache.Rebuilds;
            var sink = new CountSink();
            ChunkLightOffers.Collect(_chunk, _data, on, false, ref sink);
            sink.Count = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool fallback = !cache.Refresh(_chunk, _data);
            ChunkLightOffers.Collect(_chunk, _data, on, false, ref sink);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(fallback);
            Assert.Equal(0, allocated);
            Assert.Equal(builds, cache.Rebuilds);
            Assert.Equal(0, cache.Count);
            Assert.Equal(ChunkLightCache.MaxRows + 1, sink.Count);
            Array.Clear(_chunk.LightSources);
            _chunk.LightSourceCount = 0;
        }

        private struct CountSink : IChunkLightSink
        {
            internal int Count;
            public void Offer(GameObject obj, ChunkLightCache cache, int row, bool ground) => Count++;
        }

#if CUO_DEVTOOLS
        [Fact]
        public void Checker_detects_order_shape_lifetime_share_and_refusal_changes()
        {
            var lights = new FrameLights(2);
            lights.BeginFrame(1, 2, true);
            lights.Next() = new FrameLights.Entry { Source = Lamp(), SourceLife = 7, ShapeWidth = 32, Share = 1 };
            lights.Commit(2);
            var shadow = new FrameLights(2);
            lights.CopyTo(shadow);
            shadow[0].ShapeWidth = 64;
            Assert.Equal(0, lights.FirstDifference(shadow));
            lights.CopyTo(shadow);
            shadow[0].SourceLife++;
            Assert.Equal(0, lights.FirstDifference(shadow));
            lights.CopyTo(shadow);
            shadow[0].Share = 0.5f;
            Assert.Equal(0, lights.FirstDifference(shadow));
            lights.CopyTo(shadow);
            shadow.Refused++;
            Assert.Equal(-2, lights.FirstDifference(shadow));
            Assert.Equal(1, lights[0].Share);
        }

#endif
    }
}
