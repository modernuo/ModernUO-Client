using System;
using System.Reflection;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using Xunit;

namespace ClassicUO.UnitTests.Game.Map
{
    /// <summary>
    /// The chunk pool and the map object recycler belong to the World whose objects they hold: a chunk or object built for one
    /// World is never handed out into another, and a World's pools are released with it (follow-up of the login warm-up audit:
    /// the chunk pool used to be static, so a second World got chunks that still pointed at the first).
    /// </summary>
    public class WorldPoolOwnershipTests
    {
        // read through the field so the test also compiles against the static-pool version
        private static World OwnerOf(Chunk c) =>
            (World)typeof(Chunk).GetField("_world", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(c);

        [Fact]
        public void A_Second_World_Never_Gets_A_Chunk_Of_The_First()
        {
            var w1 = new World();
            var w2 = new World();
            w1.ChunkPool.Prefill(5, 64);

            Chunk c = Chunk.Create(w2, 1, 2);

            Assert.Same(w2, OwnerOf(c));
            Assert.Equal(5, w1.ChunkPool.Count); // untouched
            Assert.Equal(0, w2.ChunkPool.Count);
        }

        [Fact]
        public void Chunks_Created_And_Destroyed_Return_To_Their_Own_Worlds_Pool()
        {
            var w1 = new World();
            var w2 = new World();
            Chunk a = Chunk.Create(w1, 1, 1);
            Chunk b = Chunk.Create(w2, 2, 2);

            a.Destroy();
            b.Destroy();

            Assert.Equal(1, w1.ChunkPool.Count);
            Assert.Equal(1, w2.ChunkPool.Count);
            Assert.Same(a, Chunk.Create(w1, 3, 3));
            Assert.Same(b, Chunk.Create(w2, 4, 4));
        }

        [Fact]
        public void A_Pool_Refuses_Another_Worlds_Chunk()
        {
            var w1 = new World();
            var w2 = new World();
            var foreign = new Chunk(w2) { IsDestroyed = true };

            Assert.False(w1.ChunkPool.CanAdd(foreign));
            Assert.Throws<InvalidOperationException>(() => w1.ChunkPool.Add(foreign));
            Assert.Equal(0, w1.ChunkPool.Count);
        }

        [Fact]
        public void The_Pool_Is_Capped_And_Churn_Does_Not_Grow_It()
        {
            var w = new World();
            w.ChunkPool.Prefill(Constants.PREDICTABLE_CHUNKS * 2, 16);
            Assert.Equal(Constants.PREDICTABLE_CHUNKS, w.ChunkPool.Count);

            // a "session": take chunks, destroy them again, several times over (relog cycles)
            for (int session = 0; session < 5; session++)
            {
                var live = new Chunk[Constants.PREDICTABLE_CHUNKS + 50];

                for (int i = 0; i < live.Length; i++)
                {
                    live[i] = Chunk.Create(w, i, session);
                }

                foreach (Chunk c in live)
                {
                    c.Destroy();
                }

                Assert.Equal(Constants.PREDICTABLE_CHUNKS, w.ChunkPool.Count);
            }
        }

        [Fact]
        public void Release_Empties_The_Chunk_And_Object_Pools()
        {
            var w = new World();
            w.ChunkPool.Prefill(10, 16);
            w.MapRecycler.Prefill(w, 10, 10, 0);
            Assert.True(w.ChunkPool.Count > 0);

            w.ReleasePools();

            Assert.Equal(0, w.ChunkPool.Count);
            Assert.Equal(0, w.MapRecycler.PooledLands);
            Assert.Equal(0, w.MapRecycler.PooledStatics);
        }

        [Fact]
        public void A_Recycler_Ignores_Objects_Of_Another_World()
        {
            var w1 = new World();
            var w2 = new World();
            var recycler = new MapObjectRecycler(true, 0, w1);

            Land foreign = (Land)Activator.CreateInstance(typeof(Land), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { w2 }, null);
            foreign.Destroy();
            recycler.Retire(foreign, 0);
            Static foreignStatic = new Static(w2);
            foreignStatic.Destroy();
            recycler.Retire(foreignStatic, 0);

            Assert.Equal(0, recycler.PooledLands);
            Assert.Equal(0, recycler.PooledStatics);
            Assert.Null(recycler.TakeLand(1000));

            recycler.Prefill(w2, 5, 5, 0); // another World's prefill is refused too
            Assert.Equal(0, recycler.PooledLands);

            recycler.Prefill(w1, 5, 5, 0);
            Assert.Equal(5, recycler.PooledLands);
            Assert.Same(w1, recycler.TakeLand(1000).World);
        }

        [Fact]
        public void Each_World_Has_Its_Own_Recycler_And_Pool()
        {
            var w1 = new World();
            var w2 = new World();

            Assert.NotSame(w1.MapRecycler, w2.MapRecycler);
            Assert.NotSame(w1.ChunkPool, w2.ChunkPool);
        }

        [Fact]
        public void Warmup_Prefills_Once_Per_World_And_Login()
        {
            if (!MapWarmup.Enabled)
            {
                return;
            }

            var w1 = new World();
            var w2 = new World();

            try
            {
                MapWarmup.OnMapCreated(w1);
                int chunks = w1.ChunkPool.Count;
                Assert.Equal(MapWarmup.Chunks, chunks);
                Assert.True(w1.WarmupDone);
                Assert.Equal(0, w2.ChunkPool.Count); // another World's pools are not filled by it

                w1.ChunkPool.Take();
                MapWarmup.OnMapCreated(w1); // facet change in the same login: no refill
                Assert.Equal(chunks - 1, w1.ChunkPool.Count);

                MapWarmup.OnMapCreated(w2); // a second World gets its own prefill
                Assert.Equal(MapWarmup.Chunks, w2.ChunkPool.Count);
                Assert.Equal(chunks - 1, w1.ChunkPool.Count);

                // relog (World.Clear re-arms it) tops the pool up; it does not grow past the first login's
                w1.WarmupDone = false;
                MapWarmup.OnMapCreated(w1);
                Assert.Equal(chunks, w1.ChunkPool.Count);

                w1.ReleasePools();
                w2.ReleasePools();
                Assert.Equal(0, w1.ChunkPool.Count);
                Assert.Equal(0, w2.ChunkPool.Count);
            }
            finally
            {
                MapWarmup.Cancel("test");
            }
        }
    }
}
