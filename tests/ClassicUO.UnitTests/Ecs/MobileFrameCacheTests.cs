using System;
using ClassicUO.Ecs;
using ClassicUO.Game;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // W5: the mobile frame cache (gpu_mobile_frame_cache). The in-client checks (CUO_MOBILE_CACHE_CHECK, CUO_ECS_PARITY)
    // cover the replay against fresh selections and extracts; these cover the pure parts.
    public class MobileFrameCacheTests
    {
        [Fact]
        public void Replayed_Depth_Matches_The_Extracts_Expressions_Bit_For_Bit()
        {
            var rnd = new Random(1234);

            for (int n = 0; n < 200_000; n++)
            {
                // base depths as the walk produces them: tile sums plus fractional priorities
                float depth = (float)(rnd.NextDouble() * 4000.0 - 200.0);
                int i = rnd.Next(0, 64);

                Assert.Equal(BitConverter.SingleToInt32Bits(depth), BitConverter.SingleToInt32Bits(MobileSpriteExtract.DepthOf(depth, 0, 0f)));
                Assert.Equal(BitConverter.SingleToInt32Bits(depth + 1f), BitConverter.SingleToInt32Bits(MobileSpriteExtract.DepthOf(depth, 1, 0f)));
                Assert.Equal(BitConverter.SingleToInt32Bits(depth + 1f + i * 2), BitConverter.SingleToInt32Bits(MobileSpriteExtract.DepthOf(depth, 1, i * 2)));
            }
        }

        [Fact]
        public void SelectKey_Differs_On_Every_Input()
        {
            var a = new MobileFrameCache.SelectKey
            {
                Flags = AnimStateFlags.Human, NextFrameTime = 100, TileVersion = 3, EquipVersion = 7, Body = 400, Graphic = 400,
                X = 10, Y = 20, ServerGroup = 0xFF, Facing = 2, Frame = 1, Season = 0, Z = 5
            };

            Assert.True(a.Equals(a));

            void Differs(Func<MobileFrameCache.SelectKey, MobileFrameCache.SelectKey> change) => Assert.False(a.Equals(change(a)));

            Differs(k => { k.Flags |= AnimStateFlags.WarMode; return k; });
            Differs(k => { k.NextFrameTime++; return k; });
            Differs(k => { k.TileVersion++; return k; });
            Differs(k => { k.EquipVersion++; return k; });
            Differs(k => { k.Body++; return k; });
            Differs(k => { k.Graphic++; return k; });
            Differs(k => { k.X++; return k; });
            Differs(k => { k.Y++; return k; });
            Differs(k => { k.ServerGroup = 3; return k; });
            Differs(k => { k.Facing++; return k; });
            Differs(k => { k.Frame++; return k; });
            Differs(k => { k.Season++; return k; });
            Differs(k => { k.Z++; return k; });
        }

        [Fact]
        public void Invalidate_Drops_Both_Halves()
        {
            var c = new MobileFrameCache { SelectValid = true, EmitValid = true };
            c.Invalidate();
            Assert.False(c.SelectValid);
            Assert.False(c.EmitValid);
        }

        [Fact]
        public void EnsureCapacity_Keeps_Entries()
        {
            var c = new MobileFrameCache();
            c.Entries[3].Add = 6f;
            c.Entries[3].Kind = 1;
            c.EnsureCapacity(100);
            Assert.True(c.Entries.Length >= 100);
            Assert.Equal(6f, c.Entries[3].Add);
            Assert.Equal(1, c.Entries[3].Kind);
        }
    }
}
