using ClassicUO.UnitTests.Ecs;
using System;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Game.Scenes;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// light_occlusion_cache: the switch in both states, the occluder entry table (find, key, chunk versions, LRU eviction,
    /// idle sweep, release), and Chunk.OccluderVersion (bumped by statics and items joining or leaving a tile, not by mobiles).
    /// </summary>
    [Collection("ChunkStaticTileDataCollection")]
    public sealed class LightOcclusionCacheTests : IDisposable
    {
        private readonly World _world = new World();
        private readonly StaticTiles[] _savedStaticData;
        private uint _nextSerial = 0x4000_0200, _nextMobile = 0x200;

        public LightOcclusionCacheTests()
        {
            _savedStaticData = Chunk.StaticTileData;
            Chunk.StaticTileData = new StaticTiles[0x10000];
        }

        public void Dispose()
        {
            Chunk.StaticTileData = _savedStaticData;
            LightOcclusionCache.OverrideForTests = null;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Switch_FollowsOverride(bool on)
        {
            LightOcclusionCache.OverrideForTests = on;
            Assert.Equal(on, LightOcclusionCache.Enabled);
        }

        [Fact]
        public void Settings_DefaultOff()
        {
            Assert.False(new ClassicUO.Configuration.Settings().LightOcclusionCache);
        }

        private Static NewStatic() => new Static(_world) { Graphic = 1 };

        [Fact]
        public void Find_ReturnsEntryTakenForSourceAndId()
        {
            var table = new LightOccluderTable();
            Static src = NewStatic();
            Assert.Null(table.Find(src, 5, 1));
            LightOccluderEntry e = table.Take(src, 5, 1);
            e.SetKey(1, 2, 3, 0x10, 4, 5, 0, 0, 10, 10, 8, 0, 77);
            Assert.Same(e, table.Find(src, 5, 2));
            Assert.Null(table.Find(src, 6, 2));
            Assert.True(e.KeyMatches(1, 2, 3, 0x10, 4, 5, 0, 0, 10, 10, 8, 0, 77));
            Assert.False(e.KeyMatches(1, 2, 3, 0x10, 4, 6, 0, 0, 10, 10, 8, 0, 77));
            Assert.False(e.KeyMatches(1, 2, 3, 0x10, 4, 5, 0, 0, 10, 10, 8, 0, 78));
            Assert.False(e.KeyMatches(1, 2, 4, 0x10, 4, 5, 0, 0, 10, 10, 8, 0, 77));
        }

        [Fact]
        public void ChunksValid_FalseAfterVersionBumpOrDestroy()
        {
            var table = new LightOccluderTable();
            var a = new Chunk(_world) { X = 0, Y = 0 };
            var b = new Chunk(_world) { X = 1, Y = 0 };
            LightOccluderEntry e = table.Take(NewStatic(), 1, 1);
            e.AddChunk(a);
            e.AddChunk(b);
            Assert.True(e.ChunksValid());
            b.OccluderVersion++;
            Assert.False(e.ChunksValid());

            e = table.Take(NewStatic(), 1, 1);
            e.AddChunk(a);
            Assert.True(e.ChunksValid());
            a.IsDestroyed = true;
            Assert.False(e.ChunksValid());
        }

        [Fact]
        public void Take_EvictsLeastRecentlyUsedWhenFull()
        {
            var table = new LightOccluderTable(3, 1000);
            Static s1 = NewStatic(), s2 = NewStatic(), s3 = NewStatic(), s4 = NewStatic();
            table.Take(s1, 1, 1);
            table.Take(s2, 1, 2);
            table.Take(s3, 1, 3);
            table.Find(s1, 1, 4);
            table.Take(s4, 1, 5);
            Assert.Equal(3, table.Count);
            Assert.Null(table.Find(s2, 1, 6));
            Assert.NotNull(table.Find(s1, 1, 6));
            Assert.NotNull(table.Find(s3, 1, 6));
            Assert.NotNull(table.Find(s4, 1, 6));
            Assert.Equal(1, table.Evictions);
        }

        [Fact]
        public void Sweep_DropsIdleEntriesAndReleasesObjects()
        {
            var table = new LightOccluderTable(16, 10);
            Static s1 = NewStatic(), s2 = NewStatic(), cand = NewStatic();
            LightOccluderEntry e1 = table.Take(s1, 1, 1);
            e1.AddCandidate(cand);
            e1.AddChunk(new Chunk(_world));
            table.Take(s2, 1, 20);
            table.Sweep(25);
            Assert.Equal(1, table.Count);
            Assert.Null(table.Find(s1, 1, 25));
            Assert.Null(e1.Source);
            Assert.Equal(0, e1.CandidateCount);
            Assert.Null(e1.Candidates[0]);
            Assert.Null(e1.Chunks[0]);

            // the dropped entry is reused for the next source
            Assert.Same(e1, table.Take(NewStatic(), 1, 26));
            table.Reset();
            Assert.Equal(0, table.Count);
        }

        [Fact]
        public void Candidates_GrowPastInitialSize()
        {
            var table = new LightOccluderTable();
            LightOccluderEntry e = table.Take(NewStatic(), 1, 1);

            for (int i = 0; i < 40; i++)
            {
                e.AddCandidate(NewStatic());
                e.AddChunk(new Chunk(_world));
            }

            Assert.Equal(40, e.CandidateCount);
            Assert.Equal(40, e.ChunkCount);
            Assert.True(e.Bytes > 0);
        }

        [Fact]
        public void OccluderVersion_BumpsForStaticsAndItems_NotMobiles()
        {
            var chunk = new Chunk(_world) { X = 0, Y = 0 };
            uint v = chunk.OccluderVersion;

            Static s = NewStatic();
            chunk.AddGameObject(s, 1, 1);
            Assert.NotEqual(v, chunk.OccluderVersion);

            v = chunk.OccluderVersion;
            Mobile m = _world.GetOrCreateMobile(_nextMobile++);
            chunk.AddGameObject(m, 2, 2);
            Assert.Equal(v, chunk.OccluderVersion);
            m.RemoveFromTile();
            Assert.Equal(v, chunk.OccluderVersion);

            Item item = _world.GetOrCreateItem(_nextSerial++);
            item.TestSetGraphic(2);
            chunk.AddGameObject(item, 3, 3);
            Assert.NotEqual(v, chunk.OccluderVersion);

            v = chunk.OccluderVersion;
            s.RemoveFromTile();
            Assert.NotEqual(v, chunk.OccluderVersion);
        }
    }
}
