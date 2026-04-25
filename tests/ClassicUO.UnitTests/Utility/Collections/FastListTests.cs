using ClassicUO.Utility.Collections;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Utility.Collections
{
    public class FastListTests
    {
        [Fact]
        public void DefaultCapacity_IsFive()
        {
            var list = new FastList<int>();

            list.Length.Should().Be(0);
            list.Buffer.Length.Should().Be(5);
        }

        [Fact]
        public void Add_GrowsBufferWhenAtCapacity()
        {
            var list = new FastList<int>();
            for (int i = 0; i < 5; i++)
            {
                list.Add(i);
            }

            // After exactly capacity items: Length == Buffer.Length, no growth yet.
            list.Length.Should().Be(5);
            list.Buffer.Length.Should().Be(5);

            list.Add(5);

            // Add #6 trips the resize.
            list.Length.Should().Be(6);
            list.Buffer.Length.Should().Be(10);
            list.Buffer[5].Should().Be(5);
        }

        [Fact]
        public void Resize_BelowCapacity_DoesNotGrowBuffer()
        {
            var list = new FastList<int>();
            list.Add(1);
            list.Add(2);
            list.Add(3);

            list.Resize(2);

            list.Length.Should().Be(2);
            list.Buffer.Length.Should().Be(5);
            list.Buffer[0].Should().Be(1);
            list.Buffer[1].Should().Be(2);
        }

        [Fact]
        public void Resize_EqualToBufferLength_DoesNotGrowBuffer()
        {
            var list = new FastList<int>();

            list.Resize(5);

            list.Length.Should().Be(5);
            list.Buffer.Length.Should().Be(5);
        }

        [Fact]
        public void Resize_OnePastBufferLength_GrowsBuffer()
        {
            // Regression for FontsLoader bug: GetInfoASCII would set
            // Data.Length = CharCount where CharCount could land one past
            // the current Buffer.Length boundary (5, 10, 20, ...) when a
            // long token wrapped and `countspaces` bumped CharCount past
            // the natural Add-grown buffer size. Direct field assignment
            // would leave Length > Buffer.Length and the rendering loop
            // would IndexOutOfRange.
            var list = new FastList<int>();
            for (int i = 0; i < 5; i++)
            {
                list.Add(i);
            }

            list.Length.Should().Be(5);
            list.Buffer.Length.Should().Be(5);

            list.Resize(6);

            list.Length.Should().Be(6);
            list.Buffer.Length.Should().BeGreaterThanOrEqualTo(6);
            list.Length.Should().BeLessThanOrEqualTo(list.Buffer.Length);
        }

        [Fact]
        public void Resize_GrowsByDoubling()
        {
            var list = new FastList<int>();
            for (int i = 0; i < 5; i++)
            {
                list.Add(i);
            }

            list.Resize(6);

            // Math.Max(Buffer.Length << 1, newLength) = Math.Max(10, 6) = 10
            list.Buffer.Length.Should().Be(10);
        }

        [Fact]
        public void Resize_GrowsToNewLengthWhenLargerThanDoubled()
        {
            var list = new FastList<int>();

            list.Resize(64);

            // Math.Max(5 << 1, 64) = Math.Max(10, 64) = 64
            list.Length.Should().Be(64);
            list.Buffer.Length.Should().BeGreaterThanOrEqualTo(64);
        }

        [Fact]
        public void Resize_PreservesExistingItemsBelowNewLength()
        {
            var list = new FastList<int>();
            for (int i = 0; i < 5; i++)
            {
                list.Add(100 + i);
            }

            list.Resize(8);

            list.Buffer[0].Should().Be(100);
            list.Buffer[1].Should().Be(101);
            list.Buffer[2].Should().Be(102);
            list.Buffer[3].Should().Be(103);
            list.Buffer[4].Should().Be(104);
        }

        [Fact]
        public void Resize_GrownSlotsAreDefault()
        {
            var list = new FastList<int>();
            list.Add(42);

            list.Resize(8);

            for (int i = 1; i < list.Length; i++)
            {
                list.Buffer[i].Should().Be(0);
            }
        }

        [Fact]
        public void Resize_AcrossMultipleBufferBoundaries_AlwaysHoldsInvariant()
        {
            // Walks the buffer-doubling boundaries (5 → 10 → 20 → 40 → 80 → 160)
            // and verifies that Resize crossing each boundary keeps the
            // FastList invariant: Length <= Buffer.Length.
            var list = new FastList<int>();
            int[] boundaries = { 5, 10, 20, 40, 80, 160, 320 };

            foreach (int boundary in boundaries)
            {
                while (list.Length < boundary)
                {
                    list.Add(list.Length);
                }

                list.Resize(boundary + 1);

                list.Length.Should().Be(boundary + 1);
                list.Length.Should().BeLessThanOrEqualTo(
                    list.Buffer.Length,
                    "Resize across boundary {0} must keep Length <= Buffer.Length",
                    boundary
                );
            }
        }

        [Fact]
        public void Resize_ToZero_ClearsLengthOnly()
        {
            var list = new FastList<int>();
            list.Add(1);
            list.Add(2);
            list.Add(3);

            list.Resize(0);

            list.Length.Should().Be(0);
            list.Buffer.Length.Should().Be(5);
            // Buffer contents are not zeroed by Resize-down (matches Reset semantics).
            list.Buffer[0].Should().Be(1);
        }
    }
}
