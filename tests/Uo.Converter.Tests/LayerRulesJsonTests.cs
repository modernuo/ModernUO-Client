using System.Runtime.InteropServices;
using FluentAssertions;
using Uo.Converter;
using Uo.Pack;
using Xunit;

namespace Uo.Converter.Tests;

/// <summary>T3b: the layering rules as JSON (--layer-rules, --export-layer-rules): the default round trip, and the checks.</summary>
public sealed class LayerRulesJsonTests
{
    private static byte[] Bin(LayerRules r)
    {
        using var ms = new MemoryStream();
        r.Write(ms);
        return ms.ToArray();
    }

    private static string DefaultJson() => LayerRulesJson.Export(LayerRulesDefault.Create());

    [Fact]
    public void The_Default_Round_Trips_Byte_For_Byte()
    {
        byte[] want = Bin(LayerRulesDefault.Create());
        want.Length.Should().Be(3055, "the default file (pack-format.md)");
        string json = DefaultJson();
        Bin(LayerRulesJson.Parse(json)).Should().Equal(want);

        // and the export of the parsed rules is the same text
        LayerRulesJson.Export(LayerRulesJson.Parse(json)).Should().Be(json);
    }

    [Fact]
    public void Save_And_Load_Through_A_File()
    {
        string dir = Path.Combine(Path.GetTempPath(), "layerrules-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "rules.json");
            LayerRulesJson.Save(LayerRulesDefault.Create(), path);
            Bin(LayerRulesJson.Load(path)).Should().Equal(Bin(LayerRulesDefault.Create()));
            Directory.GetFiles(dir).Should().ContainSingle("no temp file left");
            Action missing = () => LayerRulesJson.Load(Path.Combine(dir, "none.json"));
            missing.Should().Throw<LayerRulesJsonException>().WithMessage("*file not found*");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Names_Numbers_And_Hex_Are_Read()
    {
        const string json = """
        {
          // a minimal rules file: one table, the default select per set, a cover and a remap
          "layerRules": 1,
          "bodies": [ { "body": 400, "group": "HumanMale" }, { "body": "0x029A", "group": 5 } ],
          "tables": [ ["Invalid", "Cloak", "Shirt", "Pants", "Shoes", "Legs", "Arms", "Torso", "Tunic", "Ring", "Bracelet", "Face", "Gloves",
                       "Skirt", "Robe", "Waist", "Necklace", "Hair", "Beard", "Earrings", "Helmet", "OneHanded", "TwoHanded", "Backpack", 9] ],
          "rules": [
            { "set": "paperdoll", "kind": "select", "table": 0 },
            { "set": "world", "kind": "select", "table": "0" },
            { "set": "world", "kind": "cover", "layer": "Pants", "covered": true, "when": [ { "graphic": "Legs", "in": ["0x1411", "5000-5005", 7] }, { "group": ["GargoyleMale-GargoyleFemale"], "not": true } ] },
            { "set": "world", "kind": "dirmove", "layer": "Cloak", "directions": [0, 7, "other"], "op": "before", "anchor": "Helmet" },
            { "set": "world", "kind": "remap", "from": "0x01D5", "to": 342, "when": [ { "flag": ["gargoyleDraw", "female"] }, { "body": ["0x029A-0x029B"] } ] },
          ],
        }
        """;
        LayerRules r = LayerRulesJson.Parse(json);
        r.Bodies.Should().HaveCount(2);
        r.Bodies[1].Body.Should().Be(0x29A);
        r.Bodies[1].Group.Should().Be((byte)BodyGroup.GargoyleMale);
        r.Tables[24].Should().Be(LayerRulesDefault.Talisman);
        r.Rules.Should().HaveCount(5);
        r.Rules[2].Kind.Should().Be((byte)LayerRuleKind.Cover);
        r.Rules[2].Other.Should().Be(1);
        r.Ranges.Take(3).Select(g => (g.Lo, g.Hi)).Should().Equal(((ushort)0x1411, (ushort)0x1411), ((ushort)5000, (ushort)5005), ((ushort)7, (ushort)7));
        r.Conds[1].Kind.Should().Be((byte)LayerCondKind.Group);
        r.Conds[1].Negate.Should().Be(1);
        r.Rules[3].Value.Should().Be((ushort)(1 | 1 << 7 | LayerRulesConst.DirOther));
        r.Rules[3].Other.Should().Be(LayerRulesDefault.Helmet);
        r.Rules[4].Value.Should().Be(0x1D5);
        r.Rules[4].Value2.Should().Be(342);
        r.Conds[2].Layer.Should().Be((byte)(LayerRulesConst.FlagGargoyleDraw | LayerRulesConst.FlagFemale));
        var present = new bool[25];
        var gfx = new ushort[25];
        present[LayerRulesDefault.Legs] = true;
        gfx[LayerRulesDefault.Legs] = 0x1411;
        r.IsCovered(LayerRuleSet.World, LayerRulesDefault.Pants, present, gfx, new ushort[25], new LayerSubject(r.GroupOf(400), 0, 400)).Should().BeTrue();
        r.IsCovered(LayerRuleSet.World, LayerRulesDefault.Pants, present, gfx, new ushort[25], new LayerSubject(r.GroupOf(0x29A), 0, 0x29A)).Should().BeFalse("not for gargoyles");
    }

    // one change to the default JSON text -> the expected message part
    public static TheoryData<string, string, string> Bad => new()
    {
        { "\"layerRules\": 1", "\"layerRules\": 2", "*\"layerRules\" must be 1*" },
        { "\"layerRules\": 1,", "\"layerRules\": 1, \"extra\": 0,", "*unknown key \"extra\"*" },
        { "\"layerRules\": 1,", "\"layerRules\": 1, \"layerRules\": 1,", "*given twice*" },
        { "\"group\": \"HumanMale\"", "\"group\": \"Orc\"", "*bodies[0].group*not a body group*" },
        { "\"body\": \"0x0190\"", "\"body\": \"0x10000\"", "*bodies[0].body*not a value 0..65535*" },
        { "[\"Invalid\", \"Cloak\", \"Shirt\"", "[\"Invalid\", \"Shirt\", \"Shirt\"", "*tables[0]: layer Shirt is listed twice*" },
        { "[\"Invalid\", \"Cloak\", \"Shirt\"", "[\"Invalid\", \"Cloak\", \"Shirtt\"", "*tables[0][2]*not a layer*" },
        { "\"kind\": \"select\", \"table\": 1", "\"kind\": \"select\", \"table\": 7", "*table: 7, but there are 3 tables*" },
        { "\"kind\": \"select\", \"table\": 1", "\"kind\": \"selekt\", \"table\": 1", "*kind*not one of*" },
        { "\"set\": \"paperdoll\"", "\"set\": \"both\"", "*set*not one of*" },
        { "{ \"anim\": \"Arms\"", "{ \"anim\": \"Mount\"", "*rules[0].when[0].anim*not a layer here*" },
        { "{ \"anim\": \"Arms\"", "{ \"anim\": 25", "*not a layer here*below 25*" },
        { "{ \"anim\": \"Arms\", \"in\": [", "{ \"anim\": \"Arms\", \"present\": \"Arms\", \"in\": [", "*one of present, graphic*per condition*" },
        { "{ \"anim\": \"Arms\", \"in\": [", "{ \"anim\": \"Arms\", \"value\": 1, \"in\": [", "*unknown key \"value\"*" },
        { "\"0x0210\"", "\"0x0210-0x0200\"", "*runs backwards*" },
        { "\"0x0210\"", "\"zz\"", "*\"zz\" is not a value*" },
        { "\"op\": \"moveTo\"", "\"op\": \"moveInto\"", "*op*not one of*" },
        { "\"flag\": [\"female\"]", "\"flag\": [\"elf\"]", "*\"elf\" is not one of female, gargoyleDraw, cv7000*" },
        { "\"directions\": [0]", "\"directions\": [9]", "*9 is not a facing*" },
        { "\"op\": \"before\", \"anchor\": \"Helmet\"", "\"op\": \"before\"", "*needs an \"anchor\"*" },
        { "\"from\": \"0x01D5\"", "\"from\": \"0x01D5\", \"layer\": \"Robe\"", "*unknown key \"layer\"*" },
        { "\"covered\": true", "\"covered\": \"yes\"", "*covered: must be true or false*" },
        { "{ \"anim\": \"Arms\"", "{ \"anim\": \"Arms\", \"not\": 1", "*not: must be true or false*" },
    };

    [Theory]
    [MemberData(nameof(Bad))]
    public void A_Bad_Value_Is_Refused_With_A_Clear_Message(string from, string to, string message)
    {
        string json = DefaultJson();
        json.Should().Contain(from);
        int at = json.IndexOf(from, StringComparison.Ordinal);
        string bad = json[..at] + to + json[(at + from.Length)..];
        Action parse = () => LayerRulesJson.Parse(bad, "rules.json");
        parse.Should().Throw<LayerRulesJsonException>().WithMessage("rules.json: " + message);
    }

    [Fact]
    public void The_Binary_Loaders_Checks_Apply()
    {
        // no unconditional select in the world set: the loader's own check, reported as the JSON's error
        string json = DefaultJson();
        string worldDefault = "{ \"set\": \"world\", \"kind\": \"select\", \"table\": 1, \"when\": [] }";
        json.Should().Contain(worldDefault);
        string bad = json.Replace(worldDefault, "{ \"set\": \"world\", \"kind\": \"select\", \"table\": 1, \"when\": [ { \"present\": \"Shirt\" } ] }");
        Action parse = () => LayerRulesJson.Parse(bad, "rules.json");
        parse.Should().Throw<LayerRulesJsonException>().WithMessage("rules.json: layerrules: set 1 has no unconditional (default) select");

        // a remap that reads a layer (remaps see only the wearer)
        string remap = "{ \"set\": \"world\", \"kind\": \"remap\", \"from\": \"0x01D5\", \"to\": \"0x0156\", \"when\": [";
        json.Should().Contain(remap);
        bad = json.Replace(remap, remap + " { \"present\": \"Robe\" },");
        parse = () => LayerRulesJson.Parse(bad, "rules.json");
        parse.Should().Throw<LayerRulesJsonException>().WithMessage("*remap rule * reads a layer*");

        Action notJson = () => LayerRulesJson.Parse("{ \"layerRules\": ", "rules.json");
        notJson.Should().Throw<LayerRulesJsonException>().WithMessage("rules.json: not valid JSON*");
        Action missing = () => LayerRulesJson.Parse("{ \"layerRules\": 1, \"bodies\": [], \"tables\": [] }", "rules.json");
        missing.Should().Throw<LayerRulesJsonException>().WithMessage("rules.json: \"rules\" is missing");
    }

    [Fact]
    public void Every_Default_Rule_Kind_And_Condition_Kind_Is_In_The_Export()
    {
        string json = DefaultJson();
        foreach (string k in new[] { "select", "reorder", "cover", "dirmove", "remap" }) json.Should().Contain($"\"kind\": \"{k}\"");
        foreach (string k in new[] { "\"present\":", "\"graphic\":", "\"anim\":", "\"flag\":", "\"group\":", "\"body\":", "\"not\": true" }) json.Should().Contain(k);
        LayerRules d = LayerRulesDefault.Create();
        MemoryMarshal.AsBytes(LayerRulesJson.Parse(json).Rules.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(d.Rules.AsSpan())).Should().BeTrue();
    }

    [Fact]
    public void A_Binary_Value_The_Json_Cannot_Hold_Is_Refused_At_Export()
    {
        LayerRules d = LayerRulesDefault.Create();
        var rules = (LayerRule[])d.Rules.Clone();
        rules[0].Op = 1; // a select's op is unused
        var odd = new LayerRules(d.Bodies, d.Tables, rules, d.Conds, d.Ranges);
        Action export = () => LayerRulesJson.Export(odd);
        export.Should().Throw<LayerRulesJsonException>().WithMessage("rule 0 (select): field op = 1 is not used*");
    }
}
