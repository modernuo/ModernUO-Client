using System;
using System.Runtime.CompilerServices;
using ClassicUO.Renderer;
using FluentAssertions;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    /// <summary>
    /// mesh_vis_bits (permanent since quiet-1123): MeshLayer's packed visibility builds the visible indices and texture runs
    /// a plain walk of the visible sprites gives, frame after frame, across unchanged frames and rebuilds to fewer sprites.
    /// </summary>
    [Collection(nameof(MeshVisibilityCollection))]
    public sealed class MeshVisibilityTests
    {
        private static Texture2D[] Textures(int n)
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

        private static void Fill(MeshLayer layer, int count, Texture2D[] textures)
        {
            layer.Reset();
            layer.EnsureCapacity(count);
            layer.Count = count;

            // texture-sorted, as ChunkMesh writes them
            for (int i = 0; i < count; i++)
            {
                layer.Textures[i] = textures[i * textures.Length / count];
            }
        }

        private static void Frame(MeshLayer layer, bool[] visible, int count)
        {
            layer.ResetVisibility();

            for (int i = 0; i < count; i++)
            {
                if (visible[i])
                {
                    layer.SetVisible(i, 0xFF);
                }
            }

            layer.BuildVisibleIndices();
        }

        // the indices and texture runs of the visible sprites in sprite order
        private static void ShouldMatch(MeshLayer layer, bool[] visible, int count)
        {
            var indices = new System.Collections.Generic.List<short>();
            var runs = new System.Collections.Generic.List<(Texture2D Texture, int Start, int Count)>();
            int sprites = 0;

            for (int i = 0; i < count; i++)
            {
                if (!visible[i])
                {
                    continue;
                }

                Texture2D tex = layer.Textures[i];

                if (runs.Count == 0 || !ReferenceEquals(runs[^1].Texture, tex))
                {
                    runs.Add((tex, sprites, 0));
                }

                runs[^1] = (runs[^1].Texture, runs[^1].Start, runs[^1].Count + 1);
                short b = (short)(i * 4);
                indices.AddRange(new[] { b, (short)(b + 1), (short)(b + 2), (short)(b + 1), (short)(b + 3), (short)(b + 2) });
                sprites++;
            }

            layer.VisibleSpriteCount.Should().Be(sprites);
            layer.VisibleIndices.ToArray().Should().Equal(indices);
            layer.VisibleRunCount.Should().Be(runs.Count);

            for (int r = 0; r < runs.Count; r++)
            {
                layer.VisibleRuns[r].Texture.Should().BeSameAs(runs[r].Texture);
                layer.VisibleRuns[r].Start.Should().Be(runs[r].Start);
                layer.VisibleRuns[r].Count.Should().Be(runs[r].Count);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(63)]
        [InlineData(64)]
        [InlineData(65)]
        [InlineData(300)]
        public void Packed_builds_the_visible_sprites_frame_after_frame(int count)
        {
            var rnd = new Random(count);
            var textures = Textures(7);
            var layer = new MeshLayer();
            Fill(layer, count, textures);
            var visible = new bool[count];

            for (int frame = 0; frame < 40; frame++)
            {
                // some frames repeat the last pattern (the unchanged-visibility shortcut), some change it
                if (frame % 3 != 2)
                {
                    for (int i = 0; i < count; i++)
                    {
                        visible[i] = rnd.Next(frame % 4 == 0 ? 2 : 8) == 0;
                    }
                }

                Frame(layer, visible, count);
                ShouldMatch(layer, visible, count);
            }
        }

        [Fact]
        public void A_rebuild_to_fewer_sprites_leaves_no_stale_bits()
        {
            var textures = Textures(3);
            var layer = new MeshLayer();
            var all = new bool[200];
            Array.Fill(all, true);

            Fill(layer, 200, textures);
            Frame(layer, all, 200);

            Fill(layer, 70, textures);
            var some = new bool[70];
            some[3] = some[69] = true;
            Frame(layer, some, 70);

            ShouldMatch(layer, some, 70);
            layer.VisibleSpriteCount.Should().Be(2);
        }
    }

    /// <summary>MeshLayer tests that run one at a time.</summary>
    [CollectionDefinition(nameof(MeshVisibilityCollection), DisableParallelization = true)]
    public sealed class MeshVisibilityCollection { }
}
