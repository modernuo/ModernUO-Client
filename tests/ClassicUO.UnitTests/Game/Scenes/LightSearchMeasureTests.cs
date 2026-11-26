using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Game.Scenes;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    public sealed class LightSearchMeasureTests
    {
        [Fact]
        public void Counts_repeated_reads_and_unique_candidates_across_lights_and_bake_passes()
        {
            using var m = new LightSearchMeasure();
            var a = new Static(new World());
            var b = new Static(a.World);
            m.Begin(default, new List<Chunk>(), true);
            m.Read(a); m.Read(b); m.Read(a);
            m.Query(a, 1, default); m.Query(a, 1, default); m.Query(b, 1, default);
            Assert.Equal(3, m.Reads);
            Assert.Equal(2, m.Unique);
            Assert.Equal(2, m.Queries);
            Assert.Equal(0, m.Hits);
        }

        [Fact]
        public void Structural_hits_require_previous_frame_target_scene_lifetime_and_all_ordered_chunks()
        {
            using var m = new LightSearchMeasure();
            var world = new World();
            var a = new Static(world);
            var chunks = new List<Chunk> { new Chunk(world), new Chunk(world) };
            var target = new LightSearchMeasure.Target();
            var scene = new LightSearchMeasure.Scene();
            void Pass(int expected)
            {
                m.Begin(scene, chunks, true);
                m.Query(a, 1, target);
                Assert.Equal(expected, m.Hits);
            }
            Pass(0); Pass(1);
            chunks[1].OccluderVersion++; // also a previously rejected chunk
            Pass(0); Pass(1);
            chunks.Reverse();
            Pass(0);
            target = target with { Px = 1 };
            Pass(0); Pass(1);
            scene = scene with { MaxZ = 10 };
            Pass(0); Pass(1);
            a.Incarnation++;
            Pass(0);
            m.Begin(scene, chunks, true); // missing light in this frame
            Pass(0);
            m.Begin(scene, chunks, false);
            m.Query(a, 1, target);
            Assert.Equal(0, m.Hits);
        }

        [Fact]
        public void Warm_measurement_has_no_allocations()
        {
            using var m = new LightSearchMeasure();
            var a = new Static(new World());
            var chunks = new List<Chunk>();
            for (int i = 0; i < 4; i++) { m.Begin(default, chunks, true); m.Read(a); m.Query(a, 1, default); }
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 16; i++) { m.Begin(default, chunks, true); m.Read(a); m.Query(a, 1, default); }
            Assert.Equal(0, System.GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }
}
