using System;
using System.Runtime.CompilerServices;
using ClassicUO.Renderer;
using FluentAssertions;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    /// <summary>
    /// mesh_vis_bits: MeshLayer's packed visibility (ResetVisibility(true, ..)) builds the same visible indices and texture
    /// runs as the bool path, frame after frame, across unchanged frames, rebuilds to fewer sprites and mode switches.
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

        private static void Frame(MeshLayer layer, bool packed, bool[] visible, int count)
        {
            layer.ResetVisibility(packed, false);

            for (int i = 0; i < count; i++)
            {
                if (visible[i])
                {
                    layer.SetVisible(i, 0xFF);
                }
            }

            layer.BuildVisibleIndices();
        }

        private static void ShouldMatch(MeshLayer packed, MeshLayer plain)
        {
            packed.VisibleSpriteCount.Should().Be(plain.VisibleSpriteCount);
            packed.VisibleIndices.ToArray().Should().Equal(plain.VisibleIndices.ToArray());
            packed.VisibleRunCount.Should().Be(plain.VisibleRunCount);

            for (int r = 0; r < plain.VisibleRunCount; r++)
            {
                packed.VisibleRuns[r].Texture.Should().BeSameAs(plain.VisibleRuns[r].Texture);
                packed.VisibleRuns[r].Start.Should().Be(plain.VisibleRuns[r].Start);
                packed.VisibleRuns[r].Count.Should().Be(plain.VisibleRuns[r].Count);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(63)]
        [InlineData(64)]
        [InlineData(65)]
        [InlineData(300)]
        public void Packed_matches_the_bool_path_frame_after_frame(int count)
        {
            var rnd = new Random(count);
            var textures = Textures(7);
            var packed = new MeshLayer();
            var plain = new MeshLayer();
            Fill(packed, count, textures);
            Fill(plain, count, textures);
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

                Frame(packed, true, visible, count);
                Frame(plain, false, visible, count);
                ShouldMatch(packed, plain);
            }
        }

        [Fact]
        public void A_rebuild_to_fewer_sprites_leaves_no_stale_bits()
        {
            var textures = Textures(3);
            var packed = new MeshLayer();
            var plain = new MeshLayer();
            var all = new bool[200];
            Array.Fill(all, true);

            Fill(packed, 200, textures);
            Fill(plain, 200, textures);
            Frame(packed, true, all, 200);
            Frame(plain, false, all, 200);

            Fill(packed, 70, textures);
            Fill(plain, 70, textures);
            var some = new bool[70];
            some[3] = some[69] = true;
            Frame(packed, true, some, 70);
            Frame(plain, false, some, 70);

            ShouldMatch(packed, plain);
            packed.VisibleSpriteCount.Should().Be(2);
        }

        [Fact]
        public void Switching_modes_rebuilds_the_indices()
        {
            var textures = Textures(2);
            var layer = new MeshLayer();
            var reference = new MeshLayer();
            Fill(layer, 100, textures);
            Fill(reference, 100, textures);
            var a = new bool[100];
            var b = new bool[100];
            a[5] = a[50] = true;
            b[7] = b[99] = true;

            Frame(layer, false, a, 100);
            Frame(layer, true, b, 100);
            Frame(reference, false, b, 100);
            ShouldMatch(layer, reference);

            Frame(layer, false, a, 100);
            Frame(reference, false, a, 100);
            ShouldMatch(layer, reference);
        }

#if CUO_DEVTOOLS
        [Fact]
        public void The_checker_compares_every_packed_layer()
        {
            var textures = Textures(4);
            var layer = new MeshLayer();
            Fill(layer, 130, textures);
            var visible = new bool[130];
            visible[0] = visible[64] = visible[129] = true;
            long checks = MeshLayer.VisChecks, mismatches = MeshLayer.VisMismatches;

            for (int frame = 0; frame < 3; frame++)
            {
                layer.ResetVisibility(true, true);

                for (int i = 0; i < 130; i++)
                {
                    if (visible[i])
                    {
                        layer.SetVisible(i, 0xFF);
                    }
                }

                layer.BuildVisibleIndices();
            }

            (MeshLayer.VisChecks - checks).Should().Be(3);
            (MeshLayer.VisMismatches - mismatches).Should().Be(0);
        }
#endif
    }

    /// <summary>MeshLayer's checker totals are static.</summary>
    [CollectionDefinition(nameof(MeshVisibilityCollection), DisableParallelization = true)]
    public sealed class MeshVisibilityCollection { }
}
