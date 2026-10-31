using ClassicUO.Assets;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Converter;
using Uo.Pack;
using Xunit;

namespace Uo.Converter.Tests;

/// <summary>bodies.json parsing and the pure resolution step, on synthetic data.</summary>
public sealed class BodyChoicesParseTests
{
    private static BodyPlan.Column C(int col, int g, int mh = 0) => new(col, (ushort)g, (sbyte)mh);

    // 100: remodel with col1 only; 101: remodel with col1 and col3; 102: remodel col2 only;
    // 200: addition col1 with a Body.def stand-in; 201: addition col1 without one; 300: UOP.
    private static BodyPlan.SourceData Data() => new()
    {
        BodyCount = 2048,
        Columns = new()
        {
            [100] = [C(1, 7)], [101] = [C(1, 8), C(3, 9)], [102] = [C(2, 10)],
            [200] = [C(1, 11)], [201] = [C(1, 12)], [300] = [C(4, 13)],
        },
        OwnArt = [100, 101, 102], BodyDef = [200], Uop = [300],
    };

    private static BodyPlan R(string json) => BodyPlan.Resolve(Data(), BodyChoices.Parse(json));

    private static int? Target(BodyPlan p, int body) => p.Conv.TryGetValue(body, out var c) ? c.Graphic : null;

    [Fact]
    public void Presets_Pick_Columns_Per_Body()
    {
        var aos = R("{}");
        (Target(aos, 100), Target(aos, 101), Target(aos, 102), Target(aos, 200), Target(aos, 201)).Should().Be((7, 9, 10, 11, 12));
        foreach (var p in new[] { "t2a", "uor" })
        {
            var t = R($"{{\"preset\":\"{p}\"}}");
            (Target(t, 100), Target(t, 101), Target(t, 102)).Should().Be(((int?)null, (int?)null, (int?)null));
            (Target(t, 200), Target(t, 201)).Should().Be((11, 12), "additions keep their converted art by default");
        }
        var lbr = R("{\"preset\":\"lbr\"}");
        (Target(lbr, 100), Target(lbr, 101), Target(lbr, 102)).Should().Be((7, 8, (int?)null), "lbr: the column-1 art, other remodels old");
        var force = R("{\"forceOldAnimations\":true}");
        (Target(force, 100), Target(force, 101), Target(force, 102)).Should().Be(((int?)null, (int?)null, 10), "every column-1 remodel old, the rest as aos");
        (Target(force, 200), Target(force, 201)).Should().Be((11, 12));
        Target(aos, 300).Should().Be(13, "UOP bodies are never remodels; their (unused) conversion is kept as today");
    }

    [Fact]
    public void Standins_Apply_Only_Where_The_Preset_Turns_The_Conversion_Off()
    {
        var t = R("{\"preset\":\"t2a\",\"additions\":\"standins\"}");
        Target(t, 200).Should().BeNull("an addition with a Body.def line draws the stand-in");
        Target(t, 201).Should().Be(12, "an addition with no stand-in keeps its art (nothing turns invisible)");
        Target(R("{\"additions\":\"standins\"}"), 200).Should().Be(11, "aos enables every column");
        Target(R("{\"forceOldAnimations\":true,\"additions\":\"standins\"}"), 200).Should().BeNull();
    }

    [Fact]
    public void Remodel_Overrides_Win_Over_The_Preset()
    {
        var p = R("{\"preset\":\"t2a\",\"remodels\":{\"100\":\"new\",\"0x65\":\"new\"}}");
        (Target(p, 100), Target(p, 101), Target(p, 102)).Should().Be((7, 9, (int?)null), "\"new\" is the last column");
        Target(R("{\"remodels\":{\"102\":\"old\"}}"), 102).Should().BeNull();
    }

    [Fact]
    public void Extras_Get_The_Variant_And_The_Referencing_Bodys_Mount_Height()
    {
        var d = Data();
        d.Columns[103] = [C(1, 204, -9)];
        var p = BodyPlan.Resolve(d, BodyChoices.Parse("{\"extras\":[{\"id\":1500,\"from\":\"anim.mul:100\"},{\"id\":\"0x1000\",\"from\":\"anim2.mul:204\"}]}"));
        p.Conv[1500].Should().Be((0, (ushort)100, (sbyte)0));
        p.ExtraMobTypeFrom[1500].Should().Be(100);
        p.Conv[0x1000].Should().Be((1, (ushort)204, (sbyte)-9));
        p.ExtraMobTypeFrom[0x1000].Should().Be(103);
        p.BodyCount.Should().Be(0x1001);
    }

