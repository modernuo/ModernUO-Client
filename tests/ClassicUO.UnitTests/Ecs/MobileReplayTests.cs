using System;
using ClassicUO.Game;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // E5: the absolute replay buffer (MobileFrameCache.Abs) and the extract's view of it.
    public class MobileReplayTests
    {
        private static MobileFrameCache MakeCache()
        {
            var c = new MobileFrameCache();
            c.EnsureCapacity(3);
            c.Count = 3;

            for (int i = 0; i < 3; i++)
            {
                c.Entries[i].Instance = new SpriteInstance { X = 7 + i, Y = -5 * i, Whf = (uint)(10 + i), Uv = 1, PageHue = 2 };
                c.Entries[i].Kind = (byte)(i == 0 ? 0 : 1);
                c.Entries[i].Add = i == 2 ? 2f : 0f;
            }

            return c;
        }

        private static SpriteInstance[] Run(MobileFrameCache c, int dx, int dy, float depth, int objX)
        {
            var x = new MobileSpriteExtract(null);
            x.Begin(null, 0, 0);
            x.Replay(c, dx, dy, depth, objX, dx, dy, 0);

            return x.Instances.ToArray();
        }

        private static void AssertSame(SpriteInstance[] a, SpriteInstance[] b)
        {
            Assert.Equal(a.Length, b.Length);

            for (int i = 0; i < a.Length; i++)
            {
                Assert.True(a[i].X == b[i].X && a[i].Y == b[i].Y && a[i].Depth == b[i].Depth && a[i].Whf == b[i].Whf, $"instance {i}");
            }
        }

        [Fact]
        public void Shifted_Replay_Equals_A_Fresh_Rebuild_And_Same_Tag_Is_A_No_Op()
        {
            var warm = MakeCache();
            Run(warm, 100, 50, 12.5f, 40);

            // same tag: same output, Abs untouched
            AssertSame(Run(warm, 100, 50, 12.5f, 40), Run(MakeCache(), 100, 50, 12.5f, 40));

            // other dx/dy, same depth and tile x: shift path
            var shifted = Run(warm, 93, 61, 12.5f, 40);
            AssertSame(shifted, Run(MakeCache(), 93, 61, 12.5f, 40));
            Assert.Equal(93, warm.AbsDx);
            Assert.Equal(61, warm.AbsDy);
        }

        [Fact]
        public void Depth_Or_TileX_Change_Rebuilds()
        {
            var warm = MakeCache();
            Run(warm, 100, 50, 12.5f, 40);

            AssertSame(Run(warm, 100, 50, 13.5f, 40), Run(MakeCache(), 100, 50, 13.5f, 40));
            AssertSame(Run(warm, 100, 50, 13.5f, 41), Run(MakeCache(), 100, 50, 13.5f, 41));
        }

        [Fact]
        public void Replaying_The_Same_Cache_Twice_In_One_Begin_Keeps_The_First_Emission()
        {
            var c = MakeCache();
            var first = Run(MakeCache(), 100, 0, 5f, 1);

            // second replay at another position within the same Begin: the first emission must survive
            var xs = new MobileSpriteExtract(null);
            xs.Begin(null, 0, 0);
            xs.Replay(c, 100, 0, 5f, 1, 100, 0, 0);
            xs.Replay(c, 120, 0, 5f, 1, 120, 0, 0);
            SpriteInstance[] both = xs.Instances.ToArray();

            Assert.Equal(6, both.Length);
            AssertSame(first, both[..3]);
            AssertSame(Run(MakeCache(), 120, 0, 5f, 1), both[3..]);
        }
    }
}
