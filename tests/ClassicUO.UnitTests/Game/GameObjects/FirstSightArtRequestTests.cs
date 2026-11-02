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
    // perf-3: 0x1A/0xF3 request the art of a ground item when WorldWriter places it (PlaceWorldItem -> SetInWorldTile ->
    // AddToTile -> Item.RequestFirstSightArt -> FirstSightArt.RequestPlaced). These pin which placements request what; the
    // dedup of repeated requests is the decoder's (FirstSightDecoderTests), and the off switch is CUO_ART_SYNC=1 (Map.ArtWait).
    // Multis (type 2) need the UO multi data and are not covered here.
    public sealed class FirstSightArtRequestTests : IDisposable
    {
        private const uint ITEM = 0x4000_8001, BAG = 0x4000_8002, OTHER = 0x4000_8003;

        private readonly List<ushort> _requests = new List<ushort>();

        public FirstSightArtRequestTests() => FirstSightArt.PlacedForTests = g => _requests.Add(g);

        public void Dispose() => FirstSightArt.PlacedForTests = null;

        private static Item Upsert(World world, uint serial, ushort graphic, ushort count = 1, ushort x = 1000, ushort y = 1000)
        {
            Item item = world.Writer.UpsertWorldItem(serial, graphic, 0, count, x, y, 0, Direction.North, 0, Flags.None, 0, out _,
                out var placement);
            world.Writer.PlaceWorldItem(placement);

            return item;
        }

        [Fact]
        public void Ground_Item_Requests_Its_Graphic_Once_Per_Placement()
        {
            var world = new World();

            Upsert(world, ITEM, 0x1515);

            _requests.Should().Equal(0x1515);
        }

        [Fact]
        public void Graphic_Changed_In_Place_Requests_The_New_Graphic()
        {
            var world = new World();
            Upsert(world, ITEM, 0x1515);
            _requests.Clear();

            Upsert(world, ITEM, 0x1516);

            _requests.Should().Equal(0x1516);
        }

        [Fact]
        public void Coin_Stack_Requests_The_Displayed_Variant()
        {
            var world = new World();

            Upsert(world, ITEM, 0x0EED, count: 3);
            Upsert(world, OTHER, 0x0EED, count: 50);

            _requests.Should().Equal(0x0EEE, 0x0EEF);
        }

        [Fact]
        public void Corpse_Requests_Nothing()
        {
            var world = new World();

            Upsert(world, ITEM, 0x2006);

            _requests.Should().BeEmpty("a corpse draws from the animation path");
        }

        [Fact]
        public void Container_Contents_Request_Nothing()
        {
            var world = new World();
            Upsert(world, BAG, 0x0E75);
            _requests.Clear();

            world.Writer.PlaceInContainer(ITEM, 0x1515, 1, 10, 10, 0, BAG, out _);
            world.Writer.PlaceInContainer(ITEM, 0x1516, 1, 11, 11, 0, BAG, out _);

            _requests.Should().BeEmpty("an item in a container is not drawn in the world");
        }

        [Fact]
        public void Item_Dropped_From_A_Container_Requests_Its_Graphic()
        {
            var world = new World();
            Upsert(world, BAG, 0x0E75);
            world.Writer.PlaceInContainer(ITEM, 0x1515, 1, 10, 10, 0, BAG, out _);
            _requests.Clear();

            Upsert(world, ITEM, 0x1515, x: 1001);

            _requests.Should().Equal(0x1515);
        }
    }
}
