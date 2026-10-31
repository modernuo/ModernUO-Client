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
    private static BodyPlan.Column C(int col, int g, int mh = 0) => new(col, (ushort)g, (sbyte)mh, BodyPlan.Era(col));

    // Remodels: 100 col1 only; 101 col1 and col3; 102 col2 only.
    // Additions: 200 col1 with a Body.def stand-in; 201 col1 without one; 202 col2 with one; 203 col1 and col3 with one.
    // 300: UOP.
    private static BodyPlan.SourceData Data() => new()
    {
        BodyCount = 2048,
        Columns = new()
        {
            [100] = [C(1, 7)], [101] = [C(1, 8), C(3, 9)], [102] = [C(2, 10)],
            [200] = [C(1, 11)], [201] = [C(1, 12)], [202] = [C(2, 14)], [203] = [C(1, 15), C(3, 16)], [300] = [C(4, 13)],
        },
        OwnArt = [100, 101, 102], BodyDef = [200, 202, 203], Uop = [300],
    };

    private static BodyPlan R(string json) => BodyPlan.Resolve(Data(), BodyChoices.Parse(json));

    private static int? Target(BodyPlan p, int body) => p.Conv.TryGetValue(body, out var c) ? c.Graphic : null;

    private static (int?, int?, int?) Remodels(BodyPlan p) => (Target(p, 100), Target(p, 101), Target(p, 102));
    private static (int?, int?, int?, int?) Additions(BodyPlan p) => (Target(p, 200), Target(p, 201), Target(p, 202), Target(p, 203));

    private static readonly (int?, int?, int?) AllOld = (null, null, null);

    [Fact]
    public void Aos_Is_The_Last_Column_Everywhere()
    {
        var aos = R("{}");
        Remodels(aos).Should().Be((7, 9, 10));
        Additions(aos).Should().Be((11, 12, 14, 16));
        Target(aos, 300).Should().Be(13, "UOP bodies are never remodels; their (unused) conversion is kept as today");
        aos.StandInIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("t2a")]
    [InlineData("uor")]
    public void T2a_And_Uor_Turn_Every_Remodel_Old_And_Later_Additions_Into_Their_Stand_Ins(string preset)
    {
        var p = R($"{{\"preset\":\"{preset}\"}}");
        Remodels(p).Should().Be(AllOld);
        Additions(p).Should().Be(((int?)null, 12, (int?)null, (int?)null), "every addition is later than T2A: stand-in where Body.def has one, else its art");
        p.StandInIds.Should().Equal(200, 202, 203);
    }

    [Fact]
    public void Lbr_Turns_Every_Remodel_Old_And_Keeps_Lbr_Additions()
    {
        var p = R("{\"preset\":\"lbr\"}");
        Remodels(p).Should().Be(AllOld, "remodels count as AOS (BodyPlan.RemodelEra), whatever their column");
        Additions(p).Should().Be((11, 12, (int?)null, 15), "column 1 is LBR; 202 is AOS (stand-in); 203 keeps its LBR column");
    }

    [Fact]
    public void ForceOld_Turns_Every_Remodel_Old_And_Leaves_Additions()
    {
        var p = R("{\"forceOldAnimations\":true}");
        Remodels(p).Should().Be(AllOld);
        Additions(p).Should().Be((11, 12, 14, 16));
        Additions(R("{\"preset\":\"lbr\",\"forceOldAnimations\":true}")).Should().Be(Additions(R("{\"preset\":\"lbr\"}")));
    }

    [Fact]
    public void Additions_Newest_Takes_The_Last_Column_Directly()
    {
        foreach (var preset in BodyChoices.Presets)
            Additions(R($"{{\"preset\":\"{preset}\",\"additions\":\"newest\"}}")).Should().Be((11, 12, 14, 16), $"{preset}: newest never uses an earlier column");
    }

    [Fact]
    public void Remodel_Overrides_Win_Over_The_Preset_And_ForceOld()
    {
        var p = R("{\"preset\":\"t2a\",\"remodels\":{\"100\":\"new\",\"0x65\":\"new\"}}");
        Remodels(p).Should().Be((7, 9, (int?)null), "\"new\" is the last column");
        Target(R("{\"remodels\":{\"102\":\"old\"}}"), 102).Should().BeNull();
        Target(R("{\"forceOldAnimations\":true,\"remodels\":{\"102\":\"new\"}}"), 102).Should().Be(10);
    }

    [Fact]
    public void Old_Remodels_With_Another_Own_Layout_Install_It()
    {
        var d = Data();
        d.OwnLayout[102] = (AnimationGroupsType.Monster, AnimationFlags.None);
        BodyPlan.Resolve(d, BodyChoices.Parse("{\"preset\":\"t2a\"}")).MobTypes.Should().ContainKey(102);
        BodyPlan.Resolve(d, BodyChoices.Parse("{}")).MobTypes.Should().NotContainKey(102, "new art is read with the mobtypes layout");
    }

    [Fact]
    public void Legacy_Policy_Is_Every_Last_Column()
    {
        var p = BodyPlan.Resolve(Data(), BodyChoices.Legacy);
        Remodels(p).Should().Be((7, 9, 10));
        Additions(p).Should().Be((11, 12, 14, 16));
        BodyChoices.Legacy.TargetSequences.Should().BeFalse();
    }

    [Fact]
    public void Extras_Get_The_Variant_And_A_Mount_Height()
    {
        var d = Data();
        d.Columns[103] = [C(1, 204, -9)];
        d.Columns[90] = [C(1, 204, 5)];
        d.Uop.Add(90);
        var p = BodyPlan.Resolve(d, BodyChoices.Parse("{\"extras\":[{\"id\":1500,\"from\":\"anim.mul:100\"},{\"id\":\"0x1000\",\"from\":\"anim2.mul:204\"},{\"id\":1501,\"from\":\"anim2.mul:204\",\"mountHeight\":3}]}"));
        p.Conv[1500].Should().Be((0, (ushort)100, (sbyte)0));
        p.ExtraMobTypeFrom[1500].Should().Be(100);
        p.Conv[0x1000].Should().Be((1, (ushort)204, (sbyte)-9), "default: the column of the lowest non-UOP body converting to it");
        p.ExtraMobTypeFrom[0x1000].Should().Be(103, "UOP body 90 is lower but never draws that MUL art");
        p.Conv[1501].Should().Be((1, (ushort)204, (sbyte)3), "an explicit mount height wins");
        p.BodyCount.Should().Be(0x1001);
    }

    [Theory]
    [InlineData("[]", "top level must be an object")]
    [InlineData("{\"preset\":", "not valid JSON")]
    [InlineData("{\"prest\":\"aos\"}", "unknown key \"prest\"")]
    [InlineData("{\"preset\":\"se\"}", "\"preset\" must be one of")]
    [InlineData("{\"preset\":\"aos\",\"preset\":\"t2a\"}", "given twice")]
    [InlineData("{\"additions\":\"oldest\"}", "\"additions\" must be one of")]
    [InlineData("{\"sequences\":\"source\"}", "\"sequences\" must be one of")]
    [InlineData("{\"forceOldAnimations\":\"yes\"}", "true or false")]
    [InlineData("{\"remodels\":{\"76\":\"older\"}}", "must be \"old\" or \"new\"")]
    [InlineData("{\"remodels\":{\"dragon\":\"old\"}}", "not a body id")]
    [InlineData("{\"remodels\":{\"65535\":\"old\"}}", "not a body id")]
    [InlineData("{\"remodels\":{\"76\":\"old\",\"0x4C\":\"new\"}}", "lists body 76 twice")]
    [InlineData("{\"extras\":{}}", "must be an array")]
    [InlineData("{\"extras\":[{\"id\":1500}]}", "extras[0]: an extra needs both")]
    [InlineData("{\"extras\":[{\"id\":1500,\"from\":\"anim.mul:76\",\"hue\":5}]}", "extras[0]: unknown key \"hue\"")]
    [InlineData("{\"extras\":[{\"id\":1500,\"from\":\"AnimationFrame1.uop:713\"}]}", "UOP bodies cannot be copied")]
    [InlineData("{\"extras\":[{\"id\":1500,\"from\":\"anim1.mul:76\"}]}", "must be <file>:<body>")]
    [InlineData("{\"extras\":[{\"id\":1500,\"from\":\"anim.mul:x\"}]}", "not a body id")]
    [InlineData("{\"extras\":[{\"id\":1500,\"from\":\"anim.mul:76\"},{\"id\":1500,\"from\":\"anim.mul:10\"}]}", "id 1500 more than once")]
    [InlineData("{\"extras\":[{\"id\":1500,\"from\":\"anim.mul:76\"},{\"id\":1501,\"from\":\"anim.mul:9\",\"from\":\"anim.mul:10\"}]}", "extras[1]: \"from\" is given twice in the same extra")]
    [InlineData("{\"extras\":[{\"id\":1500,\"id\":1501,\"from\":\"anim.mul:9\"}]}", "extras[0]: \"id\" is given twice in the same extra")]
    [InlineData("{\"extras\":[{\"id\":1500,\"from\":\"anim.mul:9\",\"mountHeight\":200}]}", "extras[0]: \"mountHeight\" must be a whole number")]
    [InlineData("{\"extras\":[{\"id\":1500,\"from\":\"anim.mul:9\",\"mountHeight\":-9,\"mountHeight\":0}]}", "\"mountHeight\" is given twice")]
    public void Bad_Input_Fails_With_A_Clear_Error(string json, string message)
    {
        var act = () => BodyChoices.Parse(json);
        act.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain(message);
    }

    [Theory]
    [InlineData("{\"remodels\":{\"200\":\"old\"}}", "body 200 has no anim.mul art of its own")]
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
    public void Manifest_Record_Round_Trips_And_Versions_The_Policy()
    {
        var c = BodyChoices.Parse("{\"preset\":\"lbr\",\"forceOldAnimations\":true,\"additions\":\"newest\",\"sequences\":\"target\",\"remodels\":{\"76\":\"old\"},\"extras\":[{\"id\":1500,\"from\":\"anim2.mul:7\"},{\"id\":1501,\"from\":\"anim.mul:9\",\"mountHeight\":-9}]}");
        var rec = c.ToRecord();
        rec.Policy.Should().Be(BodyChoices.CurrentPolicy);
        var back = BodyChoices.FromRecord(rec);
        (back.Policy, back.Preset, back.ForceOldAnimations, back.Additions, back.Sequences).Should().Be((BodyChoices.CurrentPolicy, "lbr", true, "newest", "target"));
        back.Remodels.Should().Equal(new Dictionary<int, bool> { [76] = false });
        back.Extras.Should().Equal(new BodyExtra(1500, 1, 7), new BodyExtra(1501, 0, 9, -9));

        var legacy = BodyChoices.FromRecord(null);
        (legacy.Policy, legacy.Preset, legacy.Additions, legacy.Sequences).Should().Be((BodyChoices.LegacyPolicy, "aos", "newest", "none"), "a pack without a bodies record was built with the classic-1.2 rules");
        var defaults = BodyChoices.Default;
        (defaults.Policy, defaults.Preset, defaults.Additions, defaults.Sequences).Should().Be((BodyChoices.CurrentPolicy, "aos", "standins", "none"));

        rec.Policy = 0;
        FluentActions.Invoking(() => BodyChoices.FromRecord(rec)).Should().Throw<BodyChoicesException>().WithMessage("*policy 0*rebuild*");
        rec.Policy = BodyChoices.CurrentPolicy + 1;
        FluentActions.Invoking(() => BodyChoices.FromRecord(rec)).Should().Throw<BodyChoicesException>().WithMessage("*knows up to*");
    }

    // ---- round 2 ----

    [Fact]
    public void Mount_Shift_Is_The_Drawn_Height_Minus_The_Default_Art_Height()
    {
        // Finding 1. 200 (addition, col1 height -9) has Body.def stand-in 210; 100 (remodel) converts with height -9;
        // 300 is UOP. Extras carry their own height.
        var d = Data();
        d.Columns[200] = [C(1, 11, -9)];
        d.Columns[100] = [C(1, 7, -9)];
        bool Uop(int id) => id == 300;
        ushort? BodyDef(ushort id) => id == 200 ? (ushort)210 : null;
        sbyte Shift(BodyPlan p, int b) => p.MountShift(b, p.MountBaseline(), Uop, BodyDef);

        var aos = BodyPlan.Resolve(d, BodyChoices.Parse("{}"));
        foreach (var b in new[] { 100, 101, 200, 201, 210, 300, 999 }) Shift(aos, b).Should().Be(0, $"the default pack draws the art the Mounts table was set for (body {b})");

        var t2a = BodyPlan.Resolve(d, BodyChoices.Parse("{\"preset\":\"t2a\",\"extras\":[{\"id\":1500,\"from\":\"anim2.mul:7\",\"mountHeight\":5}]}"));
        Shift(t2a, 200).Should().Be(9, "200 draws its stand-in 210 (height 0) instead of the col1 art (-9)");
        Shift(t2a, 100).Should().Be(9, "old remodel: own anim.mul art, height 0");
        Shift(t2a, 201).Should().Be(0);
        Shift(t2a, 1500).Should().Be(5, "an extra has no default art: its own height");

        var legacy = BodyPlan.Resolve(d, BodyChoices.Legacy);
        Shift(legacy, 200).Should().Be(0, "legacy packs store no shift");
    }

    [Fact]
    public void Probe_Requires_Visible_Pixels()
    {
        // Finding 3: a positive-size frame holding only the RLE terminator decodes fully transparent.
        BodyPlan.Visible(null).Should().BeFalse();
        BodyPlan.Visible([]).Should().BeFalse();
        BodyPlan.Visible([new AnimConvert.RawFrame(0, 0, 10, 10, new uint[100])]).Should().BeFalse("dimensions alone are not art");
        var px = new uint[100];
        px[57] = 0xFF00_7C00;
        BodyPlan.Visible([new AnimConvert.RawFrame(0, 0, 10, 10, new uint[100]), new AnimConvert.RawFrame(0, 0, 10, 10, px)]).Should().BeTrue();
        BodyPlan.Classification[95].Remodel.Should().BeFalse("95 is classified as not a remodel (owner, 2026-10-31)");
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

    // The bodies with own walk/stand art in their own anim.mul layout and a conversion (finding 1 of the Codex audit).
    // 95 is not one: its slot's usable art is an unrelated grey creature (BodyPlan.Classification, round-2 finding 3).
    private static readonly int[] Remodels = [9, 10, 26, 28, 76];
    // Bodies the mobtypes.txt layout found index entries for, in other bodies' slots: not remodels.
    private static readonly int[] Misread = [253, 318, 319, 689, 704, 791, 793, 794, 796, 797, 798, 799];

    [ClassicUoFact]
    public void Remodels_Are_Classified_By_Their_Own_AnimMul_Layout()
    {
        var p = Plan("{}");
        p.RemodelIds.Should().Equal(Remodels);
        p.Source.MisreadSlots.Should().Contain(Misread);
        p.Source.OwnLayout.Should().BeEmpty("95, the only remodel candidate read in another layout, is classified as an addition");
        p.Source.NotRemodels.Keys.Should().Equal([95]);
        p.Source.MisreadSlots.Should().NotContain(95);
        p.Source.StandIn[318].Should().Be((9, 0), "318's older look is the Body.def stand-in 9, not own art");
        p.Source.StandIn[791].Should().Be((220, 11));
    }

    [ClassicUoFact]
    public void Aos_Default_Is_Every_Conversion_Plus_The_Three_Reverted_Lines()
    {
        var p = Plan("{}");
        T(p, 10).Should().Be((1, 7, 0));
        T(p, 11).Should().Be((1, 3, 0));
        T(p, 76).Should().Be((1, 27, 0));
        T(p, 95).Should().Be((2, 95, 0));
        T(p, 157).Should().Be((1, 1, 0));
        T(p, 318).Should().Be((2, 318, 0));
        T(p, 791).Should().Be((1, 203, 0));
        T(p, 793).Should().Be((1, 204, -9), "mount heights follow the chosen column");
        T(p, 9).Should().Be((1, 7, 0));
        T(p, 26).Should().Be((1, 24, 0));
        T(p, 28).Should().Be((1, 5, 0));
        T(p, 43).Should().Be((1, 11, 0));
        p.OldRemodelIds.Should().BeEmpty();
        p.StandInIds.Should().BeEmpty("aos shows every era");
        p.MobTypes.Should().BeEmpty();
    }

    [ClassicUoFact]
    public void Legacy_Policy_Has_No_Reverted_Lines()
    {
        var fm = Fm();
        var p = BodyPlan.Build(fm, BodyChoices.Legacy);
        foreach (var b in new[] { 9, 26, 28 }) T(p, b).Should().BeNull($"classic-1.2 drew {b}'s anim.mul art");
        T(p, 10).Should().Be((1, 7, 0));
        using var w = new AnimWorker(fm, p);
        AnimConvert.Meta(w.Loader, fm.Version, 10, p).SeqBody.Should().Be(-1, "legacy: no sequences for MUL bodies");
    }

    [ClassicUoFact]
    public void Pre_Aos_Presets_Turn_Remodels_Old_And_Later_Additions_Into_Stand_Ins()
    {
        foreach (var preset in new[] { "t2a", "uor", "lbr" })
        {
            var p = Plan($"{{\"preset\":\"{preset}\"}}");
            foreach (var b in Remodels) T(p, b).Should().BeNull($"{preset}: remodel {b} draws its anim.mul art");
            p.OldRemodelIds.Should().Equal(Remodels);
            p.MobTypes.Should().NotContainKey(95, $"{preset}: 95 is an addition, never read in its slot's monster layout");
            T(p, 318).Should().BeNull($"{preset}: 318 (column 2, AOS) draws its Body.def stand-in 9");
            T(p, 319).Should().BeNull($"{preset}: 319 draws its stand-in 51");
            T(p, 253).Should().Be((3, 11, 0), $"{preset}: 253 has no stand-in, so it keeps its art");
            if (preset == "lbr")
            {
                T(p, 11).Should().Be((1, 3, 0), "lbr: column-1 additions keep their art");
                T(p, 791).Should().Be((1, 203, 0));
                T(p, 43).Should().Be((1, 11, 0));
            }
            else
            {
                T(p, 11).Should().BeNull($"{preset}: 11 draws its stand-in 28 hue 1401");
                T(p, 157).Should().BeNull();
                T(p, 43).Should().BeNull();
                T(p, 791).Should().BeNull($"{preset}: the mount draws its stand-in 220");
            }
            p.StandInIds.Should().NotBeEmpty();
        }
    }

    [ClassicUoFact]
    public void ForceOld_Turns_Every_Remodel_Old_And_Leaves_Additions()
    {
        var force = Plan("{\"forceOldAnimations\":true}");
        foreach (var b in Remodels) T(force, b).Should().BeNull();
        T(force, 318).Should().Be((2, 318, 0), "318 is an addition");
        T(force, 791).Should().Be((1, 203, 0));
        T(force, 11).Should().Be((1, 3, 0), "additions keep their art");
        force.StandInIds.Should().BeEmpty();
    }

    [ClassicUoFact]
    public void Turkey_95_Is_Classified_As_An_Addition()
    {
        // Round-2 finding 3: its own anim.mul slot passes the probe (usable monster-layout art), but the art is an
        // unrelated grey creature, not an older turkey. The classification table makes it an addition.
        var fm = Fm();
        var p = BodyPlan.Build(fm, BodyChoices.Default);
        p.RemodelIds.Should().NotContain(95);
        T(p, 95).Should().Be((2, 95, 0));
        var t2a = BodyPlan.Build(fm, BodyChoices.Parse("{\"preset\":\"t2a\"}"));
        t2a.OldRemodelIds.Should().NotContain(95, "t2a never draws the grey shape for 95");
        t2a.MobTypes.Should().NotContainKey(95);
        var a = () => BodyPlan.Build(fm, BodyChoices.Parse("{\"remodels\":{\"95\":\"old\"}}"));
        a.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain("classified as an addition");
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
    public void An_Explicit_Mul_Extra_Never_Turns_Into_A_Uop_Lookup()
    {
        // anim2.mul:71 is converted to only by UOP bodies (753): its kind is inherited, its UOP flag is not.
        var fm = Fm();
        var p = BodyPlan.Build(fm, BodyChoices.Parse("{\"extras\":[{\"id\":2000,\"from\":\"anim2.mul:71\"}]}"));
        p.MobTypes[2000].Flags.HasFlag(AnimationFlags.UseUopAnimation).Should().BeFalse();
        using var w = new AnimWorker(fm, p);
        var m = AnimConvert.Meta(w.Loader, fm.Version, 2000, p);
        (m.Uop, m.FileIndex, m.SourceBody).Should().Be((false, 1, (ushort)71));
    }

    [ClassicUoFact]
    public void Extra_Mount_Height_Defaults_To_The_Referencing_Body_And_Can_Be_Set()
    {
        var p = Plan("{\"extras\":[{\"id\":2000,\"from\":\"anim2.mul:204\"},{\"id\":2001,\"from\":\"anim2.mul:204\",\"mountHeight\":0}]}");
        T(p, 2000).Should().Be((1, 204, -9), "793 converts to anim2 204 with height -9");
        T(p, 2001).Should().Be((1, 204, 0));
    }

    [ClassicUoFact]
    public void Bad_Ids_On_Real_Data()
    {
        Action a = () => Plan("{\"extras\":[{\"id\":76,\"from\":\"anim.mul:10\"}]}");
        a.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain("id 76 is already in use");
        a = () => Plan("{\"extras\":[{\"id\":2000,\"from\":\"anim.mul:2002\"}]}");
        a.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain("extras[0] (id 2000): anim.mul:2002 has no frames");
        a = () => Plan("{\"remodels\":{\"11\":\"old\"}}");
        a.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain("is an addition");
        a = () => Plan("{\"remodels\":{\"318\":\"old\"}}");
        a.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain("Body.def stand-in 9");
        a = () => Plan("{\"remodels\":{\"200\":\"old\"}}");
        a.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain("no Bodyconv.def conversion");
    }

    [ClassicUoFact]
    public void Extras_Cannot_Take_Equipment_Anim_Ids()
    {
        // 498 is the AnimID of ring 0x108A: an extra there would change how the ring draws.
        Action a = () => Plan("{\"extras\":[{\"id\":498,\"from\":\"anim.mul:76\"}]}");
        a.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain("tiledata item 0x108A");

        // An Equipconv.def target no tiledata item uses.
        var fm = Fm();
        fm.TileData.Load();
        var animIds = fm.TileData.StaticData.Select(s => (int)s.AnimID).ToHashSet();
        using var w = new AnimWorker(fm, null);
        var target = w.Loader.EquipConversions.SelectMany(kv => kv.Value.Values).Select(d => (int)d.Graphic)
            .Where(g => !animIds.Contains(g) && !w.Loader.HasMobType(g) && g >= 2048 && g < AnimConst.NoBody).DefaultIfEmpty(-1).Min();
        if (target < 0) return;
        a = () => BodyPlan.Build(fm, BodyChoices.Parse($"{{\"extras\":[{{\"id\":{target},\"from\":\"anim.mul:76\"}}]}}"));
        a.Should().Throw<BodyChoicesException>().Which.Message.Should().Contain("Equipconv.def");
    }

    [ClassicUoFact]
    public void Target_Sequences_Are_Opt_In()
    {
        var fm = Fm();
        var plan = BodyPlan.Build(fm, BodyChoices.Default);
        using (var w0 = new AnimWorker(fm, plan))
        {
            var m0 = AnimConvert.Meta(w0.Loader, fm.Version, 9, plan);
            (m0.FileIndex, m0.SourceBody, m0.SeqBody).Should().Be((1, (ushort)7, -1), "default: no AnimationSequence for MUL bodies");
            AnimConvert.SequenceGroup(w0.Loader, m0, 13, 2).Should().Be(13);
        }

        // "sequences": "target". AnimationSequence.uop: 9 replaces 13->12 and 20->19; anim2 body 7 replaces 12->4, 13->4 and 20->1.
        plan = BodyPlan.Build(fm, BodyChoices.Parse("{\"sequences\":\"target\"}"));
        using var w = new AnimWorker(fm, plan);
        var m = AnimConvert.Meta(w.Loader, fm.Version, 9, plan);
        (m.FileIndex, m.SourceBody, m.SeqBody).Should().Be((1, (ushort)7, 7));
        AnimConvert.SequenceGroup(w.Loader, m, 12, 2).Should().Be(4);
        AnimConvert.SequenceGroup(w.Loader, m, 13, 2).Should().Be(4);
        AnimConvert.SequenceGroup(w.Loader, m, 20, 2).Should().Be(1);
        AnimConvert.SequenceGroup(w.Loader, m, 0, 2).Should().Be(0);
        AnimConvert.Job(w.Loader, m, 13, 2)!.Key.Should().Be(AnimConvert.Job(w.Loader, m, 4, 2)!.Key, "action 13 draws stored group 4 of anim2 body 7");
        AnimConvert.Job(w.Loader, m, 13, 2)!.Action.Should().Be(4);

        // A replacement whose group is empty in the drawn file is not applied: 5's sequence sends walk (0) to group 22,
        // past anim2 body 5's 22 monster groups, which would leave giant spider 28 invisible while walking.
        var spider = AnimConvert.Meta(w.Loader, fm.Version, 28, plan);
        (spider.FileIndex, spider.SourceBody, spider.SeqBody).Should().Be((1, (ushort)5, 5));
        AnimConvert.SequenceGroup(w.Loader, spider, 0, 0).Should().Be(0);
        AnimConvert.Job(w.Loader, spider, 0, 0).Should().NotBeNull();
        AnimConvert.SequenceGroup(w.Loader, spider, 12, 0).Should().Be(4);

        // Old (own anim.mul art): no conversion, so no sequence.
        var old = BodyPlan.Build(fm, BodyChoices.Parse("{\"sequences\":\"target\",\"remodels\":{\"9\":\"old\"}}"));
        using var w2 = new AnimWorker(fm, old);
        var m2 = AnimConvert.Meta(w2.Loader, fm.Version, 9, old);
        (m2.FileIndex, m2.SeqBody).Should().Be((0, -1));
    }

    [ClassicUoFact]
    public void Bad_Choices_Stop_Before_The_Previous_Output_Is_Cleared()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"uo_body_choices_out_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "art"));
            File.WriteAllText(Path.Combine(dir, "art", "a.bin"), "previous output");
            ManifestFileIo.Write(Path.Combine(dir, "manifest.json"), new Manifest { Files = [new ManifestFile { Path = "art/a.bin", Size = 15, Sha256 = "" }] });
            var bad = Path.Combine(dir, "..", $"uo_bad_bodies_{Guid.NewGuid():N}.json");
            File.WriteAllText(bad, "{\"extras\":[{\"id\":76,\"from\":\"anim.mul:10\"}]}");
            var entry = typeof(BodyPlan).Assembly.EntryPoint!;
            foreach (var choices in new[] { bad, Path.Combine(dir, "missing.json") })
            {
                var rc = entry.Invoke(null, [new[] { "--uo", ClassicUoFactAttribute.Dir, "--out", dir, "--body-choices", choices, "--no-maps" }]);
                rc.Should().Be(1);
                File.Exists(Path.Combine(dir, "art", "a.bin")).Should().BeTrue($"{Path.GetFileName(choices)} is rejected before anything is cleared");
            }
            File.Delete(bad);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [ClassicUoFact]
    public void Pack_Entries_Follow_The_Choices_And_Stand_Ins_Follow_Their_Target()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"uo_body_choices_{Guid.NewGuid():N}");
        try
        {
            var fm = Fm();
            AnimConvert.Bodies = [9, 43, 11, 28, 95, 2000];
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

            var t2a = Convert("{\"preset\":\"t2a\"}");
            (t2a[9].Source, t2a[9].SourceBody).Should().Be(((byte)0, (ushort)9), "9 draws its own anim.mul art");
            (t2a[43].AliasBody, t2a[43].AliasHue).Should().Be(((ushort)9, (ushort)1152), "43's stand-in is 9, which now draws the old art");
            (t2a[11].AliasBody, t2a[11].AliasHue).Should().Be(((ushort)28, (ushort)1401));
            t2a[11].HasFrames.Should().BeFalse("a body drawn as its stand-in stores no frames of its own");
            t2a[43].HasFrames.Should().BeFalse();
            (t2a[28].Source, t2a[28].SourceBody).Should().Be(((byte)0, (ushort)28));
            (t2a[95].Source == 0 && t2a[95].HasFrames).Should().BeFalse("95 is an addition: never its slot's grey creature");
        }
        finally
        {
            AnimConvert.Bodies = null;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- round 2 ----

    [ClassicUoFact]
    public void Mount_Shifts_On_Real_Data()
    {
        // Finding 1: aos stores no shift anywhere; t2a moves the rider of every mount drawn with other art.
        var fm = Fm();
        var aos = BodyPlan.Build(fm, BodyChoices.Default);
        var t2a = BodyPlan.Build(fm, BodyChoices.Parse("{\"preset\":\"t2a\"}"));
        using var w = new AnimWorker(fm, aos);
        var uop = new Dictionary<int, bool>();
        bool Uop(int id) => uop.TryGetValue(id, out var u) ? u : uop[id] = AnimConvert.Meta(w.Loader, fm.Version, id, aos).Uop;
        ushort? Def(ushort id) => AnimConvert.BodyDefNext(w.Loader, id);
        var aosBase = aos.MountBaseline();
        for (int b = 0; b < aos.BodyCount; b++) aos.MountShift(b, aosBase, Uop, Def).Should().Be(0);
        var tb = t2a.MountBaseline();
        t2a.MountShift(793, tb, Uop, Def).Should().Be(9, "skeletal mount 793 draws horse 200 (0) instead of its anim2 art (-9)");
        t2a.MountShift(192, tb, Uop, Def).Should().Be(9, "ethereal unicorn 192 draws its stand-in 228");
        t2a.MountShift(200, tb, Uop, Def).Should().Be(0);
        var legacy = BodyPlan.Build(fm, BodyChoices.Legacy);
        legacy.MountShift(793, legacy.MountBaseline(), Uop, Def).Should().Be(0);
    }

    [ClassicUoFact]
    public void Frame_Less_Stand_Ins_Keep_A_Corpse()
    {
        // Finding 2: t2a 191-195 draw Body.def stand-ins and have no Corpse.def line. Their corpses fall back to the live
        // stand-in; 192's corpse draws 228's death frames (aos: 192's own six).
        var dir = Path.Combine(Path.GetTempPath(), $"uo_body_corpse_{Guid.NewGuid():N}");
        try
        {
            var fm = Fm();
            AnimConvert.Bodies = [191, 192, 193, 194, 195, 228, 210, 219, 220];
            var plan = BodyPlan.Build(fm, BodyChoices.Parse("{\"preset\":\"t2a\"}"));
            AnimConvert.Run(fm, dir, plan);
            var (bodies, dirs, _) = AnimTableFile.Read(Path.Combine(dir, "anim", AnimTableFile.FileName));
            foreach (var b in new[] { 191, 192, 193, 194, 195 })
            {
                bodies[b].HasFrames.Should().BeFalse($"{b} draws its stand-in");
                bodies[b].AliasBody.Should().NotBe(AnimConst.NoBody);
                (bodies[b].CorpseBody, bodies[b].CorpseHue).Should().Be((bodies[b].AliasBody, bodies[b].AliasHue), $"{b} has no Corpse.def line: its corpse is its stand-in");
            }
            foreach (var b in plan.StandInIds) bodies[b].CorpseBody.Should().NotBe(AnimConst.NoBody, $"stand-in {b} needs a corpse target");
            var c = bodies[bodies[192].CorpseBody];
            c.HasFrames.Should().BeTrue();
            int deathFrames = 0;
            foreach (var a in new[] { 8, 12 })
                if (a < c.ActionCount) deathFrames += dirs[c.FirstDir + a * AnimConst.Directions].FrameCount;
            deathFrames.Should().BeGreaterThan(0, "the corpse target has death frames");

            // Corpse.def is honoured: a stand-in with a Corpse.def line keeps a target on its Corpse.def chain.
            using var w = new AnimWorker(fm, plan);
            int withLine = 0;
            foreach (var b in plan.StandInIds)
            {
                ushort id = (ushort)b, hue = 0;
                var chain = new List<ushort>();
                for (int guard = 0; guard < 16 && w.Loader.ReplaceCorpse(ref id, ref hue); guard++) chain.Add(id);
                if (chain.Count == 0) continue;
                withLine++;
                chain.Should().Contain(bodies[b].CorpseBody, $"stand-in {b}'s corpse follows Corpse.def");
            }
            withLine.Should().BeGreaterThan(0);
        }
        finally
        {
            AnimConvert.Bodies = null;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [ClassicUoFact]
    public void Target_Sequences_Fall_Back_Per_Direction()
    {
        // Finding 5: a replacement populated in only some directions is applied there only; the other directions keep
        // the action's own group. Simulated by emptying directions 1-4 of group 4 (7's replacement for 12) in 9's index.
        var fm = Fm();
        var plan = BodyPlan.Build(fm, BodyChoices.Parse("{\"sequences\":\"target\"}"));
        using var w = new AnimWorker(fm, plan);
        var m = AnimConvert.Meta(w.Loader, fm.Version, 9, plan);
        m.SeqBody.Should().Be(7);
        for (int d = 1; d < 5; d++) m.Idx[4 * 5 + d] = default;
        AnimConvert.SequenceGroup(w.Loader, m, 12, 0).Should().Be(4);
        for (int d = 1; d < 5; d++)
        {
            AnimConvert.SequenceGroup(w.Loader, m, 12, d).Should().Be(12, $"direction {d} of group 4 is empty");
            AnimConvert.Job(w.Loader, m, 12, d).Should().NotBeNull($"direction {d} keeps group 12's own frames");
        }
    }

    [ClassicUoFact]
    public void Unsupported_Manifest_Policy_Stops_Every_Path_Before_Writing()
    {
        // Finding 4: a retained bodies record is validated before any output, manifest or debug write.
        var root = Path.Combine(Path.GetTempPath(), $"uo_body_policy_{Guid.NewGuid():N}");
        var dir = Path.Combine(root, "pack");
        var debug = Path.Combine(root, "debug");
        try
        {
            Directory.CreateDirectory(dir);
            var manifest = new Manifest { Bodies = new BodyChoicesRecord { Policy = BodyChoices.CurrentPolicy + 1 } };
            ManifestFileIo.Write(Path.Combine(dir, "manifest.json"), manifest);
            var before = File.ReadAllText(Path.Combine(dir, "manifest.json"));
            var entry = typeof(BodyPlan).Assembly.EntryPoint!;
            foreach (var args in new[]
            {
                new[] { "--only", "hues" }, new[] { "--only", "tiledata,hues" }, new[] { "--verify-only" }, new[] { "--no-anims", "--no-maps" },
            })
            {
                var rc = entry.Invoke(null, [new[] { "--uo", ClassicUoFactAttribute.Dir, "--out", dir, "--debug", debug }.Concat(args).ToArray()]);
                rc.Should().Be(1, string.Join(" ", args));
                Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Should().Equal([Path.Combine(dir, "manifest.json")], $"{string.Join(" ", args)} writes nothing");
                File.ReadAllText(Path.Combine(dir, "manifest.json")).Should().Be(before);
                Directory.Exists(debug).Should().BeFalse($"{string.Join(" ", args)} writes no debug output");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
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
