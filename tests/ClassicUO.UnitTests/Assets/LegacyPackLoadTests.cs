using System;
using System.Buffers.Binary;
using System.IO;
using ClassicUO.Assets;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // The client loading a pack converted from a pre-UOP install (UOML, client 4.0.11c): 0x4000 statics, five facets with
    // Trammel built from map0 + mapdif1, 7168-wide Felucca/Trammel, 12-byte multis. Runs only when CUO_LEGACY_PACK names
    // such a pack (and CUO_LEGACY_UO its install, read-only, for the map diff check); UO data is never committed.
    public class LegacyPackLoadTests
    {
        private static readonly string Pack = Environment.GetEnvironmentVariable("CUO_LEGACY_PACK");
        private static readonly string Uo = Environment.GetEnvironmentVariable("CUO_LEGACY_UO");

        [Fact]
        public void Client_Loads_A_Pack_Converted_From_A_Pre_Uop_Install()
        {
            if (string.IsNullOrEmpty(Pack) || !File.Exists(Path.Combine(Pack, "manifest.json")))
            {
                return;
            }

            ClientVersionHelper.IsClientVersionValid("4.0.11c", out var cv).Should().BeTrue();
            using var pack = PackAssets.Open(Pack);
            var fm = new UOFileManager(cv, string.IsNullOrEmpty(Uo) ? Path.GetTempPath() : Uo, pack);
            fm.Load("ENU");

            fm.TileData.LandData.Length.Should().Be(0x4000);
            fm.TileData.StaticData.Length.Should().Be(0x4000);
            fm.Multis.Count.Should().BeGreaterThan(0);
            fm.Multis.GetMultis(0).Should().NotBeEmpty();

            for (int f = 0; f <= 4; f++)
            {
                fm.Maps.HasMap(f).Should().BeTrue($"facet {f}");
            }

            fm.Maps.HasMap(5).Should().BeFalse();
            fm.Maps.MapsDefaultSize[0, 0].Should().Be(7168);
            fm.Maps.MapsDefaultSize[1, 0].Should().Be(7168);

            if (string.IsNullOrEmpty(Uo) || !File.Exists(Path.Combine(Uo, "mapdifl1.mul")))
            {
                return;
            }

            // Trammel's last diff entry for a block wins; check the last one against mapdif1.mul, cell by cell
            var difl = File.ReadAllBytes(Path.Combine(Uo, "mapdifl1.mul"));
            var dif = File.ReadAllBytes(Path.Combine(Uo, "mapdif1.mul"));
            int last = difl.Length / 4 - 1;
            int block = BinaryPrimitives.ReadInt32LittleEndian(difl.AsSpan(last * 4));
            int blocksY = 4096 / 8;
            fm.Maps.TryGetBlock(1, block / blocksY, block % blocksY, out var data).Should().BeTrue();

            for (int c = 0; c < 64; c++)
            {
                int p = last * 196 + 4 + c * 3;
                data.Land[c].Z.Should().Be((sbyte)dif[p + 2], $"cell {c}");
            }
        }
    }
}
