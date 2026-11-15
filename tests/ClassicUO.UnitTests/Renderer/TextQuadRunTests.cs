using ClassicUO.Game;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    public class TextQuadRunTests
    {
        [Theory]
        [InlineData(1f, 13, 22, 7, 9)]
        [InlineData(1.5f, 14, 23, 10, 13)]
        public void Projection_preserves_separate_integer_rounding(float scale, int x, int y, int width, int height)
        {
            var run = new TextQuadRun { X = 5, Y = 6, Width = 7, Height = 9 };
            Assert.Equal(new Rectangle(x, y, width, height), run.Project(10, 20, scale, 2, 4));
        }

        [Fact]
        public void Glyph_clip_is_strict_and_tests_unscaled_bounds()
        {
            var run = new TextQuadRun { X = 5, Y = 6, Width = 7, Height = 9 };
            Assert.True(run.Visible(11, 14, 1, 1));
            Assert.False(run.Visible(12, 6, 10, 10));
            Assert.False(run.Visible(5, 15, 10, 10));
        }

        [Fact]
        public void Underline_uses_vertical_clip_only_and_one_pixel_scaled_height()
        {
            var run = new TextQuadRun { Y = 7, Width = 20, Underline = true };
            Assert.True(run.Visible(100, 7, 1, 1));
            Assert.False(run.Visible(0, 8, 20, 5));
            Assert.Equal(new Rectangle(7, 27, 30, 1), run.Project(10, 20, 1.5f, 2, 2));
        }

        [Fact]
        public void Layout_atlas_and_baked_style_changes_invalidate_the_key()
        {
            var key = new TextQuadCacheKey(new object(), new object(), 100, 1, true, false, FontStyle.None, 0xFFFF, 30);
            Assert.Equal(key, key with { });
            Assert.NotEqual(key, key with { Layout = new object() });
            Assert.NotEqual(key, key with { Atlas = new object() });
            Assert.NotEqual(key, key with { Width = 101 });
            Assert.NotEqual(key, key with { Font = 2 });
            Assert.NotEqual(key, key with { Unicode = false });
            Assert.NotEqual(key, key with { Html = true });
            Assert.NotEqual(key, key with { Style = FontStyle.BlackBorder });
            Assert.NotEqual(key, key with { Hue = 33 });
            Assert.NotEqual(key, key with { Cell = 31 });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Switch_defaults_off_and_can_be_enabled(bool on)
        {
            var settings = new ClassicUO.Configuration.Settings();
            Assert.False(settings.TextQuadCache);
            settings.TextQuadCache = on;
            Assert.Equal(on, settings.TextQuadCache);
            var key = new TextQuadCacheKey(new object(), new object(), 100, 1, true, false, FontStyle.None, 0xFFFF, 30);
            Assert.Equal(on, key.CanReuse(settings.TextQuadCache, true, key));
            Assert.False(key.CanReuse(settings.TextQuadCache, false, key));
            Assert.False((key with { Width = 101 }).CanReuse(settings.TextQuadCache, true, key));
        }
    }
}
