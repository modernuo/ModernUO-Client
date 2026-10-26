using ClassicUO.Game.Scenes;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    // E7: CUO_GPU_ITEM_CHECK compares the served frame's capture order (cached runs injected between walked objects) with the
    // reference walk's. The audit's counterexample: the reference order C1, W, C2 served as C1, C2, W must not hash the same.
    public class CaptureOrderHashTests
    {
        private static ulong Hash(params object[] order)
        {
            ulong h = 1469598103934665603UL;

            foreach (object o in order)
            {
                h = RenderLists.MixCaptureOrder(h, o);
            }

            return h;
        }

        [Fact]
        public void Misplaced_split_is_detected()
        {
            object c1 = new object(), w = new object(), c2 = new object();

            Hash(c1, c2, w).Should().NotBe(Hash(c1, w, c2));
            Hash(w, c1, c2).Should().NotBe(Hash(c1, w, c2));
        }

        [Fact]
        public void Same_order_hashes_the_same()
        {
            object c1 = new object(), w = new object(), c2 = new object();

            Hash(c1, w, c2).Should().Be(Hash(c1, w, c2));
        }

        [Fact]
        public void Missing_or_extra_object_is_detected()
        {
            object c1 = new object(), w = new object(), c2 = new object();

            Hash(c1, w).Should().NotBe(Hash(c1, w, c2));
            Hash(c1, w, c2, c2).Should().NotBe(Hash(c1, w, c2));
        }
    }
}
