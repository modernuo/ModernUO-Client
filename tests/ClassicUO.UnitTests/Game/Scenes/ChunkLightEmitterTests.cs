using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Game.Scenes;
using ClassicUO.UnitTests.Assets;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// chunk_lights round W2a: the chunks' emitter lists hold every light-flagged static whose light GameScene.AddLightCore
    /// draws. CUO_CHUNK_LIGHTS_CHECK logged "not in its chunk's light list: Static 0x00CB at 1517,1611,10" in Britain: the
    /// arrow loop's tiledata light index is 255, which has no shape, but with colored lights (the default) AddLightCore
    /// draws an index above 200 as light 1 tinted (index - 200). The table took only the index itself, so those statics had
    /// no light with chunk_lights on (Britain zoomed out: three arrow loops in the view).
    /// </summary>
    [Collection("ChunkStaticTileDataCollection")] // LightEmitters.OverrideForTests is process-wide
    public sealed class ChunkLightEmitterTests
    {
        // shapes 1, 2 and 26 exist; 43 and 255 have none (the pack's lights: below 100, non-empty)
        private static bool FakeShape(int id) => id == 1 || id == 2 || id == 26;

        [Theory]
        [InlineData(255, true, 1, 55)]
        [InlineData(220, true, 1, 20)]
        [InlineData(201, true, 1, 1)]
        [InlineData(200, false, 0, 0)]
        [InlineData(26, false, 0, 0)]
        public void Colored_index_is_shape_one_tinted(int index, bool colored, int shape, int tint)
        {
            Assert.Equal(colored, LightEmitters.ColoredIndex(index, out byte s, out ushort t));
            Assert.Equal(shape, s);
            Assert.Equal(tint, t);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Shape_follows_the_light_add_in_both_colored_states(bool colored)
        {
            const ushort arrowLoop = 0x00CB, window = 0x0162;
            Assert.Equal(colored ? 1 : 255, LightEmitters.ShapeOf(arrowLoop, 255, colored));
            Assert.Equal(26, LightEmitters.ShapeOf(window, 26, colored));
            // fire fields and 0x0B1D draw light 2 whatever their index
            Assert.Equal(2, LightEmitters.ShapeOf(0x3E02, 255, colored));
            Assert.Equal(2, LightEmitters.ShapeOf(0x0B1D, 43, colored));
        }

        [Fact]
        public void Static_emits_when_its_light_draws_in_some_colored_state()
        {
            Assert.True(LightEmitters.StaticEmits(0x00CB, 255, FakeShape));
            Assert.True(LightEmitters.StaticEmits(0x0162, 26, FakeShape));
            Assert.True(LightEmitters.StaticEmits(0x3914, 43, FakeShape));
            // a flagged index up to 200 without a shape draws nothing either way
            Assert.False(LightEmitters.StaticEmits(0x0100, 43, FakeShape));
        }

        private static (StaticTiles[] Statics, Func<int, bool> HasShape) Pack()
        {
            string dir = TileDrawOracleTests.PackDir();
            TileDataLoader.Read(Path.Combine(dir, TableConst.TileDataFile), out _, out StaticTiles[] statics);
            LightEntry[] lights = LightFile.Read(Path.Combine(dir, TableConst.LightsFile)).Lights;

            // LightsLoader.HasShape
            return (statics, id => (uint)id < (uint)Math.Min(LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT, lights.Length) && lights[id].Width != 0 && lights[id].Height != 0);
        }

        // every light-flagged static whose light AddLightCore draws, with colored lights on or off, is in the table
        [TileDataPackFact]
        public void Pack_table_holds_every_static_whose_light_draws()
        {
            var (statics, hasShape) = Pack();
            bool[] table = LightEmitters.BuildTable(statics, hasShape);
            var missing = new List<string>();

            for (int g = 0; g < statics.Length; g++)
            {
                if (!statics[g].IsLight)
                {
                    Assert.False(table[g]);

                    continue;
                }

                foreach (bool colored in new[] { true, false })
                {
                    if (hasShape(LightEmitters.ShapeOf((ushort)g, statics[g].Layer, colored)) && !table[g] && missing.Count < 10)
                    {
                        missing.Add($"0x{g:X4} '{statics[g].Name}' light {statics[g].Layer} colored {colored}");
                    }
                }
            }

            Assert.True(missing.Count == 0, "flagged statics whose light draws, not in the emitter table: " + string.Join("; ", missing));
            Assert.Equal(255, statics[0x00CB].Layer);
            Assert.True(table[0x00CB]);
        }

        // Britain 1495 1629 at zoom 2.5 (1280 x 720 view): the light-flagged map statics in the walk's rectangle (map 0)
        private const string BritainZoomedOut = @"
1440 1645 10 000E;1472 1615 20 00BA;1479 1609 20 00B9;1513 1575 20 013A;1475 1615 20 00BA;1447 1645 10 000E;1477 1615 20 00BA;
1479 1614 20 00B9;1451 1648 10 000F;1491 1609 20 013B;1509 1591 10 000E;1451 1651 10 000F;1511 1591 10 000E;1491 1612 40 013B;
1513 1593 10 000F;1491 1617 40 013B;1513 1596 10 000F;1507 1612 40 013B;1495 1625 40 013A;1507 1613 20 013B;1507 1617 40 013B;
1491 1636 31 0A0C;1502 1625 20 013A;1517 1611 10 00CB;1504 1625 40 013A;1494 1636 31 0A0C;1505 1625 20 013A;1517 1613 10 00CB;
1517 1615 10 00CB;1490 1645 16 0A07;1495 1641 31 0A07;1494 1646 23 0A07;1510 1638 30 0A07;1495 1659 27 003C;1499 1655 27 003B;
1510 1645 30 0A07;1495 1664 27 003C;1504 1655 27 003B;1498 1667 27 003B;1501 1667 27 003B;1507 1661 27 003C;1485 1686 20 003C;
1488 1683 20 003B;1493 1678 20 003C";

        [TileDataPackFact]
        public void Britain_arrow_loops_are_in_their_chunks_light_lists()
        {
            var (statics, hasShape) = Pack();
            LightEmitters.OverrideForTests = LightEmitters.BuildTable(statics, hasShape);
            StaticTiles[] saved = Chunk.StaticTileData;
            Chunk.StaticTileData = statics;
            var chunks = new Dictionary<(int, int), Chunk>();

            try
            {
                var world = new World();
                var placed = new List<Static>();

                foreach (string row in BritainZoomedOut.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] f = row.Split(' ');
                    int x = int.Parse(f[0]), y = int.Parse(f[1]);
                    var key = (x >> 3, y >> 3);

                    if (!chunks.TryGetValue(key, out Chunk chunk))
                    {
                        chunks[key] = chunk = Chunk.Create(world, key.Item1, key.Item2);
                    }

                    var st = new Static(world) { Graphic = Convert.ToUInt16(f[3], 16), X = (ushort)x, Y = (ushort)y, Z = sbyte.Parse(f[2]) };
                    chunk.AddGameObject(st, x & 7, y & 7);
                    placed.Add(st);
                }

                var unlisted = new List<string>();

                foreach (Static st in placed)
                {
                    // the classic walk's light with colored lights on (the profile default)
                    if (!hasShape(LightEmitters.ShapeOf(st.Graphic, statics[st.Graphic].Layer, true)))
                    {
                        continue;
                    }

                    Chunk chunk = chunks[(st.X >> 3, st.Y >> 3)];

                    if (!chunk.LightSources.Take(chunk.LightSourceCount).Contains(st))
                    {
                        unlisted.Add($"0x{st.Graphic:X4} at {st.X},{st.Y},{st.Z}");
                    }
                }

                Assert.True(unlisted.Count == 0, "not in its chunk's light list: " + string.Join("; ", unlisted));
                Assert.Equal(44, placed.Count);
                Assert.Equal(44, chunks.Values.Sum(c => c.LightSourceCount));
            }
            finally
            {
                foreach (Chunk chunk in chunks.Values)
                {
                    chunk.Destroy();
                }

                LightEmitters.OverrideForTests = null;
                Chunk.StaticTileData = saved;
            }
        }
    }
}
