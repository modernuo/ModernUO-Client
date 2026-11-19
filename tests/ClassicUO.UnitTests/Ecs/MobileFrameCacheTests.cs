using System;
using ClassicUO.Ecs;
using ClassicUO.Game;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // W5: the mobile frame cache. The in-client check (CUO_MOBILE_CACHE_CHECK) covers the replay against fresh selections
    // and extracts; these cover the pure parts.
    [Collection("CachePool")]
    public class MobileFrameCacheTests
    {
        // The pools are process-wide statics that other test classes also rent from, so nothing here asserts that Rent hands
        // back a particular instance: the reset is checked on the instance itself, and the pool-full test runs alone.
        [Fact]
        public void Reset_Clears_A_Used_Cache()
        {
            var c = new MobileFrameCache();
            c.SelectValid = true;
            c.EmitValid = true;
            c.AbsValid = true;
            c.LegacyValid = true;
            c.EnsureCapacity(20);
            c.Count = 5;
            c.LightCount = 1;
            c.EnsureLegacyCapacity(8);
            c.LegacyCount = 3;
            c.LegacyLightCount = 2;
            c.LegacyEntries[1].Kind = 4;
            c.Reset();

            Assert.False(c.SelectValid);
            Assert.False(c.EmitValid);
            Assert.False(c.AbsValid);
            Assert.False(c.LegacyValid);
            Assert.Equal(0, c.Count);
            Assert.Equal(0, c.LightCount);
            Assert.Equal(0, c.LegacyCount);
            Assert.Equal(0, c.LegacyLightCount);
            Assert.Equal(0, c.LegacyEntries[1].Kind);
            Assert.True(c.Entries.Length >= 20);
        }

        [Fact]
        public void Reset_Clears_A_Used_Worn_Cache()
        {
            var c = new WornCache();
            c.Serial = 5;
            c.Epoch = 3;
            c.Version = 9;
            c.Writes = 4;
            c.Held = WornHeld.Record;
            c.Items = new ClassicUO.Game.GameObjects.Item[3];
            c.Reset();

            Assert.Equal(0u, c.Serial);
            Assert.Equal(0u, c.Epoch);
            Assert.Equal(WornHeld.Unknown, c.Held);
            Assert.Equal(0UL, c.Version);
            Assert.Equal(0L, c.Writes);
            Assert.Null(c.Layers);
            Assert.Equal(3, c.Items.Length);
            Assert.All(c.Items, i => Assert.Null(i));
        }

        [Fact]
        public void Return_Drops_Caches_Past_The_Pool_Cap()
        {
            var frames = new System.Collections.Generic.List<MobileFrameCache>();
            var worn = new System.Collections.Generic.List<WornCache>();

            // empty both pools (nothing else runs in parallel with this collection)
            for (int i = 0; i < 64; i++)
            {
                frames.Add(MobileFrameCache.Rent());
                worn.Add(WornCache.Rent());
            }

            int framesKept = 0, wornKept = 0;

            foreach (MobileFrameCache c in frames)
            {
                framesKept += MobileFrameCache.Return(c) ? 1 : 0;
            }

            foreach (WornCache c in worn)
            {
                wornKept += WornCache.Return(c) ? 1 : 0;
            }

            Assert.Equal(32, framesKept);
            Assert.Equal(32, wornKept);

            // a kept cache comes back reset and is one of those returned
            MobileFrameCache again = MobileFrameCache.Rent();
            Assert.Contains(again, frames);
            Assert.False(again.SelectValid);
            WornCache wAgain = WornCache.Rent();
            Assert.Contains(wAgain, worn);
            Assert.Equal(0u, wAgain.Serial);
        }

#if CUO_DEVTOOLS
        [Fact]
        public void Double_Return_Is_Refused_And_Counted()
        {
            long frameBefore = MobileFrameCache.DoubleReturns;
            long wornBefore = WornCache.DoubleReturns;
            var c = new MobileFrameCache();
            var w = new WornCache();

            Assert.True(MobileFrameCache.Return(c));
            Assert.False(MobileFrameCache.Return(c));
            Assert.True(WornCache.Return(w));
            Assert.False(WornCache.Return(w));
            Assert.Equal(frameBefore + 1, MobileFrameCache.DoubleReturns);
            Assert.Equal(wornBefore + 1, WornCache.DoubleReturns);
        }
#endif

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
