using System.Text;
using FluentAssertions;
using Uo.Converter;
using Uo.Pack;
using Xunit;

namespace Uo.Converter.Tests;

// The shard's Clilocs.txt folded into text/cliloc.bin (data-formats.md, text section): from the install folder, from
// --cliloc-overrides' file (which wins), left out by --no-cliloc-overrides.
public sealed class ClilocOverridesFoldTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"uo_cliloc_fold_{Guid.NewGuid():N}");
    private readonly string _uo, _out;

    public ClilocOverridesFoldTests()
    {
        _uo = Path.Combine(_dir, "uo");
        _out = Path.Combine(_dir, "pack");
        Directory.CreateDirectory(_uo);
        Directory.CreateDirectory(_out);
        File.WriteAllBytes(Path.Combine(_uo, "Cliloc.enu"), RawCliloc((1, "one"), (2, "two")));
    }

    public void Dispose()
    {
        TextConvert.ClilocOverridesPath = null;
        TextConvert.ClilocOverridesOff = false;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // a raw (uncompressed) Cliloc.* file: 4 + 2 header bytes, then number, flag, length, text per entry
    private static byte[] RawCliloc(params (int Id, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(2); w.Write((short)1);
        foreach (var (id, text) in entries)
        {
            var b = Encoding.UTF8.GetBytes(text);
            w.Write(id); w.Write((byte)0); w.Write((short)b.Length); w.Write(b);
        }
        w.Flush();
        return ms.ToArray();
    }

    private ClilocTable? Overrides()
    {
        TextConvert.Cliloc(_uo, _out);
        using var store = RecordStore.Open(Path.Combine(_out, TextConst.ClilocFile), TextConst.MagicCliloc);
        var dir = ClilocStore.Resolve(store);
        if (dir.Overrides < 0) return null;
        var t = ClilocStore.ReadTable(store, dir.Overrides, out var why);
        t.Should().NotBeNull(why);
        return t;
    }

    [Fact]
    public void Folder_Clilocs_Txt_Becomes_The_Overrides_Record()
    {
        File.WriteAllText(Path.Combine(_uo, "clilocs.txt"), "# ours\n3100001\tBreed\n2 two!\n");
        var t = Overrides()!;
        t.Count.Should().Be(2);
        t.TryGetString(3100001, out var s).Should().BeTrue();
        s.Should().Be("Breed");
        t.TryGetString(2, out s).Should().BeTrue();
        s.Should().Be("two!");
    }

    [Fact]
    public void No_Clilocs_Txt_Means_No_Overrides_Record()
    {
        Overrides().Should().BeNull();
    }

    [Fact]
    public void Explicit_File_Wins_Over_The_Folders()
    {
        File.WriteAllText(Path.Combine(_uo, "Clilocs.txt"), "5 folder\n");
        var explicitFile = Path.Combine(_dir, "shard", "Clilocs.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(explicitFile)!);
        File.WriteAllText(explicitFile, "6 explicit\n");
        TextConvert.ClilocOverridesPath = explicitFile;
        var t = Overrides()!;
        t.Count.Should().Be(1);
        t.TryGetString(6, out var s).Should().BeTrue();
        s.Should().Be("explicit");
        t.TryGetString(5, out _).Should().BeFalse();
    }

    [Fact]
    public void Explicit_File_Is_Folded_When_The_Folder_Has_None()
    {
        var explicitFile = Path.Combine(_dir, "Clilocs.txt");
        File.WriteAllText(explicitFile, "7 seven\n");
        TextConvert.ClilocOverridesPath = explicitFile;
        Overrides()!.Count.Should().Be(1);
    }

    [Fact]
    public void Missing_Explicit_File_Is_An_Error()
    {
        TextConvert.ClilocOverridesPath = Path.Combine(_dir, "nope.txt");
        Action a = () => TextConvert.Cliloc(_uo, _out);
        a.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void No_Cliloc_Overrides_Leaves_Both_Out()
    {
        File.WriteAllText(Path.Combine(_uo, "Clilocs.txt"), "5 folder\n");
        TextConvert.ClilocOverridesOff = true;
        Overrides().Should().BeNull();
    }
}
