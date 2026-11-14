using System.Text;
using ClassicUO.Assets;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Converter;
using Uo.Pack;
using Xunit;

namespace Uo.Converter.Tests;

// The last UO-folder files (pack-format.md "Skills, skill groups, multimap, string dictionary", "Loose text files"):
// converted from a synthetic install, verified EXACT, and absent in the pack when the install lacks them.
public sealed class MiscConvertTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"uo_misc_conv_{Guid.NewGuid():N}");
    private readonly string _uo, _out;

    public MiscConvertTests()
    {
        _uo = Path.Combine(_dir, "uo");
        _out = Path.Combine(_dir, "pack");
        Directory.CreateDirectory(_uo);
        Directory.CreateDirectory(_out);
        Verify.Reset();
    }

    public void Dispose()
    {
        Verify.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private UOFileManager Fm() => new(ClientVersion.CV_7090, _uo);

    private void WriteSkills()
    {
        using var mul = new BinaryWriter(File.Create(Path.Combine(_uo, "skills.mul")));
        using var idx = new BinaryWriter(File.Create(Path.Combine(_uo, "Skills.idx")));
        foreach (var (action, name) in new[] { (true, "Alchemy"), (false, "Anatomy"), (true, "Animal Lore") })
        {
            var b = Encoding.ASCII.GetBytes(name + "\0");
            idx.Write((int)mul.BaseStream.Position); idx.Write(1 + b.Length); idx.Write(0);
            mul.Write((byte)(action ? 1 : 0));
            mul.Write(b);
        }
    }

    // skillgrp.mul: count, (count − 1) names of 17 bytes (34 when unicode, after a −1 marker), then one i32 per skill
    private void WriteSkillGroups(bool unicode, string[] names, int[] groups, int truncate = 0)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            if (unicode) w.Write(-1);
            w.Write(names.Length + 1);
            foreach (var n in names)
            {
                var slot = new byte[unicode ? 34 : 17];
                var b = unicode ? Encoding.Unicode.GetBytes(n) : Encoding.ASCII.GetBytes(n);
                b.CopyTo(slot, 0);
                w.Write(slot);
            }
            foreach (int g in groups) w.Write(g);
        }
        File.WriteAllBytes(Path.Combine(_uo, "skillgrp.mul"), ms.ToArray()[..^truncate]);
    }

    private string P(string rel) => Path.Combine(_out, rel);

    private void VerifyAll(UOFileManager fm)
    {
        MiscConvert.VerifySkills(fm, _out);
        MiscConvert.VerifyMultiMap(fm, _out);
        MiscConvert.VerifyStringDict(fm, _out);
        MiscConvert.VerifyLoose(fm, _out);
        Verify.Failures.Should().BeEmpty();
    }

    private void ConvertAll(UOFileManager fm)
    {
        MiscConvert.Skills(fm, _out);
        MiscConvert.MultiMap(fm, _out);
        MiscConvert.StringDict(fm, _out);
        MiscConvert.Loose(fm, _out);
    }

    [Fact]
    public void A_Full_Install_Converts_And_Verifies_Exact()
    {
        WriteSkills();
        WriteSkillGroups(unicode: true, new[] { "Kampf", "Magie" }, new[] { 1, 2, 0 });
        File.WriteAllBytes(Path.Combine(_uo, "Multimap.rle"), new byte[] { 1, 0, 0, 0, 1, 0, 0, 0, 0x81 });
        File.WriteAllBytes(Path.Combine(_uo, "facet00.mul"), new byte[40000]);
        File.WriteAllText(Path.Combine(_uo, "Prof.txt"), "Begin\nName Warrior\nEnd\n");
        File.WriteAllBytes(Path.Combine(_uo, "Citytext.enu"), [0, 1, 2, 3, 0xFF]);

        using var fm = Fm();
        ConvertAll(fm);
        VerifyAll(fm);

        StringListFile.Read(P(MiscConst.SkillsFile), MiscConst.MagicSkills).Should().Equal((1u, "Alchemy"), (0u, "Anatomy"), (1u, "Animal Lore"));
        SkillGroupsFile.Read(P(MiscConst.SkillGroupsFile)).Names.Should().Equal("Kampf", "Magie");
        using (var store = MiscConst.OpenMultiMapStore(P(MiscConst.MultiMapFile)))
        {
            store.SlotCount.Should().Be(2);
            store.TryGet(1, out _, out var rec).Should().BeTrue();
            rec.Codec.Should().Be(StoreConst.CodecBrotli); // 40000 zero bytes compress
        }
        File.ReadAllBytes(P("loose/citytext.enu")).Should().Equal(0, 1, 2, 3, 0xFF);
        File.Exists(P("loose/prof.txt")).Should().BeTrue();
        File.Exists(P("loose/stitchin.def")).Should().BeFalse();
        File.Exists(P(MiscConst.StringDictFile)).Should().BeFalse();
    }

    [Fact]
    public void An_Install_Without_The_Optional_Files_Leaves_No_Tables_And_Removes_Stale_Ones()
    {
        WriteSkills();
        WriteSkillGroups(unicode: false, new[] { "Combat" }, new[] { 1, 0, 1 });
        File.WriteAllBytes(Path.Combine(_uo, "Multimap.rle"), new byte[] { 1, 0, 0, 0, 1, 0, 0, 0, 0x81 });
        File.WriteAllText(Path.Combine(_uo, "walls.txt"), "x");
        using (var fm = Fm()) ConvertAll(fm);
        File.Exists(P(MiscConst.SkillGroupsFile)).Should().BeTrue();

        // the same output from an install with only the skills (a fresh folder: the frozen loaders keep their files mapped)
        var bare = Path.Combine(_dir, "bare");
        Directory.CreateDirectory(bare);
        foreach (var f in new[] { "skills.mul", "Skills.idx" }) File.Copy(Path.Combine(_uo, f), Path.Combine(bare, f));
        using var fm2 = new UOFileManager(ClientVersion.CV_7090, bare);
        ConvertAll(fm2);
        VerifyAll(fm2);
        File.Exists(P(MiscConst.SkillsFile)).Should().BeTrue();
        foreach (var f in new[] { MiscConst.SkillGroupsFile, MiscConst.MultiMapFile, "loose/walls.txt" })
            File.Exists(P(f)).Should().BeFalse(f);
    }

    [Fact]
    public void Skills_Are_Required()
    {
        using var fm = Fm();
        ((Action)(() => MiscConvert.Skills(fm, _out))).Should().Throw<FileNotFoundException>();
        MiscConvert.VerifySkills(fm, _out);
        Verify.Failures.Should().Contain(f => f.Contains("requires skills.bin"));
    }

    [Fact]
    public void A_Malformed_Skillgrp_Mul_Has_No_Table_As_The_Client_Used_Its_Defaults()
    {
        WriteSkills();
        WriteSkillGroups(unicode: false, new[] { "Combat" }, new[] { 1, 0 }, truncate: 2);
        using var fm = Fm();
        MiscConvert.Skills(fm, _out);
        File.Exists(P(MiscConst.SkillGroupsFile)).Should().BeFalse();
        MiscConvert.VerifySkills(fm, _out);
        Verify.Failures.Should().BeEmpty();
    }

    [Fact]
    public void Verify_Catches_A_Changed_Loose_File()
    {
        File.WriteAllText(Path.Combine(_uo, "roof.txt"), "a");
        using var fm = Fm();
        MiscConvert.Loose(fm, _out);
        File.WriteAllText(Path.Combine(_uo, "roof.txt"), "b");
        MiscConvert.VerifyLoose(fm, _out);
        Verify.Failures.Should().ContainSingle(f => f.Contains("roof.txt"));
    }
}
