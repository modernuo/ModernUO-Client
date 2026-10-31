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

        // ClassicUO's parser maps the letter from 'a' = 0, so "4.0.11c" is 4.0.11.2 (ModernUO's ClientVersion says .3)
        ((uint)Cv("4.0.11c")).Should().Be(0x04000B02u);
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
        statics.Length.Should().Be(512 * 32); // 0x4000 statics in the pre-7.0.9 file, as UOML has
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

    private void WriteFacet()
    {
        // map0 only has to exist (MapLoader.Load reads its length); facet 4 holds the data
        File.WriteAllBytes(Path.Combine(_uo, "map0.mul"), Block(3, 0));
        File.WriteAllBytes(Path.Combine(_uo, "staidx0.mul"), Ints(-1, 0, 0));
        File.WriteAllBytes(Path.Combine(_uo, "statics0.mul"), []);

        var map = new byte[Bw * Bh * 196];
        for (int b = 0; b < Bw * Bh; b++) Block(3, 0).CopyTo(map, b * 196);
        File.WriteAllBytes(Path.Combine(_uo, $"map{Facet}.mul"), map);

        // block 0's statics start at offset 0 of statics4.mul (a valid address), block 5 has two more
        var sta = Statics((0x100, 1, 2, 5, 0), (0x101, 7, 7, -5, 0x22), (0x200, 3, 3, 0, 0), (0x201, 9, 0, 0, 0));
        var idx = new byte[Bw * Bh * 12];
        for (int b = 0; b < Bw * Bh; b++) Ints(-1, 0, 0).CopyTo(idx, b * 12);
        Ints(0, 14, 0).CopyTo(idx, 0);
        Ints(14, 14, 0).CopyTo(idx, 5 * 12);
        File.WriteAllBytes(Path.Combine(_uo, $"staidx{Facet}.mul"), idx);
        File.WriteAllBytes(Path.Combine(_uo, $"statics{Facet}.mul"), sta);

        // land diff: block 7; statics diffs: block 5 replaced by the first record of stadif4 (offset 0), block 0 emptied
        File.WriteAllBytes(Path.Combine(_uo, $"mapdifl{Facet}.mul"), Ints(7));
        File.WriteAllBytes(Path.Combine(_uo, $"mapdif{Facet}.mul"), Block(0x77, 10));
        File.WriteAllBytes(Path.Combine(_uo, $"stadifl{Facet}.mul"), Ints(5, 0));
        File.WriteAllBytes(Path.Combine(_uo, $"stadifi{Facet}.mul"), Ints(0, 7, 0, -1, 0, 0));
        File.WriteAllBytes(Path.Combine(_uo, $"stadif{Facet}.mul"), Statics((0x300, 0, 0, 1, 0)));
    }

    private BlockData[] ConvertFacet(string version, out UOFileManager fm)
    {
        fm = new UOFileManager(Cv(version), _uo);
        fm.Maps.Load();
        ClientLoad.ApplyLegacyMapRules(fm);
        MapConvert.Facet(fm, Facet, _out, useXFiles: false);
        var (h, blocks) = RegionFile.Read(PackConst.RegionPath(Path.Combine(_out, "map", Facet.ToString()), 0, 0));
        (h.BlocksW, h.BlocksH).Should().Be(((ushort)PackConst.RegionBlocks, (ushort)PackConst.RegionBlocks));
        return blocks;
    }

    private static int At(int blockId) => blockId % Bh * PackConst.RegionBlocks + blockId / Bh; // column-major id -> region row-major

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
        for (int id = 0; id < 64 * Bh && id / Bh < PackConst.RegionBlocks; id++)
            if (id % Bh < PackConst.RegionBlocks) oracle.Matches(id, in blocks[At(id)]).Should().BeTrue($"block {id}");
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

    [Fact]
    public void Trammel_Comes_From_Map0_Below_6000_When_There_Is_No_Map1()
    {
        File.WriteAllBytes(Path.Combine(_uo, "map0.mul"), []);
        MapConvert.AvailableFacets(new UOFileManager(Cv("4.0.11c"), _uo)).Should().Equal(0, 1);
        MapConvert.AvailableFacets(new UOFileManager(Cv("6.0.1.3"), _uo)).Should().Equal(0);
    }
}
