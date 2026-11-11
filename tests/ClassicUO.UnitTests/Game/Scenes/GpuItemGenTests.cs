using System;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Game.Scenes;
using ClassicUO.UnitTests.Ecs;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Game.Scenes
{
    // pa-gpusig (gpu_prepare_item_cache_gen): the Gpu item cache trusts a served tile while its chunk's render generation is
    // the one it was built at, the tile was served in every pass since its last check and the range bits hold; with the
    // switch off every served tile compares its signatures, as before. The writers the signature reads bump the generation.
    [Collection(nameof(GpuItemGenTests))]
    public sealed class GpuItemGenTests : IDisposable
    {
        private const uint ITEM = 0x4000_7B00;

        public GpuItemGenTests()
        {
            Reset();
        }

        public void Dispose()
        {
            Reset();
        }

        private static void Reset()
        {
#if CUO_DEVTOOLS
            GpuItemGen.ResetForTests();
#else
            GpuItemGen.OverrideForTests = null;
#endif
        }

        // a cache built at the chunk's generation, two entries on tile 9 at (1001,1000) and (1003,1000), in range of (1000,1000)
        private static (Chunk, GpuItemCache) Built(World world, ulong rangeKey)
        {
            var chunk = new Chunk(world) { GpuItemsDirty = false, GpuItemRenderGen = 7 };
            var cache = new GpuItemCache { RenderGen = 7, RangeKey = rangeKey, Count = 2 };

            for (int b = 0; b <= 64; b++)
            {
                cache.TileStart[b] = b <= 9 ? 0 : 2;
            }

            cache.Entries[0].Sig1 = 1001UL | 1000UL << 16;
            cache.Entries[1].Sig1 = 1003UL | 1000UL << 16;
            cache.Tiles = 1UL << 9;

            return (chunk, cache);
        }

        private static ulong Key(int x, int y, int range) => (ushort)x | (ulong)(ushort)y << 16 | (ulong)(uint)range << 32;

        [Fact]
        public void Switch_Follows_Its_Override()
        {
            GpuItemGen.OverrideForTests = true;
            GpuItemGen.Enabled.Should().BeTrue();

            GpuItemGen.OverrideForTests = false;
            GpuItemGen.Enabled.Should().BeFalse();
        }

        [Fact]
        public void Switch_Off_Always_Compares_Signatures()
        {
            var world = new World();
            (Chunk chunk, GpuItemCache cache) = Built(world, Key(1000, 1000, 18));

            GpuItemGen.Decide(false, chunk, cache, 9, true, Key(1000, 1000, 18)).Should().Be(GpuItemGen.TileCheck.Signatures);

            chunk.GpuItemRenderGen++;
            GpuItemGen.Decide(false, chunk, cache, 9, true, Key(1000, 1000, 18)).Should().Be(GpuItemGen.TileCheck.Signatures);
        }

        [Fact]
        public void Switch_On_Trusts_An_Unchanged_Tile()
        {
            var world = new World();
            (Chunk chunk, GpuItemCache cache) = Built(world, Key(1000, 1000, 18));

            GpuItemGen.Decide(true, chunk, cache, 9, true, Key(1000, 1000, 18)).Should().Be(GpuItemGen.TileCheck.Trust);
            GpuItemGen.Decide(true, chunk, cache, 9, false, 0).Should().Be(GpuItemGen.TileCheck.Trust);
        }

        [Fact]
        public void Switch_On_Falls_Back_After_A_Change_Since_The_Build()
        {
            var world = new World();
            (Chunk chunk, GpuItemCache cache) = Built(world, 0);

            chunk.GpuItemRenderGen++;
            GpuItemGen.Decide(true, chunk, cache, 9, false, 0).Should().Be(GpuItemGen.TileCheck.Fallback);

        }

        [Fact]
        public void Switch_On_Compares_Signatures_In_A_Chunk_Marked_Since_The_Prepare()
        {
            var world = new World();
            (Chunk chunk, GpuItemCache cache) = Built(world, 0);

            // a join, leave or another tile's fallback after the prepare
            chunk.GpuItemsDirty = true;
            GpuItemGen.Decide(true, chunk, cache, 9, false, 0).Should().Be(GpuItemGen.TileCheck.Signatures);
        }

        [Fact]
        public void Switch_On_Polls_A_Tile_The_Walk_Had()
        {
            var world = new World();
            (Chunk chunk, GpuItemCache cache) = Built(world, 0);
            cache.PollTiles = 1UL << 9;

            GpuItemGen.Decide(true, chunk, cache, 9, false, 0).Should().Be(GpuItemGen.TileCheck.Signatures);
            GpuItemGen.Decide(true, chunk, cache, 10, false, 0).Should().Be(GpuItemGen.TileCheck.Trust);
        }

        [Theory]
        [InlineData(1000, 1000, 18, false)] // unchanged key
        [InlineData(1001, 1000, 18, false)] // moved, both still in range
        [InlineData(984, 1000, 18, true)]   // the far entry (1003) leaves the range, 1001 stays
        [InlineData(1000, 1000, 2, true)]   // the view range shrinks below 3
        [InlineData(1000, 1000, 3, false)]  // exactly in range
        public void Switch_On_Range_Bits_Follow_The_Player(int px, int py, int range, bool fallback)
        {
            var world = new World();
            (Chunk chunk, GpuItemCache cache) = Built(world, Key(1000, 1000, 18));

            GpuItemGen.Decide(true, chunk, cache, 9, true, Key(px, py, range))
                      .Should().Be(fallback ? GpuItemGen.TileCheck.Fallback : GpuItemGen.TileCheck.Trust);
        }

        [Fact]
        public void Out_Of_Range_Matches_Distance()
        {
            var world = new World();
            world.RangeSize = new Point(1000, 1000);
            world.ClientViewRange = 5;

            foreach ((int x, int y) in new[] { (1000, 1000), (1005, 1000), (1006, 1000), (995, 1003), (994, 1000), (1000, 1006) })
            {
                ulong sig1 = (ulong)x | (ulong)y << 16;
                int distance = Math.Max(Math.Abs(x - world.RangeSize.X), Math.Abs(y - world.RangeSize.Y));
                GpuItemGen.OutOfRange(sig1, Key(1000, 1000, 5)).Should().Be(distance > world.ClientViewRange, $"({x},{y})");
            }

            // no player: Distance is ushort.MaxValue, out of any range
            GpuItemGen.RangeKey(world).Should().Be(ulong.MaxValue);
            GpuItemGen.OutOfRange(1000UL | 1000UL << 16, ulong.MaxValue).Should().BeTrue();
        }

        [Fact]
        public void Touch_Bumps_The_Items_Chunk_And_Nothing_Off_The_Map()
        {
            var world = new World();
            var chunk = new Chunk(world);
            Item item = world.GetOrCreateItem(ITEM);
            uint before = chunk.GpuItemRenderGen;

            GpuItemGen.Touch(item);
            chunk.GpuItemRenderGen.Should().Be(before);

            item.TileChunk = chunk;
            GpuItemGen.Touch(item);
            chunk.GpuItemRenderGen.Should().Be(before + 1);
            item.TileChunk = null;
        }

        [Fact]
        public void An_Item_Commit_Bumps_Its_Chunk()
        {
            World world = EcsCheckV2Tests.BuildFunnelWorld();
            Item item = world.Writer.UpsertWorldItem(ITEM, 0x0F3F, 0, 1, 1005, 1000, 0, Direction.North, 0, Flags.None, 3, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);

            var chunk = new Chunk(world);
            Chunk saved = item.TileChunk;
            item.TileChunk = chunk;
            uint before = chunk.GpuItemRenderGen;

            // 0x1A again with another hue, not placed (the placement is a content change of its own)
            world.Writer.UpsertWorldItem(ITEM, 0x0F3F, 0, 1, 1005, 1000, 0, Direction.North, 0x21, Flags.None, 3, out _, out _);

            chunk.GpuItemRenderGen.Should().NotBe(before);
            item.TileChunk = saved;
        }

        [Fact]
        public void A_Boat_Offset_Appearing_Or_Clearing_Bumps_The_Chunk()
        {
            World world = EcsCheckV2Tests.BuildFunnelWorld();
            Item item = world.Writer.UpsertWorldItem(ITEM, 0x0F3F, 0, 1, 1005, 1000, 0, Direction.North, 0, Flags.None, 3, out _, out WorldWriter.Placement p);
            world.Writer.PlaceWorldItem(p);

            var chunk = new Chunk(world);
            Chunk saved = item.TileChunk;
            item.TileChunk = chunk;

            uint g0 = chunk.GpuItemRenderGen;
            ItemOffsets.Push(world, item, new Vector3(1, 2, 0));
            uint g1 = chunk.GpuItemRenderGen;
            ItemOffsets.Push(world, item, Vector3.Zero);
            uint g2 = chunk.GpuItemRenderGen;

            g1.Should().NotBe(g0);
            g2.Should().NotBe(g1);
            item.TileChunk = saved;
        }

        [Fact]
        public void Describe_Diff_Names_The_Changed_Fields()
        {
            ulong a = 0x0F3FUL | 0x21UL << 16 | 0xFFUL << 56;
            ulong b = 1000UL | 1000UL << 16;

            GpuItemGen.DescribeDiff(a, b, a ^ 0x1UL << 16, b | GpuItemGen.RANGE_BIT).Should().Be("hue 0x21->0x20, outOfRange 0x0->0x1");
            GpuItemGen.DescribeDiff(a, b, a, b).Should().BeEmpty();
        }

#if CUO_DEVTOOLS
        [Fact]
        public void Checker_Counts_Every_Mismatch()
        {
            var world = new World();
            var chunk = new Chunk(world);
            Item item = world.GetOrCreateItem(ITEM);
            long before = GpuItemGen.MismatchesTotal;

            GpuItemGen.Mismatch(item, chunk, 9, 1, 2, 1, 3, false);
            GpuItemGen.Mismatch(item, chunk, 9, 1, 2, 1, 2, true);

            GpuItemGen.MismatchesTotal.Should().Be(before + 2);
        }

        [Fact]
        public void Touch_Counts_Invalidations_For_The_Summary()
        {
            var world = new World();
            var chunk = new Chunk(world);
            Item item = world.GetOrCreateItem(ITEM);
            item.TileChunk = chunk;
            long before = GpuItemGen.Invalidations;

            GpuItemGen.Touch(item);

            GpuItemGen.Invalidations.Should().Be(before + 1);
            item.TileChunk = null;
        }
#endif
    }
}
