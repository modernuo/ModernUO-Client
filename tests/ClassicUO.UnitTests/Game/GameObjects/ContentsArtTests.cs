using System;
using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using FluentAssertions;
using Xunit;
using Flags = ClassicUO.Game.Data.Flags;

namespace ClassicUO.UnitTests.Game.GameObjects
{
    // perf-4 (art_container_prefetch): an item going into an open container requests its displayed art
    // (WorldWriter.PlaceInContainer -> ContentsArt.RequestContained), and a container window about to build its item
    // controls prefetches its contents' distinct art in one batch (ContentsArt.PrefetchContents). The observers see both
    // without a game; the worker decode, its dedup by sprite state and the bounded wait are Art's and FirstSightDecoder's.
    public sealed class ContentsArtTests : IDisposable
    {
        private const uint BAG = 0x4000_9001, ITEM = 0x4000_9002, OTHER = 0x4000_9003, THIRD = 0x4000_9004;

        private readonly List<ushort> _requests = new List<ushort>();
        private readonly List<uint[]> _batches = new List<uint[]>();

        public ContentsArtTests()
        {
            ContentsArt.OverrideForTests = true;
            ContentsArt.RequestedForTests = g => _requests.Add(g);
            ContentsArt.PrefetchedForTests = ids => _batches.Add(ids.ToArray());
        }

        public void Dispose()
        {
            ContentsArt.OverrideForTests = null;
            ContentsArt.RequestedForTests = null;
            ContentsArt.PrefetchedForTests = null;
        }

        private static Item Bag(World world, bool opened)
        {
            Item bag = world.Writer.UpsertWorldItem(BAG, 0x0E75, 0, 1, 1000, 1000, 0, Direction.North, 0, Flags.None, 0, out _,
                out var placement);
            world.Writer.PlaceWorldItem(placement);
            bag.Opened = opened;

            return bag;
        }

        [Fact]
        public void Item_Added_To_An_Open_Container_Requests_Its_Displayed_Graphic()
        {
            var world = new World();
            Bag(world, opened: true);

            world.Writer.PlaceInContainer(ITEM, 0x1515, 1, 10, 10, 0, BAG, out _);
            world.Writer.PlaceInContainer(OTHER, 0x0EED, 3, 20, 20, 0, BAG, out _);

            _requests.Should().Equal(0x1515, 0x0EEE);
        }

        [Fact]
        public void Item_Added_To_A_Closed_Container_Requests_Nothing()
        {
            var world = new World();
            Bag(world, opened: false);

            world.Writer.PlaceInContainer(ITEM, 0x1515, 1, 10, 10, 0, BAG, out _);

            _requests.Should().BeEmpty("nothing draws a closed container's contents");
        }

        [Fact]
        public void Item_With_No_Amount_Requests_Nothing()
        {
            var world = new World();
            Bag(world, opened: true);

            world.Writer.PlaceInContainer(ITEM, 0x1515, 0, 10, 10, 0, BAG, out _);

            _requests.Should().BeEmpty("ContainerGump skips items with no amount");
        }

        [Fact]
        public void Open_Prefetches_The_Distinct_Displayed_Graphics_Of_The_Contents()
        {
            var world = new World();
            Item bag = Bag(world, opened: false);
            world.Writer.PlaceInContainer(ITEM, 0x1515, 1, 10, 10, 0, BAG, out _);
            world.Writer.PlaceInContainer(OTHER, 0x1515, 1, 30, 30, 0, BAG, out _);
            world.Writer.PlaceInContainer(THIRD, 0x0EED, 50, 20, 20, 0, BAG, out _);

            ContentsArt.PrefetchContents(world, bag);

            _batches.Should().ContainSingle().Which.Should().Equal(0x1515u, 0x0EEFu);
        }

        [Fact]
        public void Prefetch_Needs_Two_Distinct_Sprites()
        {
            var world = new World();
            Item bag = Bag(world, opened: true);
            world.Writer.PlaceInContainer(ITEM, 0x1515, 1, 10, 10, 0, BAG, out _);
            world.Writer.PlaceInContainer(OTHER, 0x1515, 1, 30, 30, 0, BAG, out _);

            ContentsArt.PrefetchContents(world, bag);
            ContentsArt.Prefetch(new uint[] { 0x1515, 0x1515, 0x1515 });

            _batches.Should().BeEmpty("one sprite has nothing to overlap");
        }

        [Fact]
        public void Prefetch_Of_Slot_Graphics_Deduplicates()
        {
            ContentsArt.Prefetch(new uint[] { 0x1F03, 0x13BB, 0x1F03, 0x1086 });

            _batches.Should().ContainSingle().Which.Should().Equal(0x1F03u, 0x13BBu, 0x1086u);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(null)] // the setting, default off
        public void Switch_Off_Requests_And_Prefetches_Nothing(bool? setting)
        {
            ContentsArt.OverrideForTests = setting;

            if (setting == null && ContentsArt.Enabled)
            {
                return; // CUO_ART_CONTAINER_PREFETCH=1 in this environment
            }

            var world = new World();
            Item bag = Bag(world, opened: true);
            world.Writer.PlaceInContainer(ITEM, 0x1515, 1, 10, 10, 0, BAG, out _);
            world.Writer.PlaceInContainer(OTHER, 0x1516, 1, 20, 20, 0, BAG, out _);

            ContentsArt.PrefetchContents(world, bag);
            ContentsArt.Prefetch(new uint[] { 0x1F03, 0x13BB });

            _requests.Should().BeEmpty();
            _batches.Should().BeEmpty();
        }
    }
}
