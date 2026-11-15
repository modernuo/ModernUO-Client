using System.Buffers.Binary;
using System.Text;
using ClassicUO.Assets;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Converter;
using Uo.Pack;
using Xunit;

namespace Uo.Converter.Tests;

// Pre-UOP (pre-7.0.9) import paths on small synthetic MUL files: the version scan of a version-less client.exe, the
// old tiledata layout, 12-byte multi components and ModernUO-style map diffs. One class: the MUL loaders keep static
// state (TileDataLoader's arrays, MapLoader.MAPS_COUNT), so these tests must not run in parallel with each other.
public sealed class LegacyImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"uo_legacy_import_{Guid.NewGuid():N}");
    private readonly string _uo, _out;

    public LegacyImportTests()
    {
        _uo = Path.Combine(_dir, "uo");
        _out = Path.Combine(_dir, "pack");
        Directory.CreateDirectory(_uo);
        Directory.CreateDirectory(_out);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ClientVersion Cv(string text)
    {
        ClientVersionHelper.IsClientVersionValid(text, out var cv).Should().BeTrue();
        return cv;
    }

    // ---- client version of a version-less exe

    private const uint ImageBase = 0x400000, SectionVa = 0x1000, RawOff = 0x200;

    /// <summary>A PE32 with one section; <paramref name="body"/> is its raw data (file offset 0x200).</summary>
    private static byte[] Pe(byte[] body)
    {
        var d = new byte[RawOff + body.Length];
        d[0] = (byte)'M';
        d[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0x3C), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x40), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x44 + 2), 1);    // sections
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x44 + 16), 0xE0); // optional header size
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x58), 0x10B);     // PE32
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x58 + 28), ImageBase);
        int sh = 0x58 + 0xE0;
        Encoding.ASCII.GetBytes(".text").CopyTo(d, sh);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(sh + 8), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(sh + 12), SectionVa);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(sh + 16), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(sh + 20), RawOff);
        body.CopyTo(d, RawOff);
        return d;
    }

    private static uint Va(int bodyOffset) => ImageBase + SectionVa + (uint)bodyOffset;

    private static void Push32(List<byte> code, uint v)
    {
        code.Add(0x68);
        code.AddRange(BitConverter.GetBytes(v));
    }

    private static void PushNumber(List<byte> code, int v)
    {
        if (v <= 0x7F)
        {
            code.Add(0x6A);
            code.Add((byte)v);
        }
        else
        {
            Push32(code, (uint)v);
        }
    }

    /// <summary>Strings at 0 ("%d.%d.%d%s") and 16 (suffix), then <paramref name="calls"/> sprintf call sites at 32.</summary>
    private static byte[] VersionExe(string suffix, params (int Major, int Minor, int Build)[] calls)
    {
        var body = new List<byte>(Encoding.ASCII.GetBytes("%d.%d.%d%s\0"));
        while (body.Count < 16) body.Add(0);
        body.AddRange(Encoding.ASCII.GetBytes(suffix + "\0"));
        while (body.Count < 32) body.Add(0x90);
        foreach (var (major, minor, build) in calls)
        {
            Push32(body, Va(16));
            PushNumber(body, build);
            PushNumber(body, minor);
            PushNumber(body, major);
            Push32(body, Va(0));
            Push32(body, 0x0087F1D4); // the buffer
            body.AddRange(new byte[] { 0xE8, 0, 0, 0, 0, 0x83, 0xC4, 0x18 });
        }

        return Pe(body.ToArray());
    }

    [Fact]
    public void Version_Format_Call_Of_A_Version_Less_Exe_Is_Read_As_ModernUO_Does()
    {
        // the UOML client.exe (July 2005): push "c"; push 11; push 0; push 4; push "%d.%d.%d%s"
        LegacyClientVersion.DetectFromVersionFormat(VersionExe("c", (4, 0, 11))).Should().Be("4.0.11c");
        LegacyClientVersion.DetectFromVersionInfo(VersionExe("c", (4, 0, 11))).Should().BeNull();

        // letters count from 'a' = 1, as ModernUO's ClientVersion: "4.0.11c" is 4.0.11.3 = CV_4011C
        ((uint)Cv("4.0.11c")).Should().Be(0x04000B03u);
        Cv("4.0.11c").Should().Be(ClientVersion.CV_4011C);
        LegacyClientVersion.MinSupported.Should().Be(Cv("4.0.11c"));
        (Cv("4.0.11c") < ClientVersion.CV_6000).Should().BeTrue();

        LegacyClientVersion.DetectFromVersionFormat(VersionExe("", (5, 0, 9))).Should().Be("5.0.9");
    }

    [Fact]
    public void Version_Format_Call_Returns_Nothing_Rather_Than_Guess()
    {
        LegacyClientVersion.DetectFromVersionFormat(VersionExe("c", (4, 0, 11), (4, 0, 11))).Should().BeNull("two pushes of the format");
        LegacyClientVersion.DetectFromVersionFormat(VersionExe("c")).Should().BeNull("no push of the format");
        LegacyClientVersion.DetectFromVersionFormat(VersionExe("ab", (4, 0, 11))).Should().BeNull("two-letter suffix");
        LegacyClientVersion.DetectFromVersionFormat(VersionExe("C", (4, 0, 11))).Should().BeNull("upper-case suffix");
        LegacyClientVersion.DetectFromVersionFormat(VersionExe("c", (4, 0, 200))).Should().BeNull("a number pushed as imm32");
        LegacyClientVersion.DetectFromVersionFormat(Encoding.ASCII.GetBytes("MZ not a PE %d.%d.%d%s\0")).Should().BeNull();
    }

    [Fact]
    public void Version_Resolution_Order_Is_Flag_Then_Settings_Then_Exe_And_Unknown_Names_Both_Options()
    {
        File.WriteAllBytes(Path.Combine(_uo, "client.exe"), VersionExe("c", (4, 0, 11)));
        var settings = Path.Combine(_dir, "settings.json");
        File.WriteAllText(settings, "{ \"ultimaonlinedirectory\": \"x\", \"clientversion\": \"5.0.9.1\" }");

        LegacyClientVersion.TryResolve(_uo, "7.0.15.1", settings, out var text, out _, out var source).Should().BeTrue();
        (text, source).Should().Be(("7.0.15.1", "--client-version"));
        LegacyClientVersion.TryResolve(_uo, null, settings, out text, out _, out source).Should().BeTrue();
        text.Should().Be("5.0.9.1");
        source.Should().StartWith("--cuo-settings");
        LegacyClientVersion.TryResolve(_uo, null, null, out text, out _, out source).Should().BeTrue();
        (text, source).Should().Be(("4.0.11c", "client.exe version format call"));

        File.WriteAllBytes(Path.Combine(_uo, "client.exe"), Pe(new byte[64]));
        LegacyClientVersion.TryResolve(_uo, null, null, out _, out _, out source).Should().BeFalse();
        // older than the oldest supported client: resolved, and refused by the converter (Program.cs)
        LegacyClientVersion.TryResolve(_uo, "4.0.11b", null, out _, out var older, out _).Should().BeTrue();
        (older < LegacyClientVersion.MinSupported).Should().BeTrue();
        source.Should().BeEmpty();
        LegacyClientVersion.FlagHelp.Should().Contain("--client-version").And.Contain("--cuo-settings");
        LegacyClientVersion.TryResolve(_uo, "not.a.version!", null, out _, out _, out _).Should().BeFalse();
    }

    // ---- tiledata

    private const string LandName = "grass", StaticName = "barrel";

    /// <summary>tiledata.mul with 512 land groups and <paramref name="staticGroups"/> static groups; land 5 and static
    /// 100 carry distinctive values (flag bit 31, and bit 40 in the 64-bit layout).</summary>
    private static byte[] TileData(bool old, int staticGroups)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Flags(ulong f)
        {
            if (old) w.Write((uint)f);
            else w.Write(f);
        }

        void Name(string s)
        {
            var b = new byte[20];
            Encoding.ASCII.GetBytes(s).CopyTo(b, 0);
            w.Write(b);
        }

        for (int g = 0; g < 512; g++)
        {
            w.Write(0u);
            for (int j = 0; j < 32; j++)
            {
                bool hit = g * 32 + j == 5;
                Flags(hit ? (old ? 0x8000_0040ul : 0x100_8000_0040ul) : 0);
                w.Write((ushort)(hit ? 0x1234 : 0));
                Name(hit ? LandName : "");
            }
        }

        for (int g = 0; g < staticGroups; g++)
        {
            w.Write(0u);
            for (int j = 0; j < 32; j++)
            {
                bool hit = g * 32 + j == 100;
                Flags(hit ? (old ? 0x8000_0001ul : 0x100_8000_0001ul) : 0);
                w.Write((byte)(hit ? 7 : 0));       // weight
                w.Write((byte)(hit ? 9 : 0));       // layer
                w.Write(hit ? 12345 : 0);           // count
                w.Write((ushort)(hit ? 0x190 : 0)); // anim
                w.Write((ushort)(hit ? 0x55 : 0));  // hue
                w.Write((ushort)(hit ? 3 : 0));     // light
                w.Write((byte)(hit ? 15 : 0));      // height
                Name(hit ? StaticName : "");
            }
        }

        return ms.ToArray();
    }

    [Theory]
    [InlineData("4.0.11c", true)]
    [InlineData("7.0.9.0", false)]
    public void Tiledata_Old_And_New_Layouts_Import_Into_TiledataBin(string version, bool old)
    {
        var path = Path.Combine(_uo, "tiledata.mul");
        File.WriteAllBytes(path, TileData(old, 512));
        var cv = Cv(version);
        ClientLoad.CheckTileDataLayout(path, cv).Should().BeNull();

        var fm = new UOFileManager(cv, _uo);
        fm.TileData.Load();
        TableConvert.TileData(fm, _out);
        var (land, statics, names) = TileDataFile.Read(Path.Combine(_out, TableConst.TileDataFile));

        land.Length.Should().Be(PackConst.LandTileCount);
        // 0x4000 statics in the source (as UOML has), padded to the standardized 0x10000 with empty entries
        statics.Length.Should().Be(PackConst.StaticTileCount);
        string Name(uint off, ushort len) => Encoding.UTF8.GetString(names, (int)off, len);

        land[5].Flags.Should().Be(old ? 0x8000_0040ul : 0x100_8000_0040ul);
        land[5].TexId.Should().Be(0x1234);
        Name(land[5].NameOffset, land[5].NameLength).Should().Be(LandName);
        land[6].Flags.Should().Be(0);

        var s = statics[100];
        s.Flags.Should().Be(old ? 0x8000_0001ul : 0x100_8000_0001ul);
        (s.Weight, s.Layer, s.Count, s.AnimId, s.Hue, s.LightIndex, s.Height).Should().Be(((byte)7, (byte)9, 12345, (ushort)0x190, (ushort)0x55, (ushort)3, (byte)15));
        Name(s.NameOffset, s.NameLength).Should().Be(StaticName);
        statics[101].Flags.Should().Be(0);
        for (int i = 512 * 32; i < statics.Length; i++)
        {
            var p = statics[i];
            p.PackFlags = 0;
            p.Should().Be(default(StaticTileInfo), $"padded static 0x{i:X} is empty");
        }
    }

    [Fact]
    public void Legacy_Statics_Are_Padded_Exactly_Like_A_Modern_Install_s_Empty_Entries()
    {
        // The same entries, once as a pre-7.0.9 file with 0x4000 statics (padded by the converter) and once as a 7.0.9+
        // file with 0x10000 (the rest genuinely empty): every entry from 0x4000 up, PackFlags included, is identical.
        var path = Path.Combine(_uo, "tiledata.mul");
        StaticTileInfo[] Convert(string version, bool old, int groups, string sub)
        {
            File.WriteAllBytes(path, TileData(old, groups));
            var fm = new UOFileManager(Cv(version), _uo);
            fm.TileData.Load();
            var dir = Path.Combine(_out, sub);
            Directory.CreateDirectory(dir);
            TableConvert.TileData(fm, dir).Should().Contain(old ? "(16384 from the source, 49152 padded empty)" : "65536 statics,");
            return TileDataFile.Read(Path.Combine(dir, TableConst.TileDataFile)).Statics;
        }

        var legacy = Convert("4.0.11c", true, 512, "legacy");
        var modern = Convert("7.0.9.0", false, 2048, "modern");
        legacy.Length.Should().Be(PackConst.StaticTileCount);
        legacy.Skip(0x4000).SequenceEqual(modern.Skip(0x4000)).Should().BeTrue();
        // the draw rules keyed on ids in the padded range still apply (0xA1FE is never drawn)
        (legacy[0xA1FE].PackFlags & TilePackFlags.NoDraw).Should().Be(TilePackFlags.NoDraw);
    }

    [Fact]
    public void Tiledata_With_More_Statics_Than_The_Format_Holds_Is_Refused_Not_Truncated()
    {
        File.WriteAllBytes(Path.Combine(_uo, "tiledata.mul"), TileData(old: false, 2049)); // 0x10020 statics
        var fm = new UOFileManager(Cv("7.0.9.0"), _uo);
        fm.TileData.Load();
        fm.TileData.StaticData.Length.Should().Be(2049 * 32);
        ((Action)(() => TableConvert.TileData(fm, _out))).Should().Throw<InvalidDataException>().WithMessage("*65568 static entries*Refusing to truncate*");
        File.Exists(Path.Combine(_out, TableConst.TileDataFile)).Should().BeFalse();
    }

    [Fact]
    public void Tiledata_Layout_That_Does_Not_Fit_The_Version_Is_Refused_With_The_Flag_Named()
    {
        var path = Path.Combine(_uo, "tiledata.mul");
        File.WriteAllBytes(path, TileData(old: true, 512));
        ClientLoad.CheckTileDataLayout(path, Cv("7.0.9.0")).Should().Contain("fits the pre-7.0.9 layout").And.Contain("--client-version");
        File.WriteAllBytes(path, TileData(old: false, 2048));
        ClientLoad.CheckTileDataLayout(path, Cv("4.0.11c")).Should().Contain("fits the 7.0.9+ layout");
        ClientLoad.CheckTileDataLayout(path, Cv("7.0.15.1")).Should().BeNull();
    }

    // ---- multis

    private void WriteMultis(bool sixteen, params (short Graphic, short X, short Y, short Z, uint Flags)[][] multis)
    {
        using var idx = new BinaryWriter(File.Create(Path.Combine(_uo, "multi.idx")));
        using var mul = new BinaryWriter(File.Create(Path.Combine(_uo, "multi.mul")));
        foreach (var comps in multis)
        {
            if (comps.Length == 0)
            {
                idx.Write(-1);
                idx.Write(-1);
                idx.Write(0);
                continue;
            }

            idx.Write((int)mul.BaseStream.Position);
            idx.Write(comps.Length * (sixteen ? 16 : 12));
            idx.Write(0);
            foreach (var c in comps)
            {
                mul.Write((ushort)c.Graphic);
                mul.Write(c.X);
                mul.Write(c.Y);
                mul.Write(c.Z);
                mul.Write(c.Flags);
                if (sixteen) mul.Write(0xDEADBEEFu); // the post-7.0.9 extra field
            }
        }
    }

    [Theory]
    [InlineData("4.0.11c", false)]
    [InlineData("7.0.9.0", true)]
    public void Multis_12_And_16_Byte_Components_Import_With_Visibility_From_NonZero_Flags(string version, bool sixteen)
    {
        WriteMultis(sixteen,
            [(0x0064, -3, 2, 0, 1), (0x0065, 0, 0, 7, 0), (0x0066, 4, -5, -2, 0x100)],
            [],
            [(0x0bb8, 1, 1, 20, 1)]);
        var fm = new UOFileManager(Cv(version), _uo);
        TableConvert.Multis(fm, _out);
        var (entries, comps) = MultiFile.Read(Path.Combine(_out, TableConst.MultisFile));

        entries.Length.Should().Be(3);
        MultiFile.Components(entries, comps, 1).Length.Should().Be(0);
        var m0 = MultiFile.Components(entries, comps, 0).ToArray();
        m0.Select(c => (c.Graphic, c.X, c.Y, c.Z)).Should().Equal(((ushort)0x64, (short)-3, (short)2, (short)0), ((ushort)0x65, (short)0, (short)0, (short)7), ((ushort)0x66, (short)4, (short)-5, (short)-2));
        // MUL rule (both layouts): visible iff flags != 0 (0x100 is visible here; only UOP treats 0 and 0x100 as visible)
        m0.Select(c => c.Flags).Should().Equal(MultiComponentFlags.Visible, MultiComponentFlags.None, MultiComponentFlags.Visible);
        MultiFile.Components(entries, comps, 2).ToArray().Single().Should().Be(new MultiComponent { Graphic = 0xbb8, X = 1, Y = 1, Z = 20, Flags = MultiComponentFlags.Visible });
    }

    // ---- maps: diffs applied as ModernUO serves them

    private const int Facet = 4, Bw = 1448 / 8, Bh = 1448 / 8; // Tokuno: the smallest facet ClassicUO sizes by default

    private static byte[] Block(ushort tile, sbyte z)
    {
        var b = new byte[196];
        for (int c = 0; c < 64; c++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4 + c * 3), tile);
            b[4 + c * 3 + 2] = (byte)z;
        }

        return b;
    }

    private static byte[] Statics(params (ushort G, byte X, byte Y, sbyte Z, ushort Hue)[] s)
    {
        var b = new byte[s.Length * 7];
        for (int i = 0; i < s.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 7), s[i].G);
            b[i * 7 + 2] = s[i].X;
            b[i * 7 + 3] = s[i].Y;
            b[i * 7 + 4] = (byte)s[i].Z;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 7 + 5), s[i].Hue);
        }

        return b;
    }

    private static byte[] Ints(params int[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();

    private void Write(string name, byte[] data) => File.WriteAllBytes(Path.Combine(_uo, name), data);

    private static byte[] Map(int columns, ushort tile)
    {
        var map = new byte[columns * Bh * 196];
        for (int b = 0; b < columns * Bh; b++) Block(tile, 0).CopyTo(map, b * 196);
        return map;
    }

    /// <summary>Base facet 4 (tile 3; statics in blocks 0 and 5), no diffs.</summary>
    private void WriteBaseFacet(string suffix = "", ushort tile = 3, int columns = Bw)
    {
        // map0 only has to exist (MapLoader.Load reads its length); facet 4 holds the data
        Write("map0.mul", Block(3, 0));
        Write("staidx0.mul", Ints(-1, 0, 0));
        Write("statics0.mul", []);
        Write($"map{Facet}{suffix}.mul", Map(columns, tile));

        // block 0's statics start at offset 0 of statics4.mul (a valid address), block 5 has two more
        var sta = Statics((0x100, 1, 2, 5, 0), (0x101, 7, 7, -5, 0x22), (0x200, 3, 3, 0, 0), (0x201, 9, 0, 0, 0));
        var idx = new byte[columns * Bh * 12];
        for (int b = 0; b < columns * Bh; b++) Ints(-1, 0, 0).CopyTo(idx, b * 12);
        Ints(0, 14, 0).CopyTo(idx, 0);
        Ints(14, 14, 0).CopyTo(idx, 5 * 12);
        Write($"staidx{Facet}{suffix}.mul", idx);
        Write($"statics{Facet}{suffix}.mul", sta);
    }

    private void WriteLandDiffs() // block 7
    {
        Write($"mapdifl{Facet}.mul", Ints(7));
        Write($"mapdif{Facet}.mul", Block(0x77, 10));
    }

    private void WriteStaticDiffs() // block 5 replaced by the first record of stadif4 (offset 0), block 0 emptied
    {
        Write($"stadifl{Facet}.mul", Ints(5, 0));
        Write($"stadifi{Facet}.mul", Ints(0, 7, 0, -1, 0, 0));
        Write($"stadif{Facet}.mul", Statics((0x300, 0, 0, 1, 0)));
    }

    private void WriteFacet()
    {
        WriteBaseFacet();
        WriteLandDiffs();
        WriteStaticDiffs();
    }

    private BlockData[] ConvertFacet(string version, out UOFileManager fm)
    {
        fm = new UOFileManager(Cv(version), _uo);
        ClientLoad.ApplyLegacyMapRules(fm);
        MapConvert.Facet(fm, Facet, _out);
        var (h, blocks) = RegionFile.Read(PackConst.RegionPath(Path.Combine(_out, "map", Facet.ToString()), 0, 0));
        (h.BlocksW, h.BlocksH).Should().Be(((ushort)PackConst.RegionBlocks, (ushort)PackConst.RegionBlocks));
        return blocks;
    }

    private static int At(int blockId) => blockId % Bh * PackConst.RegionBlocks + blockId / Bh; // column-major id -> region row-major

    private static void OracleAgrees(UOFileManager fm, BlockData[] blocks)
    {
        var oracle = MulMapOracle.TryOpen(fm, Facet, Bw, Bh)!;
        oracle.Should().NotBeNull();
        for (int id = 0; id < 64 * Bh; id++)
            if (id % Bh < PackConst.RegionBlocks) oracle.Matches(id, in blocks[At(id)]).Should().BeTrue($"block {id}");
    }

    [Fact]
    public void Map_Diffs_Are_Baked_Below_6000_And_Statics_At_Address_0_Survive()
    {
        WriteFacet();
        var blocks = ConvertFacet("4.0.11c", out var fm);

        blocks[At(7)].Land[0].TileId.Should().Be(0x77);
        blocks[At(7)].Land[63].Z.Should().Be(10);
        blocks[At(6)].Land[0].TileId.Should().Be(3);
        blocks[At(5)].Statics.Select(s => s.Graphic).Should().Equal((ushort)0x300);
        blocks[At(0)].Statics.Should().BeEmpty("stadifi entry -1 empties the block");

        var oracle = MulMapOracle.TryOpen(fm, Facet, Bw, Bh)!;
        (oracle.LandPatches, oracle.StaticPatches).Should().Be((1, 2));
        OracleAgrees(fm, blocks);
    }

    [Fact]
    public void Map_Diffs_Are_Not_Applied_From_7090_And_Base_Statics_At_Address_0_Are_Kept()
    {
        WriteFacet();
        var blocks = ConvertFacet("7.0.9.0", out _);

        blocks[At(7)].Land[0].TileId.Should().Be(3);
        // the first static block of statics4.mul starts at offset 0; the old "address != 0" test dropped it
        blocks[At(0)].Statics.Select(s => (s.Graphic, s.X, s.Y, s.Z, s.Hue)).Should().Equal(((ushort)0x100, (byte)1, (byte)2, (sbyte)5, (ushort)0), ((ushort)0x101, (byte)7, (byte)7, (sbyte)-5, (ushort)0x22));
        // ClassicUO's Chunk.Load filter: x > 7 is dropped
        blocks[At(5)].Statics.Select(s => s.Graphic).Should().Equal((ushort)0x200);
    }

    // finding 1: offset 0 with length 0xFFFFFFFF read unsigned became 1,024 records from the start of the file

    [Theory]
    [InlineData(0, 14, 14, true, 2)]       // offset 0 is valid
    [InlineData(7, 7, 14, true, 1)]
    [InlineData(0, -1, 7168, false, 0)]    // length 0xFFFFFFFF: negative, empty (was 1,024 records)
    [InlineData(-1, 0, 7168, false, 0)]    // offset 0xFFFFFFFF
    [InlineData(-1, 14, 7168, false, 0)]
    [InlineData(0, 0, 7168, false, 0)]
    [InlineData(0, 6, 7168, false, 0)]     // shorter than one record
    [InlineData(7, 14, 14, false, 0)]      // runs past the end of the file
    [InlineData(0, 7 * 2000, 7 * 2000, true, 1024)] // ClassicUO's cap, applied after the checks
    public void Static_Index_Entries_Are_Checked_Signed_And_In_Range_Before_The_Count(int offset, int length, long fileLength, bool ok, int count)
    {
        FacetTable.TryStaticRange(offset, length, fileLength, out int c).Should().Be(ok);
        c.Should().Be(count);
    }

    [Theory]
    [InlineData("4.0.11c")] // through stadifi as well
    [InlineData("7.0.9.0")] // base statics only
    public void Negative_Length_At_Offset_0_Is_An_Empty_Block_In_Staidx_And_Stadifi(string version)
    {
        WriteBaseFacet();
        // 1,100 records, so 1,024 records from offset 0 would be in range; block 9 = (0, 0xFFFFFFFF)
        var recs = Enumerable.Range(0, 1100).Select(i => ((ushort)(0x400 + i), (byte)1, (byte)1, (sbyte)0, (ushort)0)).ToArray();
        Write($"statics{Facet}.mul", Statics(recs));
        var idx = File.ReadAllBytes(Path.Combine(_uo, $"staidx{Facet}.mul"));
        Ints(0, 14, 0).CopyTo(idx, 0);
        Ints(0, -1, 0).CopyTo(idx, 9 * 12);
        Write($"staidx{Facet}.mul", idx);
        // stadif: block 0 replaced by (0, 0xFFFFFFFF) of a 1,100-record stadif, block 5 by a valid offset-0 entry
        Write($"stadifl{Facet}.mul", Ints(0, 5));
        Write($"stadifi{Facet}.mul", Ints(0, -1, 0, 0, 7, 0));
        Write($"stadif{Facet}.mul", Statics(recs));

        var blocks = ConvertFacet(version, out var fm);
        blocks[At(9)].Statics.Should().BeEmpty();
        if (version == "4.0.11c")
        {
            blocks[At(0)].Statics.Should().BeEmpty("the diff entry has a negative length");
            blocks[At(5)].Statics.Select(s => s.Graphic).Should().Equal((ushort)0x400);
        }
        else
        {
            blocks[At(0)].Statics.Select(s => s.Graphic).Should().Equal((ushort)0x400, (ushort)0x401);
        }

        OracleAgrees(fm, blocks);
    }

    // finding 2: the two diff families are independent, and deletions need no stadif payload

    [Fact]
    public void Land_Diffs_Alone_Are_Applied()
    {
        WriteBaseFacet();
        WriteLandDiffs();
        var blocks = ConvertFacet("4.0.11c", out var fm);
        blocks[At(7)].Land[0].TileId.Should().Be(0x77);
        blocks[At(5)].Statics.Select(s => s.Graphic).Should().Equal(new ushort[] { 0x200 }, "no statics diffs");
        OracleAgrees(fm, blocks);
    }

    [Fact]
    public void Static_Diffs_Alone_Are_Applied()
    {
        WriteBaseFacet();
        WriteStaticDiffs();
        var blocks = ConvertFacet("4.0.11c", out var fm);
        blocks[At(7)].Land[0].TileId.Should().Be(3, "no land diffs");
        blocks[At(5)].Statics.Select(s => s.Graphic).Should().Equal((ushort)0x300);
        blocks[At(0)].Statics.Should().BeEmpty();
        OracleAgrees(fm, blocks);
    }

    [Fact]
    public void Static_Diffs_That_Only_Delete_Are_Applied_With_An_Empty_Stadif()
    {
        WriteBaseFacet();
        Write($"stadifl{Facet}.mul", Ints(5, 0));
        Write($"stadifi{Facet}.mul", Ints(-1, 0, 0, 0, -1, 0));
        Write($"stadif{Facet}.mul", []);
        var blocks = ConvertFacet("4.0.11c", out var fm);
        blocks[At(5)].Statics.Should().BeEmpty();
        blocks[At(0)].Statics.Should().BeEmpty();
        MulMapOracle.TryOpen(fm, Facet, Bw, Bh)!.StaticPatches.Should().Be(2);
        OracleAgrees(fm, blocks);
    }

    // finding 3 is resolved by removing --xfiles (owner, 2026-10-31): x files are never read

    [Fact]
    public void X_Files_Are_Never_Read()
    {
        WriteFacet();
        WriteBaseFacet("x", tile: 9);
        var blocks = ConvertFacet("4.0.11c", out var fm);
        blocks[At(6)].Land[0].TileId.Should().Be(3, "map4.mul, not map4x.mul");
        blocks[At(7)].Land[0].TileId.Should().Be(0x77);
        blocks[At(5)].Statics.Select(s => s.Graphic).Should().Equal((ushort)0x300);
        var src = FacetSources.Resolve(fm, Facet)!;
        (Path.GetFileName(src.Map), Path.GetFileName(src.StaIdx), Path.GetFileName(src.Statics)).Should().Be(($"map{Facet}.mul", $"staidx{Facet}.mul", $"statics{Facet}.mul"));
        MulMapOracle.TryOpen(fm, Facet, Bw, Bh)!.MapFile.Should().Be($"map{Facet}.mul");
        SourceFacets.MapPath(fm, Facet, out _, out _).Should().EndWith($"map{Facet}.mul");
        OracleAgrees(fm, blocks);
    }

    // finding 4: the oracle selects its source the converter's way and stands aside for UOP

    [Fact]
    public void Oracle_Is_Not_Applicable_When_The_Converter_Reads_The_Uop_Map()
    {
        WriteBaseFacet();
        Write($"map{Facet}LegacyMUL.uop", [1, 2, 3]); // only its existence matters here
        Write("MainMisc.uop", []);                    // makes it a UOP installation (7.0+)
        var fm = new UOFileManager(Cv("7.0.15.1"), _uo);
        FacetSources.Resolve(fm, Facet)!.MapIsUop.Should().BeTrue();
        MulMapOracle.TryOpen(fm, Facet, Bw, Bh).Should().BeNull("a stale map4.mul next to the UOP map is not what was converted");
        SourceFacets.MapPath(fm, Facet, out bool uop, out _).Should().EndWith($"map{Facet}LegacyMUL.uop");
        uop.Should().BeTrue();

        File.Delete(Path.Combine(_uo, "MainMisc.uop")); // not a UOP installation: MUL, for both
        fm = new UOFileManager(Cv("7.0.15.1"), _uo);
        FacetSources.Resolve(fm, Facet)!.MapIsUop.Should().BeFalse();
        MulMapOracle.TryOpen(fm, Facet, Bw, Bh)!.MapFile.Should().Be($"map{Facet}.mul");
    }

    // finding 5: expected facets and sizes come from the source files, not from the pack

    [Fact]
    public void Verify_Fails_A_Pack_Narrower_Than_Its_Source_Map()
    {
        WriteBaseFacet(columns: Bw + 1); // the source holds 1456x1448; ClassicUO's table (and so the pack) says 1448
        var expected = SourceFacets.For(new UOFileManager(Cv("4.0.11c"), _uo), [Facet]).Single();
        (expected.Width, expected.Height, expected.Problem).Should().Be((1456, 1448, (string?)null));

        var failures = ConvertAndCheckMaps();
        failures.Should().Contain(f => f.Contains("pack 1448x1448") && f.Contains("holds 1456x1448"));

        WriteBaseFacet(); // the right size passes
        ConvertAndCheckMaps().Should().BeEmpty();
    }

    [Fact]
    public void Verify_Fails_A_Pack_Missing_A_Facet_The_Source_Has()
    {
        WriteBaseFacet();
        Write("map2.mul", new byte[2304 / 8 * 1600 / 8 * 196]);
        var fm = new UOFileManager(Cv("4.0.11c"), _uo);
        MapConvert.Facet(fm, Facet, _out);
        var manifest = new Manifest { Facets = [FacetFileIo.Read(Path.Combine(_out, "map", Facet.ToString()))] };
        WithFacets([2, Facet], () =>
        {
            Verify.Reset();
            Verify.CheckMaps(fm, _out, manifest, new()).Should().BeGreaterThan(0);
            Verify.Failures.Should().Contain(f => f.Contains("pack has no facet 2"));
        });
        WithFacets([Facet], () => // --facets 4: facet 2 is not expected
        {
            Verify.Reset();
            Verify.CheckMaps(fm, _out, manifest, new()).Should().Be(0);
        });
    }

    // Codex round 2, finding 1: a maps-only pack (no tiledata.bin, as --only maps permits) still gets every map check,
    // through Verify.Run itself.

    [Fact]
    public void Verify_Run_Checks_The_Maps_Of_A_Maps_Only_Pack_Without_Tiledata()
    {
        // too narrow: the source holds 1456x1448, the pack 1448x1448
        WriteBaseFacet(columns: Bw + 1);
        var fm = new UOFileManager(Cv("4.0.11c"), _uo);
        RunMapsOnly(fm, [Facet], [Facet]).Should().BeFalse();
        Verify.Failures.Should().Contain(f => f.Contains("pack 1448x1448") && f.Contains("holds 1456x1448"));

        // missing facet: the source has map2.mul, the pack has only facet 4
        WriteBaseFacet();
        Write("map2.mul", new byte[2304 / 8 * 1600 / 8 * 196]);
        fm = new UOFileManager(Cv("4.0.11c"), _uo);
        RunMapsOnly(fm, [Facet], [2, Facet]).Should().BeFalse();
        Verify.Failures.Should().Contain(f => f.Contains("pack has no facet 2"));

        // the right pack passes, also without tiledata.bin
        RunMapsOnly(fm, [Facet], [Facet]).Should().BeTrue(string.Join("; ", Verify.Failures));
        File.Exists(Path.Combine(_out, TableConst.TileDataFile)).Should().BeFalse();
    }

    /// <summary>Converts <paramref name="convert"/> into an empty pack with a manifest, then runs Verify.Run as
    /// <c>--only maps --facets ...</c> would.</summary>
    private bool RunMapsOnly(UOFileManager fm, int[] convert, HashSet<int> facets)
    {
        Directory.Delete(_out, recursive: true);
        Directory.CreateDirectory(_out);
        var manifest = new Manifest();
        foreach (int f in convert)
        {
            MapConvert.Facet(fm, f, _out);
            manifest.Facets.Add(FacetFileIo.Read(Path.Combine(_out, "map", f.ToString())));
        }

        ManifestFileIo.Write(Path.Combine(_out, "manifest.json"), manifest);
        var oldOnly = Verify.Only;
        Verify.Only = ["maps"];
        bool ok = false;
        try { WithFacets(facets, () => ok = Verify.Run(fm, _out, new())); }
        finally { Verify.Only = oldOnly; }
        return ok;
    }

    // Codex round 2, finding 2: the manifest's facet index chooses the source; a facet directory copied under another
    // index with its own facet.json must fail, even when its blocks would match that other facet's source.

    [Fact]
    public void Verify_Fails_A_Facet_Directory_Copied_Under_Another_Index()
    {
        // below 6.0 Trammel is map0 (no mapdif1 here), so a copied Felucca matches Trammel's source block for block
        const int columns = 64, w = columns * 8;
        var map0 = new byte[columns * (4096 / 8) * 196];
        for (int b = 0; b < columns * (4096 / 8); b++) Block(3, 0).CopyTo(map0, b * 196);
        Write("map0.mul", map0);
        var fm = new UOFileManager(Cv("4.0.11c"), _uo);
        fm.Maps.MapsDefaultSize[0, 0] = fm.Maps.MapsDefaultSize[1, 0] = w; // a 512-wide facet from the file size
        SourceFacets.For(fm, [0, 1]).Select(e => (e.Facet, e.Width, e.Height, e.Problem)).Should().Equal((0, w, 4096, (string?)null), (1, w, 4096, (string?)null));

        MapConvert.Facet(fm, 0, _out);
        string d0 = Path.Combine(_out, "map", "0"), d1 = Path.Combine(_out, "map", "1");
        Directory.CreateDirectory(d1);
        foreach (var file in Directory.GetFiles(d0)) File.Copy(file, Path.Combine(d1, Path.GetFileName(file)));
        var f0 = FacetFileIo.Read(d0);
        FacetFileIo.Read(d1).Index.Should().Be(0, "the copy keeps facet 0's metadata");
        var claimed = FacetFileIo.Read(d1);
        claimed.Index = 1; // the manifest says Trammel
        var manifest = new Manifest { Facets = [f0, claimed] };
        WithFacets([0, 1], () =>
        {
            Verify.Reset();
            Verify.CheckMaps(fm, _out, manifest, new()).Should().BeGreaterThan(0);
            Verify.Failures.Should().Contain(f => f.Contains("facet 1: facet.json says facet 0"));
        });

        // the real Trammel passes
        MapConvert.Facet(fm, 1, _out);
        manifest = new Manifest { Facets = [f0, FacetFileIo.Read(d1)] };
        WithFacets([0, 1], () =>
        {
            Verify.Reset();
            Verify.CheckMaps(fm, _out, manifest, new()).Should().Be(0, string.Join("; ", Verify.Failures));
        });
    }

    private List<string> ConvertAndCheckMaps()
    {
        var fm = new UOFileManager(Cv("4.0.11c"), _uo);
        MapConvert.Facet(fm, Facet, _out);
        var manifest = new Manifest { Facets = [FacetFileIo.Read(Path.Combine(_out, "map", Facet.ToString()))] };
        var failures = new List<string>();
        WithFacets([Facet], () =>
        {
            Verify.Reset();
            Verify.CheckMaps(fm, _out, manifest, new());
            failures.AddRange(Verify.Failures);
        });
        return failures;
    }

    private static void WithFacets(HashSet<int> facets, Action a)
    {
        var old = ConvertOptions.Facets;
        ConvertOptions.Facets = facets;
        try { a(); }
        finally { ConvertOptions.Facets = old; }
    }

    [LegacyUoFact]
    public void Verify_Fails_A_6144_Wide_Felucca_Built_From_The_7168_Wide_Uoml_Map0()
    {
        var uo = Environment.GetEnvironmentVariable("CUO_LEGACY_UO")!;
        File.Exists(Path.Combine(uo, "map0.mul")).Should().BeTrue($"CUO_LEGACY_UO ({uo}) is set, so it must hold a pre-UOP install's map0.mul");
        var fm = new UOFileManager(Cv("4.0.11c"), uo);
        fm.Maps.MapsDefaultSize[0, 0] = 6144; // what ClassicUO's version rule (below CV_4011D) produced before the fix
        int oldMax = ConvertOptions.MaxRegions;
        ConvertOptions.MaxRegions = 1;
        try
        {
            MapConvert.Facet(fm, 0, _out);
            var manifest = new Manifest { Facets = [FacetFileIo.Read(Path.Combine(_out, "map", "0"))] };
            WithFacets([0], () =>
            {
                Verify.Reset();
                Verify.CheckMaps(fm, _out, manifest, new()).Should().BeGreaterThan(0);
                Verify.Failures.Should().Contain(f => f.Contains("pack 6144x4096, the source map0.mul holds 7168x4096"));
            });

            ClientLoad.ApplyLegacyMapRules(fm); // the fix: 7168 from the file size
            fm.Maps.MapsDefaultSize[0, 0].Should().Be(7168);
            MapConvert.Facet(fm, 0, _out);
            manifest = new Manifest { Facets = [FacetFileIo.Read(Path.Combine(_out, "map", "0"))] };
            WithFacets([0], () =>
            {
                Verify.Reset();
                Verify.CheckMaps(fm, _out, manifest, new()).Should().Be(0, string.Join("; ", Verify.Failures));
            });
        }
        finally
        {
            ConvertOptions.MaxRegions = oldMax;
        }
    }

    // ModernUO parity: below 6.0 Trammel is map0 even when map1 exists

    [Fact]
    public void Trammel_Comes_From_Map0_Below_6000_Even_When_There_Is_A_Map1()
    {
        Write("map0.mul", []);
        MapConvert.AvailableFacets(new UOFileManager(Cv("4.0.11c"), _uo)).Should().Equal(0, 1);
        MapConvert.AvailableFacets(new UOFileManager(Cv("6.0.1.3"), _uo)).Should().Equal(0);

        Write("map1.mul", []);
        Write("staidx1.mul", []);
        Write("statics1.mul", []);
        var old = FacetSources.Resolve(new UOFileManager(Cv("4.0.11c"), _uo), 1)!;
        (old.FileIndex, Path.GetFileName(old.Map)).Should().Be((0, "map0.mul"));
        var v6 = FacetSources.Resolve(new UOFileManager(Cv("6.0.1.3"), _uo), 1)!;
        (v6.FileIndex, Path.GetFileName(v6.Map)).Should().Be((1, "map1.mul"));
        SourceFacets.MapPath(new UOFileManager(Cv("4.0.11c"), _uo), 1, out _, out int fi).Should().EndWith("map0.mul");
        fi.Should().Be(0);
    }

    [Fact]
    public void Trammel_Below_6000_Is_Map0_With_The_Facet_1_Diffs()
    {
        // map1 differs from map0 (tile 5 vs 3); below 6.0 the pack's Trammel is map0 + mapdif1, as ModernUO serves it
        int bh = 4096 / 8, bw = 64; // one region of a 7168-wide facet is enough
        var map0 = new byte[bw * bh * 196]; // only the first region's columns; the rest of the facet reads as empty
        for (int b = 0; b < bw * bh; b++) Block(3, 0).CopyTo(map0, b * 196);
        Write("map0.mul", map0);
        Write("map1.mul", map0.Select((v, i) => i % 196 == 4 ? (byte)5 : v).ToArray());
        Write("mapdifl1.mul", Ints(2));
        Write("mapdif1.mul", Block(0x66, 1));
        var fm = new UOFileManager(Cv("4.0.11c"), _uo);
        ClientLoad.ApplyLegacyMapRules(fm);
        int oldMax = ConvertOptions.MaxRegions;
        ConvertOptions.MaxRegions = 1;
        try { MapConvert.Facet(fm, 1, _out); }
        finally { ConvertOptions.MaxRegions = oldMax; }
        var (_, blocks) = RegionFile.Read(PackConst.RegionPath(Path.Combine(_out, "map", "1"), 0, 0));
        blocks[0].Land[0].TileId.Should().Be(3, "map0, not map1");
        blocks[2 * PackConst.RegionBlocks].Land[0].TileId.Should().Be(0x66, "mapdif1 on top"); // block 2 = (x 0, y 2)
    }

    // finding 7: a tiledata size that fits both layouts proves nothing

    [Fact]
    public void Tiledata_Size_That_Fits_Both_Layouts_Needs_An_Explicit_Version()
    {
        var path = Path.Combine(_uo, "tiledata.mul");
        File.WriteAllBytes(path, new byte[1_383_184]); // 804 old or 676 new static groups
        ClientLoad.TileDataSizeIsAmbiguous(1_383_184).Should().BeTrue();
        foreach (var v in new[] { "4.0.11c", "7.0.9.0" })
        {
            ClientLoad.CheckTileDataLayout(path, Cv(v)).Should().Contain("fits both").And.Contain("--client-version");
            ClientLoad.CheckTileDataLayout(path, Cv(v), versionExplicit: true).Should().BeNull();
        }

        // stock sizes fit exactly one layout and pass with the matching version
        foreach (var (size, v) in new[] { (1_036_288L, "4.0.11c"), (1_644_544L, "7.0.0.0"), (3_188_736L, "7.0.9.0") })
        {
            ClientLoad.TileDataSizeIsAmbiguous(size).Should().BeFalse($"{size}");
            File.WriteAllBytes(path, new byte[size]);
            ClientLoad.CheckTileDataLayout(path, Cv(v)).Should().BeNull($"{size} with {v}");
        }
    }
}

/// <summary>Runs when CUO_LEGACY_UO is set (a pre-UOP install, read-only); skipped only while it is unset. A set path
/// without map0.mul fails the test rather than skipping it.</summary>
public sealed class LegacyUoFactAttribute : FactAttribute
{
    public LegacyUoFactAttribute()
    {
        var uo = Environment.GetEnvironmentVariable("CUO_LEGACY_UO");
        if (string.IsNullOrEmpty(uo))
            Skip = "CUO_LEGACY_UO is not set (a pre-UOP install with map0.mul)";
    }
}