    [Theory]
    [InlineData("[]", "top level must be an object")]
    [InlineData("{\"preset\":", "not valid JSON")]
    [InlineData("{\"prest\":\"aos\"}", "unknown key \"prest\"")]
    [InlineData("{\"preset\":\"se\"}", "\"preset\" must be one of")]
    [InlineData("{\"preset\":\"aos\",\"preset\":\"t2a\"}", "given twice")]
    [InlineData("{\"additions\":\"oldest\"}", "\"additions\" must be one of")]
    [InlineData("{\"forceOldAnimations\":\"yes\"}", "true or false")]
    [InlineData("{\"remodels\":{\"76\":\"older\"}}", "must be \"old\" or \"new\"")]
    [InlineData("{\"remodels\":{\"dragon\":\"old\"}}", "not a body id")]
    [InlineData("{\"remodels\":{\"65535\":\"old\"}}", "not a body id")]
    [InlineData("{\"remodels\":{\"76\":\"old\",\"0x4C\":\"new\"}}", "lists body 76 twice")]
    [InlineData("{\"extras\":{}}", "must be an array")]
    [InlineData("{\"extras\":[{\"id\":1500}]}", "needs both")]
    [InlineData("{\"extras\":[{\"id\":1500,\"from\":\"anim.mul:76\",\"hue\":5}]}", "unknown key \"hue\"")]
    [InlineData("{\"extras\":[{\"id\":1500,\"from\":\"AnimationFrame1.uop:713\"}]}", "UOP bodies cannot be copied")]
    [InlineData("{\"extras\":[{\"id\":1500,\"from\":\"anim1.mul:76\"}]}", "must be <file>:<body>")]
    [InlineData("{\"extras\":[{\"id\":1500,\"from\":\"anim.mul:x\"}]}", "not a body id")]
    [InlineData("{\"extras\":[{\"id\":1500,\"from\":\"anim.mul:76\"},{\"id\":1500,\"from\":\"anim.mul:10\"}]}", "id 1500 more than once")]
    public void Bad_Input_Fails_With_A_Clear_Error(string json, string message)
    {
        var act = () => BodyChoices.Parse(json);
        act.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain(message);
    }

    [Theory]
    [InlineData("{\"remodels\":{\"200\":\"old\"}}", "body 200 has no anim.mul art of its own, so it is an addition")]
    [InlineData("{\"remodels\":{\"5\":\"old\"}}", "body 5 has no Bodyconv.def conversion")]
    [InlineData("{\"remodels\":{\"300\":\"old\"}}", "UOP frames")]
    [InlineData("{\"extras\":[{\"id\":42,\"from\":\"anim.mul:100\"}]}", "id 42 is already in use (mobtypes.txt lists it)")]
    public void Ids_That_Do_Not_Fit_Fail_With_A_Clear_Error(string json, string message)
    {
        var d = Data();
        d.InUse[42] = "mobtypes.txt lists it";
        var act = () => BodyPlan.Resolve(d, BodyChoices.Parse(json));
        act.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain(message);
    }

    [Fact]
    public void Manifest_Record_Round_Trips()
    {
        var c = BodyChoices.Parse("{\"preset\":\"lbr\",\"forceOldAnimations\":true,\"additions\":\"standins\",\"remodels\":{\"76\":\"old\"},\"extras\":[{\"id\":1500,\"from\":\"anim2.mul:7\"}]}");
        var back = BodyChoices.FromRecord(c.ToRecord());
        (back.Preset, back.ForceOldAnimations, back.Additions).Should().Be(("lbr", true, "standins"));
        back.Remodels.Should().Equal(new Dictionary<int, bool> { [76] = false });
        back.Extras.Should().Equal(new BodyExtra(1500, 1, 7));
        BodyChoices.FromRecord(null).Preset.Should().Be("aos");
    }
}

/// <summary>The listed bodies on the real 7.0.103 install (read only), under each preset.</summary>
[Collection("ClassicUo")]
public sealed class BodyChoicesInstallTests
{
    internal static UOFileManager Fm()
    {
        ClientVersionHelper.IsClientVersionValid("7.0.103.0", out var cv);
        return new UOFileManager(cv, ClassicUoFactAttribute.Dir);
    }

    private static BodyPlan Plan(string json) => BodyPlan.Build(Fm(), BodyChoices.Parse(json));

    private static (int File, int Graphic, int Mh)? T(BodyPlan p, int body) => p.Conv.TryGetValue(body, out var c) ? (c.FileIndex, c.Graphic, c.MountHeight) : null;

