using ClassicUO.Assets;
using ClassicUO.IO;
using FluentAssertions;
using Uo.Converter;
using Xunit;

namespace Uo.Converter.Tests;

// Upstream d4a188976 trims whitespace before a hex parse. The import's text inputs: mobtypes.txt flags, def files
// (DefReader: a "0x" token read as 0 before, a "0x" group entry was dropped, a tab inside a group dropped the entry) and
// the --bodies list.
public sealed class HexTextInputTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"uo_hex_text_{Guid.NewGuid():N}");

    public HexTextInputTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("400\tHUMAN\t0", 400, "human", 0u)]
    [InlineData("  200 MONSTER 1A  ", 200, "monster", 0x1Au)]
    [InlineData("200\tANIMAL\t10000\t# a comment", 200, "animal", 0x10000u)]
    [InlineData("200 ANIMAL 1A#glued comment", 200, "animal", 0x1Au)]
    [InlineData("200 SEA_MONSTER 0#x", 200, "sea_monster", 0u)]
    public void Mobtypes_Lines_Parse_With_Whitespace_And_Comments(string line, int id, string type, uint flags)
    {
        AnimationsLoader.TryParseMobTypeLine(line, out int i, out string t, out uint f).Should().BeTrue();
        i.Should().Be(id);
        t.Should().Be(type);
        f.Should().Be(flags);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# 200 HUMAN 0")]
    [InlineData("200 HUMAN")]
    [InlineData("200 HUMAN #0")]
    [InlineData("200 HUMAN zz")]
    public void Mobtypes_Comment_Short_Or_Malformed_Lines_Are_Skipped(string line) =>
        AnimationsLoader.TryParseMobTypeLine(line, out _, out _, out _).Should().BeFalse();

    private DefReader Def(string text, int minSize = 2)
    {
        string path = Path.Combine(_dir, $"{Guid.NewGuid():N}.def");
        File.WriteAllText(path, text);

        return new DefReader(path, minSize);
    }

    [Fact]
    public void Def_Ints_Read_Hex_And_Decimal()
    {
        using DefReader r = Def("0x1A 26\n12 0X0c\n");

        r.Next().Should().BeTrue();
        r.ReadInt().Should().Be(0x1A);
        r.ReadInt().Should().Be(26);
        r.Next().Should().BeTrue();
        r.ReadInt().Should().Be(12);
        r.ReadInt().Should().Be(0x0C);
    }

    [Fact]
    public void Def_Groups_Read_Hex_Entries_And_Trim_Tabs()
    {
        using DefReader r = Def("100 {0x190, 401,\t402} 3\n");

        r.Next().Should().BeTrue();
        r.ReadInt().Should().Be(100);
        r.ReadGroup().Should().Equal(0x190, 401, 402);
        r.ReadInt().Should().Be(3);
    }

    [Fact]
    public void Def_Group_Int_Reads_Hex_And_Decimal()
    {
        using DefReader hex = Def("100 {0x190} 3\n");
        hex.Next().Should().BeTrue();
        hex.ReadInt().Should().Be(100);
        hex.ReadGroupInt().Should().Be(0x190);

        using DefReader dec = Def("100 {400} 3\n");
        dec.Next().Should().BeTrue();
        dec.ReadInt().Should().Be(100);
        dec.ReadGroupInt().Should().Be(400);
    }

    [Theory]
    [InlineData(" 0x190", 0x190)]
    [InlineData("0x191 ", 0x191)]
    [InlineData(" 400 ", 400)]
    public void Body_Ids_Are_Trimmed(string text, int id) => BodyChoices.ParseId(text, "--bodies").Should().Be(id);
}
