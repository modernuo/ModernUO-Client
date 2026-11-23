using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Renderer;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    /// <summary>
    /// meshlayer-growth: MeshLayerPool. A layer that grows by trading sets with the pool builds the same vertices, visible
    /// indices, runs and hue keys as one that grows in place; a pooled chunk's layer goes back to its base size; the spares stay
    /// under the budget; and a FIFO chunk pool's held bytes stay bounded where growth in place keeps every slot's high-water mark.
    /// </summary>
    [Collection(nameof(MeshVisibilityCollection))]
    public sealed class MeshLayerPoolTests
    {
        private static readonly Texture2D[] s_textures = MakeTextures(12);

        private static Texture2D[] MakeTextures(int n)
        {
            var t = new Texture2D[n];

            for (int i = 0; i < n; i++)
            {
                // identity only: never drawn, never disposed
                t[i] = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
                GC.SuppressFinalize(t[i]);
            }

            return t;
        }

        // ChunkMesh.Build's order: Reset, EnsureCapacity, Count, then every sprite written (texture-sorted)
        private static void Build(MeshLayer layer, MeshLayerPool pool, int count, int seed)
        {
            layer.Reset();

            if (pool != null)
            {
                layer.EnsureCapacity(count, pool);
            }
            else
            {
                layer.EnsureCapacity(count);
            }

            layer.Count = count;

            for (int i = 0; i < count; i++)
            {
                Texture2D tex = s_textures[i * s_textures.Length / Math.Max(1, count)];
                layer.Vertices[i].Position0 = new Vector3(i + seed, i * 2, i * 3);
                layer.Vertices[i].Hue0 = layer.Vertices[i].Hue1 = layer.Vertices[i].Hue2 = layer.Vertices[i].Hue3 = new Vector3(i % 7, 0, 1);
                layer.Textures[i] = tex;
            }
        }

        private static void Frame(MeshLayer layer, bool[] visible)
        {
            layer.ResetVisibility();

            for (int i = 0; i < layer.Count; i++)
            {
                if (visible[i])
                {
                    layer.SetVisible(i, 0xFF);
                }
            }

            layer.BuildVisibleIndices();
        }

        private static void ShouldMatch(MeshLayer a, MeshLayer b)
        {
            a.Count.Should().Be(b.Count);

            for (int i = 0; i < b.Count; i++)
            {
                a.Vertices[i].Should().Be(b.Vertices[i]);
                a.Textures[i].Should().BeSameAs(b.Textures[i]);
                a.HueKeyAt(i).Should().Be(b.HueKeyAt(i));
            }

            a.VisibleSpriteCount.Should().Be(b.VisibleSpriteCount);
            a.VisibleIndices.ToArray().Should().Equal(b.VisibleIndices.ToArray());
            a.VisibleRunCount.Should().Be(b.VisibleRunCount);

            for (int r = 0; r < b.VisibleRunCount; r++)
            {
                a.VisibleRuns[r].Texture.Should().BeSameAs(b.VisibleRuns[r].Texture);
                a.VisibleRuns[r].Start.Should().Be(b.VisibleRuns[r].Start);
                a.VisibleRuns[r].Count.Should().Be(b.VisibleRuns[r].Count);
            }
        }

        [Fact]
        public void Pooled_layers_build_and_draw_what_layers_grown_in_place_do()
        {
            var rnd = new Random(1234);
            var pool = new MeshLayerPool();
            var pooled = new[] { new MeshLayer(), new MeshLayer(), new MeshLayer() };
            var plain = new[] { new MeshLayer(), new MeshLayer(), new MeshLayer() };
            int[] sizes = { 0, 1, 40, 64, 65, 200, 256, 257, 700, 1500, 3000 };

            for (int round = 0; round < 60; round++)
            {
                int k = rnd.Next(pooled.Length);
                int count = sizes[rnd.Next(sizes.Length)];

                // a chunk going back to the chunk pool (ChunkMesh.SoftClear) before its next build, now and then
                if (rnd.Next(3) == 0)
                {
                    pooled[k].SoftReset(pool, 256);
                    plain[k].SoftReset();
                }

                Build(pooled[k], pool, count, round);
                Build(plain[k], null, count, round);

                // hue keys as the per-frame hue pass writes them, on some sprites
                for (int i = 0; i < count; i += 3)
                {
                    long key = MeshLayer.HueKey(round + 1, (ushort)i, 5);
                    pooled[k].SetHue(i, 2, 0, key);
                    plain[k].SetHue(i, 2, 0, key);
                }

                for (int f = 0; f < 3; f++)
                {
                    var visible = new bool[count];

                    for (int i = 0; i < count; i++)
                    {
                        visible[i] = rnd.Next(4) != 0;
                    }

                    Frame(pooled[k], visible);
                    Frame(plain[k], visible);
                    ShouldMatch(pooled[k], plain[k]);
                }
            }

            pool.Reused.Should().BeGreaterThan(0);
        }

        [Fact]
        public void A_new_set_from_the_pool_carries_no_textures_or_hue_keys_of_its_last_layer()
        {
            var pool = new MeshLayerPool();
            var a = new MeshLayer();
            Build(a, pool, 600, 0);

            for (int i = 0; i < 600; i++)
            {
                a.SetHue(i, 1, 0, MeshLayer.HueKey(9, (ushort)i, 1));
            }

            a.SoftReset(pool, 256);
            a.Vertices.Length.Should().Be(256);

            var b = new MeshLayer();
            b.Reset();
            b.EnsureCapacity(600, pool);
            b.Vertices.Length.Should().Be(1024, "the next power of two from 64, as in place");
            pool.Reused.Should().Be(1, "a's 1024 set");

            for (int i = 0; i < b.Vertices.Length; i++)
            {
                b.Textures[i].Should().BeNull();
                b.HueKeyAt(i).Should().Be(0);
            }
        }

        [Fact]
        public void Soft_reset_keeps_a_layer_at_or_below_the_base_size()
        {
            var pool = new MeshLayerPool();
            var layer = new MeshLayer();
            Build(layer, pool, 200, 0);
            layer.Vertices.Length.Should().Be(256);
            var before = layer.Vertices;

            layer.SoftReset(pool, 256);

            layer.Vertices.Should().BeSameAs(before);
            layer.Count.Should().Be(0);
        }

        [Fact]
        public void Spares_stay_under_the_budget()
        {
            var pool = new MeshLayerPool(1L << 20);
            var layers = new List<MeshLayer>();

            for (int i = 0; i < 40; i++)
            {
                var l = new MeshLayer();
                Build(l, pool, 2000, i);
                layers.Add(l);
            }

            foreach (MeshLayer l in layers)
            {
                l.SoftReset(pool, 256);
                pool.SpareBytes.Should().BeLessThanOrEqualTo(1L << 20);
            }

            pool.Dropped.Should().BeGreaterThan(0);
            pool.Clear();
            pool.SpareBytes.Should().Be(0);
            pool.SpareSets.Should().Be(0);
        }

        [Fact]
        public void A_fifo_chunk_pool_stays_bounded_where_growth_in_place_keeps_every_high_water_mark()
        {
            // 60 pooled chunks prefilled at 256 sprites, 20 in use; one build in 20 is a busy chunk (600-1500 statics: 1024 or
            // 2048 sprites), the rest 20-250
            long pooledEnd = Run(new MeshLayerPool(), out long pooledSpare);
            long plainEnd = Run(null, out _);

            const long perSprite = 240; // a little above a full set's managed bytes per sprite
            pooledEnd.Should().BeLessThan(plainEnd / 2);
            (pooledEnd - pooledSpare).Should().BeLessThan(80 * 256 * perSprite + 20 * 2048 * perSprite, "base sets plus at most every chunk in use busy");
            pooledSpare.Should().BeLessThanOrEqualTo(MeshLayerPool.DefaultSpareBudgetBytes);
        }

        private static long Run(MeshLayerPool pool, out long spare)
        {
            var rnd = new Random(77);
            var free = new Queue<MeshLayer>();
            var used = new List<MeshLayer>();

            for (int i = 0; i < 60; i++)
            {
                var l = new MeshLayer();
                l.EnsureCapacity(256);
                free.Enqueue(l);
            }

            for (int step = 0; step < 4000; step++)
            {
                MeshLayer l = free.Count > 0 ? free.Dequeue() : new MeshLayer();
                int count = rnd.Next(20) == 0 ? rnd.Next(600, 1500) : rnd.Next(20, 250);
                Build(l, pool, count, step);
                used.Add(l);

                if (used.Count > 20)
                {
                    int k = rnd.Next(used.Count);
                    MeshLayer gone = used[k];
                    used[k] = used[^1];
                    used.RemoveAt(used.Count - 1);

                    if (pool != null)
                    {
                        gone.SoftReset(pool, 256);
                    }
                    else
                    {
                        gone.SoftReset();
                    }

                    if (free.Count < 60)
                    {
                        free.Enqueue(gone);
                    }
                }
            }

            long total = 0;

            foreach (MeshLayer l in free)
            {
                total += l.HeldBytes;
            }

            foreach (MeshLayer l in used)
            {
                total += l.HeldBytes;
            }

            spare = pool?.SpareBytes ?? 0;

            return total + spare;
        }
    }
}
