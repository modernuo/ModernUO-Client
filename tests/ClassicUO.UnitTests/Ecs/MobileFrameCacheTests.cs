using System;
using ClassicUO.Ecs;
using ClassicUO.Game;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // W5: the mobile frame cache. The in-client check (CUO_MOBILE_CACHE_CHECK) covers the replay against fresh selections
    // and extracts; these cover the pure parts.
    public class MobileFrameCacheTests
    {
        [Fact]
        public void Returned_Cache_Comes_Back_Reset()
        {
            MobileFrameCache c = MobileFrameCache.Rent();
            c.SelectValid = true;
            c.EmitValid = true;
            c.AbsValid = true;
            c.LegacyValid = true;
            c.EnsureCapacity(20);
            c.Count = 5;
            c.LightCount = 1;
            c.EnsureLegacyCapacity(8);
            c.LegacyCount = 3;
            MobileFrameCache.Return(c);

            MobileFrameCache again = MobileFrameCache.Rent();
            Assert.Same(c, again);
            Assert.False(again.SelectValid);
            Assert.False(again.EmitValid);
            Assert.False(again.AbsValid);
            Assert.False(again.LegacyValid);
            Assert.Equal(0, again.Count);
            Assert.Equal(0, again.LightCount);
            Assert.Equal(0, again.LegacyCount);
            Assert.True(again.Entries.Length >= 20);
        }

        [Fact]
        public void Returned_Worn_Cache_Comes_Back_Reset()
        {
            WornCache c = WornCache.Rent();
            c.Serial = 5;
            c.Epoch = 3;
            c.Version = 9;
            c.Writes = 4;
            c.Held = WornHeld.Record;
            c.Items = new ClassicUO.Game.GameObjects.Item[3];
            WornCache.Return(c);

            WornCache again = WornCache.Rent();
            Assert.Same(c, again);
            Assert.Equal(0u, again.Serial);
            Assert.Equal(WornHeld.Unknown, again.Held);
            Assert.Equal(0UL, again.Version);
            Assert.All(again.Items, i => Assert.Null(i));
        }

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