    // 10 summoned daemon, 76 Titan, 791 mount, 9 daemon (built-in reverted line): remodels, all column 1.
    // 11 Dread Spider, 157 Giant Black Widow, 43: additions (43 also has a Body.def stand-in, 9 hue 1152).
    [ClassicUoFact]
    public void Aos_Default_Is_Every_Conversion_Plus_The_Three_Reverted_Lines()
    {
        var p = Plan("{}");
        T(p, 10).Should().Be((1, 7, 0));
        T(p, 11).Should().Be((1, 3, 0));
        T(p, 76).Should().Be((1, 27, 0));
        T(p, 157).Should().Be((1, 1, 0));
        T(p, 791).Should().Be((1, 203, 0));
        T(p, 793).Should().Be((1, 204, -9), "mount heights follow the chosen column");
        T(p, 9).Should().Be((1, 7, 0));
        T(p, 26).Should().Be((1, 24, 0));
        T(p, 28).Should().Be((1, 5, 0));
        T(p, 43).Should().Be((1, 11, 0));
        p.RemodelIds.Should().Contain([9, 10, 26, 28, 76, 318, 319, 791, 793]);
        p.OldRemodelIds.Should().BeEmpty();
    }

    [ClassicUoFact]
    public void T2a_And_Uor_Turn_Every_Remodel_Old_And_Keep_Additions()
    {
        foreach (var preset in new[] { "t2a", "uor" })
        {
            var p = Plan($"{{\"preset\":\"{preset}\"}}");
            foreach (var b in new[] { 9, 10, 26, 28, 76, 318, 791, 793 }) T(p, b).Should().BeNull($"{preset}: remodel {b} draws its anim.mul art");
            T(p, 11).Should().Be((1, 3, 0));
            T(p, 157).Should().Be((1, 1, 0));
            T(p, 43).Should().Be((1, 11, 0));
            p.OldRemodelIds.Should().BeEquivalentTo(p.RemodelIds);
        }
    }

    [ClassicUoFact]
    public void Lbr_Keeps_Column_One_Remodels_And_ForceOld_Turns_Them_Off()
    {
        var lbr = Plan("{\"preset\":\"lbr\"}");
        T(lbr, 10).Should().Be((1, 7, 0));
        T(lbr, 9).Should().Be((1, 7, 0));
        T(lbr, 791).Should().Be((1, 203, 0));
        T(lbr, 318).Should().BeNull("Demon Knight is a column-2 (AOS) remodel");
        var force = Plan("{\"forceOldAnimations\":true}");
        foreach (var b in new[] { 9, 10, 26, 28, 76, 791 }) T(force, b).Should().BeNull();
        T(force, 318).Should().Be((2, 318, 0));
        T(force, 11).Should().Be((1, 3, 0), "additions keep their art: nothing turns invisible");
    }

    [ClassicUoFact]
    public void Standins_Draw_The_Body_Def_Body()
    {
        var p = Plan("{\"preset\":\"t2a\",\"additions\":\"standins\"}");
        T(p, 11).Should().BeNull();
        T(p, 157).Should().BeNull();
        T(p, 43).Should().BeNull();
        Plan("{\"additions\":\"standins\"}").Conv[11].Should().Be((1, (ushort)3, (sbyte)0));
    }

    [ClassicUoFact]
    public void Overrides_And_Extras()
    {
        var p = Plan("{\"remodels\":{\"76\":\"old\"},\"extras\":[{\"id\":2000,\"from\":\"anim.mul:76\"},{\"id\":2001,\"from\":\"anim2.mul:7\"}]}");
        T(p, 76).Should().BeNull();
        T(p, 10).Should().Be((1, 7, 0));
        T(p, 2000).Should().Be((0, 76, 0));
        p.ExtraMobTypeFrom[2000].Should().Be(76);
        T(p, 2001).Should().Be((1, 7, 0));
        p.ExtraMobTypeFrom[2001].Should().Be(9, "the lowest body that converts to anim2 7 (9, by the built-in line)");
        T(Plan("{\"preset\":\"t2a\",\"remodels\":{\"9\":\"new\"}}"), 9).Should().Be((1, 7, 0));
    }

    [ClassicUoFact]
    public void Bad_Ids_On_Real_Data()
    {
        Action a = () => Plan("{\"extras\":[{\"id\":76,\"from\":\"anim.mul:10\"}]}");
        a.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain("id 76 is already in use");
        a = () => Plan("{\"extras\":[{\"id\":2000,\"from\":\"anim.mul:2002\"}]}");
        a.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain("anim.mul:2002 has no frames");
        a = () => Plan("{\"remodels\":{\"11\":\"old\"}}");
        a.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain("is an addition");
        a = () => Plan("{\"remodels\":{\"200\":\"old\"}}");
        a.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain("no Bodyconv.def conversion");
    }

