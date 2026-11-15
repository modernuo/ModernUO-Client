using ClassicUO.Renderer;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Renderer
{
    public class ScissorStackTests
    {
        private static readonly Rectangle Target = new Rectangle(0, 0, 640, 480);

        [Fact]
        public void Inside_IsUnchanged()
        {
            ScissorStack.ClipToBounds(new Rectangle(10, 20, 100, 50), Target, out var r).Should().BeTrue();
            r.Should().Be(new Rectangle(10, 20, 100, 50));
        }

        [Fact]
        public void NegativeOrigin_IsClampedToZero()
        {
            // the login gump centred in a window smaller than the gump: x and y negative, which FNA rejects
            ScissorStack.ClipToBounds(new Rectangle(-80, -30, 200, 100), Target, out var r).Should().BeTrue();
            r.Should().Be(new Rectangle(0, 0, 120, 70));
        }

        [Fact]
        public void LargerThanTarget_IsClampedToTarget()
        {
            ScissorStack.ClipToBounds(new Rectangle(-10, -10, 2000, 2000), Target, out var r).Should().BeTrue();
            r.Should().Be(Target);
        }

        [Fact]
        public void OverlappingRightAndBottom_IsCut()
        {
            ScissorStack.ClipToBounds(new Rectangle(600, 400, 100, 100), Target, out var r).Should().BeTrue();
            r.Should().Be(new Rectangle(600, 400, 40, 80));
        }

        [Theory]
        [InlineData(-300, 0, 200, 100)]   // entirely left
        [InlineData(0, -200, 100, 150)]   // entirely above
        [InlineData(640, 0, 10, 10)]      // touching the right edge only
        [InlineData(0, 480, 10, 10)]      // touching the bottom edge only
        [InlineData(10, 10, 0, 50)]       // empty
        [InlineData(10, 10, -5, 50)]      // negative size
        public void NothingVisible_ReturnsFalse(int x, int y, int w, int h)
        {
            ScissorStack.ClipToBounds(new Rectangle(x, y, w, h), Target, out var r).Should().BeFalse();
            r.Should().Be(Rectangle.Empty);
        }

        [Fact]
        public void EmptyTarget_ReturnsFalse()
        {
            // a 0x0 back buffer while the window has a transient size
            ScissorStack.ClipToBounds(new Rectangle(0, 0, 100, 100), new Rectangle(0, 0, 0, 0), out _).Should().BeFalse();
        }
    }
}
