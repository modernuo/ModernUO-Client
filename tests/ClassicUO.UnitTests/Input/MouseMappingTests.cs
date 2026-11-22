// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Input;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Input
{
    // Mouse.MapToUi: window coordinates to UI coordinates through the back buffer and the DPI scale. Sizes are the ones
    // logged on a 3840x2160 display at 150 % (taskbar 72 px, caption 34 px), borderless repro.
    public class MouseMappingTests
    {
        [Theory]
        [InlineData(1f)]
        [InlineData(1.5f)]
        [InlineData(2.25f)]
        public void Matching_back_buffer_divides_by_the_dpi_scale_only(float dpi)
        {
            Point p = Mouse.MapToUi(1500, 900, 3840, 2160, 3840, 2160, dpi);

            Assert.Equal(new Point((int)(1500 / dpi), (int)(900 / dpi)), p);
        }

        [Fact]
        public void Back_buffer_taller_than_the_window_follows_the_stretched_ui()
        {
            // borderless on a maximized window before the fix: back buffer 3840x2160 stretched over a 3840x2088 client area.
            // A point drawn at back buffer row 2000 shows at window row 2000 * 2088 / 2160 = 1933.3; the mouse there must
            // land on UI row 2000 / 1.5.
            Point p = Mouse.MapToUi(1000, 1934, 3840, 2160, 3840, 2088, 1.5f);

            Assert.Equal((int)(1000 / 1.5), p.X);
            Assert.InRange(p.Y, (int)(2000 / 1.5f) - 1, (int)(2000 / 1.5f) + 1);
        }

        [Fact]
        public void Back_buffer_smaller_than_the_window_does_not_collapse_to_zero()
        {
            Point p = Mouse.MapToUi(1200, 1000, 1919, 1079, 1920, 1080, 1f);

            Assert.InRange(p.X, 1198, 1200);
            Assert.InRange(p.Y, 998, 1000);
        }

        [Fact]
        public void Zero_sizes_fall_back_to_the_unscaled_position()
        {
            Assert.Equal(new Point(10, 20), Mouse.MapToUi(10, 20, 0, 0, 0, 0, 0f));
            Assert.Equal(new Point(10, 20), Mouse.MapToUi(10, 20, 800, 600, 0, 0, 1f));
        }

        [Fact]
        public void Window_corners_map_to_the_ui_target_corners()
        {
            // the UI target is the back buffer divided by the DPI scale (RenderTargets.EnsureSizes)
            Point end = Mouse.MapToUi(3839, 2087, 3840, 2088, 3840, 2088, 1.5f);

            Assert.Equal(new Point(2559, 1391), end);
            Assert.Equal(Point.Zero, Mouse.MapToUi(0, 0, 3840, 2088, 3840, 2088, 1.5f));
        }
    }
}