    [ClassicUoFact]
    public void A_Converted_Body_Draws_The_Targets_AnimationSequence_Groups()
    {
        // AnimationSequence.uop: 9 replaces 13->12 and 20->19; anim2 body 7 replaces 12->4, 13->4 and 20->1.
        var fm = Fm();
        var plan = BodyPlan.Build(fm, BodyChoices.Default);
        using var w = new AnimWorker(fm, plan);
        var m = AnimConvert.Meta(w.Loader, fm.Version, 9, plan.Conv);
        (m.FileIndex, m.SourceBody, m.SeqBody).Should().Be((1, (ushort)7, 7));
        AnimConvert.SequenceGroup(w.Loader, m, 12).Should().Be(4);
        AnimConvert.SequenceGroup(w.Loader, m, 13).Should().Be(4);
        AnimConvert.SequenceGroup(w.Loader, m, 20).Should().Be(1);
        AnimConvert.SequenceGroup(w.Loader, m, 0).Should().Be(0);
        AnimConvert.Job(w.Loader, m, 13, 2)!.Key.Should().Be(AnimConvert.Job(w.Loader, m, 4, 2)!.Key, "action 13 draws stored group 4 of anim2 body 7");
        AnimConvert.Job(w.Loader, m, 13, 2)!.Action.Should().Be(4);

        // A replacement whose group is empty in the drawn file is not applied: 5's sequence sends walk (0) to group 22,
        // past anim2 body 5's 22 monster groups, which would leave giant spider 28 invisible while walking.
        var spider = AnimConvert.Meta(w.Loader, fm.Version, 28, plan.Conv);
        (spider.FileIndex, spider.SourceBody, spider.SeqBody).Should().Be((1, (ushort)5, 5));
        AnimConvert.SequenceGroup(w.Loader, spider, 0).Should().Be(0);
        AnimConvert.Job(w.Loader, spider, 0, 0).Should().NotBeNull();
        AnimConvert.SequenceGroup(w.Loader, spider, 12).Should().Be(4);

        // Old (own anim.mul art): no conversion, so no sequence (as ClassicUO: MUL bodies use none).
        var old = BodyPlan.Build(fm, BodyChoices.Parse("{\"remodels\":{\"9\":\"old\"}}"));
        using var w2 = new AnimWorker(fm, old);
        var m2 = AnimConvert.Meta(w2.Loader, fm.Version, 9, old.Conv);
        (m2.FileIndex, m2.SeqBody).Should().Be((0, -1));
        AnimConvert.SequenceGroup(w2.Loader, m2, 13).Should().Be(13);
    }

    [ClassicUoFact]
    public void Pack_Entries_Follow_The_Choices_And_Stand_Ins_Follow_Their_Target()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"uo_body_choices_{Guid.NewGuid():N}");
        try
        {
            var fm = Fm();
            AnimConvert.Bodies = [9, 43, 11, 28, 2000];
            AnimBodyEntry[] Convert(string json)
            {
                var plan = BodyPlan.Build(fm, BodyChoices.Parse(json));
                AnimConvert.Run(fm, dir, plan);
                return AnimTableFile.Read(Path.Combine(dir, "anim", AnimTableFile.FileName)).Bodies;
            }

            var aos = Convert("{\"extras\":[{\"id\":2000,\"from\":\"anim.mul:9\"}]}");
            (aos[9].Source, aos[9].SourceBody).Should().Be(((byte)1, (ushort)7), "9 draws anim2 body 7");
            aos[43].AliasBody.Should().Be(AnimConst.NoBody, "43 is converted (anim2 11), so no stand-in");
            (aos[2000].Source, aos[2000].SourceBody, aos[2000].Type, aos[2000].Flags).Should().Be(((byte)0, (ushort)9, aos[9].Type, aos[9].Flags));
            aos[2000].HasFrames.Should().BeTrue();
            aos.Length.Should().BeGreaterThan(2000);

            var t2a = Convert("{\"preset\":\"t2a\",\"additions\":\"standins\"}");
            (t2a[9].Source, t2a[9].SourceBody).Should().Be(((byte)0, (ushort)9), "9 draws its own anim.mul art");
            (t2a[43].AliasBody, t2a[43].AliasHue).Should().Be(((ushort)9, (ushort)1152), "43's stand-in is 9, which now draws the old art");
            (t2a[11].AliasBody, t2a[11].AliasHue).Should().Be(((ushort)28, (ushort)1401));
            (t2a[28].Source, t2a[28].SourceBody).Should().Be(((byte)0, (ushort)28));
        }
        finally
        {
            AnimConvert.Bodies = null;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>Runs when the 7.0.103 install is present (CUO_CLASSIC_UO, default C:\Ultima Online Classic; read only).</summary>
public sealed class ClassicUoFactAttribute : FactAttribute
{
    public static string Dir => Environment.GetEnvironmentVariable("CUO_CLASSIC_UO") is { Length: > 0 } d ? d : @"C:\Ultima Online Classic";

    public ClassicUoFactAttribute()
    {
        if (!File.Exists(Path.Combine(Dir, "anim.mul")) || !File.Exists(Path.Combine(Dir, "Bodyconv.def")))
            Skip = $"no UO install with anim.mul and Bodyconv.def at {Dir} (set CUO_CLASSIC_UO)";
    }
}
