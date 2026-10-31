using System;
using System.Collections.Generic;
using System.Linq;
using System.Buffers.Binary;
using System.IO;
using ClassicUO.Assets;
using ClassicUO.Game.Managers;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // The client loading a pack converted from a pre-UOP install (UOML, client 4.0.11c): 0x4000 source statics padded to the standardized 0x10000, five facets with
    // Trammel built from map0 + mapdif1, 7168-wide Felucca/Trammel, 12-byte multis. Runs when CUO_LEGACY_PACK names such
    // a pack and CUO_LEGACY_UO its install (read-only, for the map diff check). Skipped only while either variable is
    // unset: once they are set, a missing manifest.json or source file is a failure, so a conversion that did not produce
    // its configured fixture cannot pass as skipped. UO data is never committed.
    public class LegacyPackLoadTests
    {
        private static readonly string Pack = Environment.GetEnvironmentVariable("CUO_LEGACY_PACK");
        private static readonly string Uo = Environment.GetEnvironmentVariable("CUO_LEGACY_UO");

        [LegacyPackFact]
        public void Client_Loads_A_Pack_Converted_From_A_Pre_Uop_Install()
        {
            File.Exists(Path.Combine(Pack, "manifest.json")).Should().BeTrue($"CUO_LEGACY_PACK ({Pack}) is set, so it must hold a converted pack");

            foreach (var name in new[] { "map0.mul", "mapdifl1.mul", "mapdif1.mul" })
            {
                File.Exists(Path.Combine(Uo, name)).Should().BeTrue($"CUO_LEGACY_UO ({Uo}) is set, so it must hold the pre-UOP install's {name}");
            }

            ClientVersionHelper.IsClientVersionValid("4.0.11c", out var cv).Should().BeTrue();
            using var pack = PackAssets.Open(Pack);
            var fm = new UOFileManager(cv, Uo, pack);
            fm.Load("ENU");

            fm.TileData.LandData.Length.Should().Be(PackConst.LandTileCount);
            fm.TileData.StaticData.Length.Should().Be(PackConst.StaticTileCount); // the source's 0x4000 plus empty padding
            fm.TileData.StaticData[0x4000].Name.Should().BeEmpty();
            fm.TileData.StaticData[0xFFFF].Flags.Should().Be(TileFlag.None);
            // Animated statics registration (AnimatedStaticsManager.Initialize) over the padded table: every registered id
            // is a source entry, and the set is exactly what the unpadded 0x4000 entries register.
            var statics = new AnimatedStaticsManager();
            statics.Initialize(fm.TileData.StaticData, fm.AnimData);
            var registered = statics.RegisteredIds.ToList();
            var expected = new List<ushort>();

            for (int i = 0; i < 0x4000; i++)
            {
                if (fm.TileData.StaticData[i].IsAnimated && fm.AnimData.TryGetAnimData((ushort)i, out _))
                {
                    expected.Add((ushort)i);
                }
            }

            registered.Should().NotBeEmpty();
            registered.Should().OnlyContain(id => id < 0x4000, "padded entries carry no Animation flag");
            registered.Should().Equal(expected);

            fm.Multis.Count.Should().BeGreaterThan(0);
            fm.Multis.GetMultis(0).Should().NotBeEmpty();

            for (int f = 0; f <= 4; f++)
            {
                fm.Maps.HasMap(f).Should().BeTrue($"facet {f}");
            }

            fm.Maps.HasMap(5).Should().BeFalse();
            fm.Maps.MapsDefaultSize[0, 0].Should().Be(7168);
            fm.Maps.MapsDefaultSize[1, 0].Should().Be(7168);

            // Trammel = map0 + mapdif1. Block 20090 differs from map0 in one cell's z only; block 238434 in every id and z.
            // Each is checked against its last mapdifl1 entry (later entries win), ids and z, and against map0 (must differ).
            var difl = File.ReadAllBytes(Path.Combine(Uo, "mapdifl1.mul"));
            var dif = File.ReadAllBytes(Path.Combine(Uo, "mapdif1.mul"));
            var map0 = File.ReadAllBytes(Path.Combine(Uo, "map0.mul"));
            const int blocksY = 4096 / 8;

            foreach (int block in new[] { 20090, 238434 })
            {
                int entry = -1;

                for (int j = 0; j < difl.Length / 4; j++)
                {
                    if (BinaryPrimitives.ReadInt32LittleEndian(difl.AsSpan(j * 4)) == block)
                    {
                        entry = j;
                    }
                }

                entry.Should().BeGreaterThanOrEqualTo(0, $"block {block} is in mapdifl1.mul");
                fm.Maps.TryGetBlock(1, block / blocksY, block % blocksY, out var data).Should().BeTrue();
                int differsFromMap0 = 0;

                for (int c = 0; c < 64; c++)
                {
                    int p = entry * 196 + 4 + c * 3, b = block * 196 + 4 + c * 3;
                    ushort id = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(dif.AsSpan(p)) & 0x3FFF);
                    data.Land[c].TileId.Should().Be(id, $"block {block} cell {c} id");
                    data.Land[c].Z.Should().Be((sbyte)dif[p + 2], $"block {block} cell {c} z");

                    if (id != (BinaryPrimitives.ReadUInt16LittleEndian(map0.AsSpan(b)) & 0x3FFF) || (sbyte)dif[p + 2] != (sbyte)map0[b + 2])
                    {
                        differsFromMap0++;
                    }
                }

                differsFromMap0.Should().BeGreaterThan(0, $"block {block} of Trammel must differ from map0, or an unpatched Trammel would pass");
            }
        }
    }

    /// <summary>Skips (rather than passes) while the legacy pack or install is not configured. A configured path that
    /// lacks its files is not skipped: the test fails on it.</summary>
    public sealed class LegacyPackFactAttribute : FactAttribute
    {
        public LegacyPackFactAttribute()
        {
            string pack = Environment.GetEnvironmentVariable("CUO_LEGACY_PACK"), uo = Environment.GetEnvironmentVariable("CUO_LEGACY_UO");

            if (string.IsNullOrEmpty(pack))
            {
                Skip = "CUO_LEGACY_PACK is not set (a pack converted from a pre-UOP install)";
            }
            else if (string.IsNullOrEmpty(uo))
            {
                Skip = "CUO_LEGACY_UO is not set (the pre-UOP install the pack was converted from)";
            }
        }
    }
}
