using System;
using System.Diagnostics;
using System.IO;
using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Pack;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Assets
{
    // Pack 1.1 moved ClassicUO's hard-coded draw rules (land ids 0-2, the CanBeDrawn id lists, the "nodraw" name prefix,
    // the easel version rule, NoDiagonal, the multi item's "> 2" test) into tiledata.bin's per-tile flags. The client reads
    // only the flags. This test proves the move is pixel-neutral: for every land and static id, and both player races, the
    // decision from the flags (through the client's own loader and draw functions) equals the decision of the code it
    // replaced, kept below verbatim as the oracle and evaluated on the same file's flags and names.
    //
    // Pack: CUO_TEST_PACK, else uo-packs\<CURRENT>. A pack 1.0 tiledata.bin (the old compact layout) skips the test.
    public class TileDrawOracleTests
    {
        private readonly ITestOutputHelper _output;

        public TileDrawOracleTests(ITestOutputHelper output) => _output = output;

        internal static string PackDir()
        {
            var env = Environment.GetEnvironmentVariable("CUO_TEST_PACK");

            if (!string.IsNullOrEmpty(env))
            {
                return env;
            }

            const string root = @"C:\Repositories\uo-packs";
            var current = Path.Combine(root, "CURRENT");

            return File.Exists(current) ? Path.Combine(root, File.ReadAllText(current).Trim()) : Path.Combine(root, "classic-1.0");
        }

        [TileDataPackFact]
        public void Flags_Give_The_Same_Draw_Decision_As_The_Old_Client_Code_For_Every_Id()
        {
            var dir = PackDir();
            var path = Path.Combine(dir, TableConst.TileDataFile);
            TileDataLoader.Read(path, out var land, out var statics);

            var manifest = ManifestFileIo.Read(Path.Combine(dir, "manifest.json"));
            ClientVersionHelper.IsClientVersionValid(manifest.SourceClientVersion, out var version).Should().BeTrue();

            land.Length.Should().Be(PackConst.LandTileCount);
            int compared = 0, mismatches = 0, hiddenLand = 0, hiddenStatic = 0, hiddenGargoyle = 0, hiddenMulti = 0;

            for (int g = 0; g < land.Length; g++)
            {
                bool expected = OracleLand((ushort)g);
                bool actual = Land.IsDrawn(land, (ushort)g);
                compared++;

                if (actual != expected && mismatches++ < 10)
                {
                    _output.WriteLine($"land 0x{g:X4}: flags say {actual}, old code {expected}");
                }

                hiddenLand += expected ? 0 : 1;
            }

            for (int g = 0; g < statics.Length; g++)
            {
                foreach (bool gargoyle in new[] { false, true })
                {
                    // statics, items (Item.RefreshGraphicState), multi components and effects all go through CanBeDrawn
                    bool expected = OracleCanBeDrawn(statics, (ushort)g, gargoyle, version);
                    bool actual = GameObject.CanBeDrawn(statics, (ushort)g, gargoyle);
                    compared++;

                    if (actual != expected && mismatches++ < 10)
                    {
                        _output.WriteLine($"static 0x{g:X4} gargoyle={gargoyle}: flags say {actual}, old code {expected}");
                    }

                    if (!expected)
                    {
                        if (gargoyle)
                        {
                            hiddenGargoyle++;
                        }
                        else
                        {
                            hiddenStatic++;
                        }
                    }
                }

                bool multiExpected = OracleMultiItem((ushort)g);
                bool multiActual = Item.IsMultiItemDrawn(statics, (ushort)g);
                compared++;

                if (multiActual != multiExpected && mismatches++ < 10)
                {
                    _output.WriteLine($"multi item 0x{g:X4}: flags say {multiActual}, old code {multiExpected}");
                }

                hiddenMulti += multiExpected ? 0 : 1;
            }

            _output.WriteLine($"{dir} (client {version}): {compared} decisions ({land.Length} land, {statics.Length} statics x 2 races + multi items), " +
                              $"{mismatches} mismatches; hidden: {hiddenLand} land, {hiddenStatic} statics ({hiddenGargoyle} for a gargoyle), {hiddenMulti} multi items");
            mismatches.Should().Be(0);
            compared.Should().Be(land.Length + statics.Length * 3);
        }

        [TileDataPackFact]
        public void Compact_Arrays_Load_Time()
        {
            var path = Path.Combine(PackDir(), TableConst.TileDataFile);
            var (fullLand, fullStatics, _) = TileDataFile.Read(path);
            var (land, statics) = TileDataFile.Compact(fullLand, fullStatics);

            for (int i = 0; i < land.Length; i++)
            {
                (land[i].Flags, land[i].TexId, land[i].PackFlags).Should().Be((fullLand[i].Flags, fullLand[i].TexId, fullLand[i].PackFlags));
            }

            for (int i = 0; i < statics.Length; i++)
            {
                if (statics[i].Flags != fullStatics[i].Flags || statics[i].Height != fullStatics[i].Height || statics[i].Layer != fullStatics[i].Layer
                    || statics[i].PackFlags != fullStatics[i].PackFlags)
                {
                    throw new Xunit.Sdk.XunitException($"compact static {i} differs");
                }
            }

            const int runs = 100;
            var read = new double[runs];
            var compact = new double[runs];

            for (int i = 0; i < runs; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                var (l, s, _) = TileDataFile.Read(path);
                long t1 = Stopwatch.GetTimestamp();
                TileDataFile.Compact(l, s);
                long t2 = Stopwatch.GetTimestamp();
                read[i] = (t1 - t0) * 1e3 / Stopwatch.Frequency;
                compact[i] = (t2 - t1) * 1e3 / Stopwatch.Frequency;
            }

            Array.Sort(read);
            Array.Sort(compact);
            _output.WriteLine($"tiledata.bin {new FileInfo(path).Length} B: read+validate median {read[runs / 2]:0.000} ms (min {read[0]:0.000}), " +
                              $"build compact arrays median {compact[runs / 2]:0.000} ms (min {compact[0]:0.000}); {runs} runs");
        }

        // ---- portable: the flags are data, and the reader validates its input ----

        [Fact]
        public void Editing_A_Flag_Shows_Or_Hides_A_Tile_Without_Code()
        {
            var statics = new StaticTiles[0x10000];
            var land = new LandTiles[PackConst.LandTileCount];

            GameObject.CanBeDrawn(statics, 0x0001, false).Should().BeTrue(); // no rule in code: 0x0001 is drawn without its flag
            statics[0x0001].PackFlags = TilePackFlags.NoDraw;
            GameObject.CanBeDrawn(statics, 0x0001, false).Should().BeFalse();

            statics[0x1234].PackFlags = TilePackFlags.NoDrawUnlessGargoyle;
            GameObject.CanBeDrawn(statics, 0x1234, false).Should().BeFalse();
            GameObject.CanBeDrawn(statics, 0x1234, true).Should().BeTrue();

            Land.IsDrawn(land, 2).Should().BeTrue();
            land[2].PackFlags = TilePackFlags.NoDraw;
            Land.IsDrawn(land, 2).Should().BeFalse();

            Item.IsMultiItemDrawn(statics, 1).Should().BeTrue();
            statics[1].PackFlags |= TilePackFlags.NoDrawAsMulti;
            Item.IsMultiItemDrawn(statics, 1).Should().BeFalse();

            // NameNoDraw is informational: the converter folds the name rule into NoDraw
            statics[0x2000].PackFlags = TilePackFlags.NameNoDraw;
            GameObject.CanBeDrawn(statics, 0x2000, false).Should().BeTrue();
        }

        [Fact]
        public void Reader_Rejects_Bad_Counts_Sizes_And_The_Pack_1_0_Layouts()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cuo-tiledata-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                string Write(string name, Action<BinaryWriter> body)
                {
                    var p = Path.Combine(dir, name);

                    using (var w = new BinaryWriter(File.Create(p)))
                    {
                        body(w);
                    }

                    return p;
                }

                void Header(BinaryWriter w, uint magic)
                {
                    w.Write(magic);
                    w.Write((ushort)1);
                    w.Write((ushort)0);
                    w.Write(0UL);
                }

                var names = "abc"u8.ToArray();
                var land = new LandTileInfo[PackConst.LandTileCount];
                var statics = new StaticTileInfo[16];
                statics[3].NameOffset = 0;
                statics[3].NameLength = 3;
                var good = Path.Combine(dir, "good.bin");
                TileDataFile.Write(good, land, statics, names);
                var (l, s, n) = TileDataFile.Read(good);
                (l.Length, s.Length, TileDataFile.Name(n, s[3].NameOffset, s[3].NameLength)).Should().Be((PackConst.LandTileCount, 16, "abc"));

                Action read(string p) => () => TileDataFile.Read(p);

                // pack 1.0's compact tiledata.bin and tiledata_ext.bin: a clear "convert again" error
                read(Write("tltd.bin", w => { Header(w, PackConst.MagicTileDataCompactRetired); w.Write(0); w.Write(0); w.Write(0); }))
                    .Should().Throw<InvalidDataException>().WithMessage("*pack 1.0*");
                read(Write("tldx.bin", w => { Header(w, TableConst.MagicTileDataExtRetired); w.Write(0); w.Write(0); w.Write(0); }))
                    .Should().Throw<InvalidDataException>().WithMessage("*pack 1.0*");

                // counts the client cannot index, or that would allocate before the size check
                read(Write("land.bin", w => { Header(w, TableConst.MagicTileData); w.Write(5); w.Write(16); w.Write(0); }))
                    .Should().Throw<InvalidDataException>().WithMessage("*land entries*");
                read(Write("huge.bin", w => { Header(w, TableConst.MagicTileData); w.Write(PackConst.LandTileCount); w.Write(int.MaxValue); w.Write(0); }))
                    .Should().Throw<InvalidDataException>().WithMessage("*static entries*");
                read(Write("names.bin", w => { Header(w, TableConst.MagicTileData); w.Write(PackConst.LandTileCount); w.Write(16); w.Write(-1); }))
                    .Should().Throw<InvalidDataException>().WithMessage("*name table*");
                read(Write("short.bin", w => { Header(w, TableConst.MagicTileData); w.Write(PackConst.LandTileCount); w.Write(16); w.Write(3); }))
                    .Should().Throw<InvalidDataException>().WithMessage("*bytes, expected*");
                read(Write("trunc.bin", w => w.Write(0)))
                    .Should().Throw<InvalidDataException>().WithMessage("*truncated*");

                // a name reference outside the name table
                statics[3].NameLength = 4;
                var bad = Path.Combine(dir, "badname.bin");
                TileDataFile.Write(bad, land, statics, names);
                read(bad).Should().Throw<InvalidDataException>().WithMessage("*static 3 name*");
            }
            finally
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    File.Delete(f);
                }

                Directory.Delete(dir);
            }
        }

        // ---- The oracle: the client code the flags replaced, verbatim except for the data access (Client.Game,
        // world.Player.Race and Client.Game.UO.Version became parameters). Keep it unchanged: it defines pixel-neutral.

        // Land.Create and Land.UpdateGraphicBySeason: AllowedToDraw = Graphic > 2;
        private static bool OracleLand(ushort graphic) => graphic > 2;

        // Item.RefreshGraphicState (multi): AllowedToDraw = MultiGraphic > 2;
        private static bool OracleMultiItem(ushort multiGraphic) => multiGraphic > 2;

        // GameObject.CanBeDrawn
        private static bool OracleCanBeDrawn(StaticTiles[] staticData, ushort g, bool playerIsGargoyle, ClientVersion version)
        {
            switch (g)
            {
                case 0x0001:
                case 0x21BC:
                case 0xA1FE:
                case 0xA1FF:
                case 0xA200:
                case 0xA201:
                    //case 0x5690:
                    return false;

                case 0x9E4C:
                case 0x9E64:
                case 0x9E65:
                case 0x9E7D:
                    ref var data = ref staticData[g];

                    return !data.IsBackground && !data.IsSurface;
            }

            if (g != 0x63D3)
            {
                if (g >= 0x2198 && g <= 0x21A4)
                {
                    return false;
                }

                // Easel fix.
                // In older clients the tiledata flag for this
                // item contains NoDiagonal for some reason.
                // So the next check will make the item invisible.
                if (g == 0x0F65 && version < ClientVersion.CV_60144)
                {
                    return true;
                }

                if (g < staticData.Length)
                {
                    ref var data = ref staticData[g];

                    // Hacky way to do not render "nodraw"
                    if (!string.IsNullOrEmpty(data.Name) && data.Name.StartsWith("nodraw", StringComparison.OrdinalIgnoreCase))
                        return false;

                    if (
                        !data.IsNoDiagonal
                        || data.IsAnimated
                            && playerIsGargoyle
                    )
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>Skips unless the test pack has a pack 1.1 tiledata.bin (magic TILE).</summary>
    public sealed class TileDataPackFactAttribute : FactAttribute
    {
        public TileDataPackFactAttribute()
        {
            var path = Path.Combine(TileDrawOracleTests.PackDir(), TableConst.TileDataFile);

            if (!File.Exists(path))
            {
                Skip = $"integration data not on this machine: {path}";

                return;
            }

            using var fs = File.OpenRead(path);
            var magic = new byte[4];

            if (fs.Read(magic, 0, 4) != 4 || BitConverter.ToUInt32(magic, 0) != TableConst.MagicTileData)
            {
                Skip = $"{path} is a pack 1.0 tiledata.bin; set CUO_TEST_PACK to a pack 1.1 folder";
            }
        }
    }
}
