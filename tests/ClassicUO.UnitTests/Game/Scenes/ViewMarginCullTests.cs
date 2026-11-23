using System;
using System.Runtime.CompilerServices;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// view_margin_cull (vm-legacy): the switch in both states, the margin test the Legacy walk leaves mesh sprites out with
    /// (an anchor inside the view is always drawn; one in the margin only when its quad meets the view), and the boxes
    /// CUO_VIEW_MARGIN_CHECK tests the left-out sprites with, against the quads ChunkMesh writes.
    /// </summary>
    public sealed class ViewMarginCullTests
    {
        private const float L = 0, T = 0, R = 800, B = 600;

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Switch_override_wins_in_both_states(bool on)
        {
            ViewMargin.CullOverrideForTests = on;

            try
            {
                Assert.Equal(on, ViewMargin.CullEnabled);
            }
            finally
            {
                ViewMargin.CullOverrideForTests = null;
            }
        }

        [Fact]
        public void Anchor_inside_the_view_is_never_culled()
        {
            // even a box that misses the view (no sprite has one, but the test must not depend on the box then)
            Assert.False(ViewMargin.MarginCulls(400, 300, -500, -500, -400, -400, L, T, R, B));
            Assert.False(ViewMargin.MarginCulls(0, 0, -100, -100, -50, -50, L, T, R, B));
            Assert.False(ViewMargin.MarginCulls(800, 600, 900, 700, 950, 750, L, T, R, B));
        }

        [Theory]
        // left of the view: a 44 px land tile cannot reach it, a wide static can
        [InlineData(-100, 300, -100, 300, -56, 344, true)]
        [InlineData(-100, 300, -200, 100, 10, 344, false)]
        // below the view: a short sprite is culled, a tall one reaching up into it is not
        [InlineData(400, 700, 400, 700, 444, 744, true)]
        [InlineData(400, 700, 380, 590, 444, 744, false)]
        // edges count as meeting
        [InlineData(-44, 300, -44, 300, 0, 344, false)]
        [InlineData(400, 601, 400, 601, 444, 645, true)]
        public void Margin_sprite_is_culled_only_when_its_box_misses_the_view(int ax, int ay, float x0, float y0, float x1, float y1, bool culled)
        {
            Assert.Equal(culled, ViewMargin.MarginCulls(ax, ay, x0, y0, x1, y1, L, T, R, B));
        }

        private static MeshLayer Layer()
        {
            var layer = new MeshLayer();
            layer.EnsureCapacity(2);
            layer.Count = 2;

            return layer;
        }

        private static Texture2D Tex()
        {
            var t = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
            GC.SuppressFinalize(t);

            return t;
        }

        [Theory]
        [InlineData(10, 20, 0, 44, 44)]
        [InlineData(10, 20, 15, 120, 300)]
        [InlineData(-3, 7, -40, 61, 87)]
        public void Static_box_matches_the_mesh_quad(int x, int y, int z, int w, int h)
        {
            // ChunkMesh.TryAddStaticLike's placement (world pixels), the walk's anchor at draw offset (offX, offY)
            int offX = 123, offY = 456;
            int baseX = (x - y) * 22 - 22, baseY = (x + y) * 22 - (z << 2) - 22;
            MeshLayer layer = Layer();
            layer.WriteQuadAt(1, Tex(), new Rectangle(0, 0, w, h), baseX - ((w >> 1) - 22), baseY - (h - 44), Vector3.Zero, 0f);
            layer.QuadBounds(1, out float qx0, out float qy0, out float qx1, out float qy1);

            ViewMargin.Anchor(x, y, z, offX, offY, out int ax, out int ay);
            ViewMargin.StaticBox(ax, ay, w, h, out int x0, out int y0, out int x1, out int y1);

            Assert.Equal(qx0 - offX, x0);
            Assert.Equal(qy0 - offY, y0);
            Assert.Equal(qx1 - offX, x1);
            Assert.Equal(qy1 - offY, y1);
        }

        [Theory]
        [InlineData(0, 0, 0, 0, 0)]
        [InlineData(10, -8, 12, 30, -20)]
        [InlineData(-60, 40, 0, 80, 4)]
        public void Land_box_matches_the_stretched_mesh_quad(int z, int top, int right, int bottom, int left)
        {
            // ChunkMesh.WriteStretchedLand's corners: posY is the Z-less position (baseY + Z * 4)
            int x = 100, y = 200, offX = 50, offY = -70;
            int baseX = (x - y) * 22 - 22, baseY = (x + y) * 22 - (z << 2) - 22, posY = baseY + (z << 2);
            MeshLayer layer = Layer();
            ref var v = ref layer.Vertices[0];
            v.Position0.X = baseX + 22;
            v.Position0.Y = posY - top;
            v.Position1.X = baseX + 44;
            v.Position1.Y = posY + (22 - right);
            v.Position2.X = baseX;
            v.Position2.Y = posY + (22 - left);
            v.Position3.X = baseX + 22;
            v.Position3.Y = posY + (44 - bottom);
            layer.QuadBounds(0, out float qx0, out float qy0, out float qx1, out float qy1);

            ViewMargin.Anchor(x, y, z, offX, offY, out int ax, out int ay);
            ViewMargin.LandBox(ax, ay, z, true, top, right, bottom, left, out int x0, out int y0, out int x1, out int y1);

            Assert.Equal(qx0 - offX, x0);
            Assert.Equal(qy0 - offY, y0);
            Assert.Equal(qx1 - offX, x1);
            Assert.Equal(qy1 - offY, y1);
        }

        [Fact]
        public void Flat_land_box_is_the_anchor_tile()
        {
            ViewMargin.LandBox(-30, 70, 9, false, 0, 0, 0, 0, out int x0, out int y0, out int x1, out int y1);
            Assert.Equal((-30, 70, 14, 114), (x0, y0, x1, y1));
        }
    }
}
