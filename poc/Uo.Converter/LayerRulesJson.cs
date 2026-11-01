using System.Globalization;
using System.Text;
using System.Text.Json;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>A layer rules JSON value the converter cannot use. The message says which entry and why.</summary>
public sealed class LayerRulesJsonException(string message) : Exception(message);

/// <summary>
/// T3b: the layering rules as text (docs/modern-client/layer-rules.md): <c>--layer-rules rules.json</c> writes
/// layerrules.bin from it, <c>--export-layer-rules rules.json</c> writes the built-in rules (<see cref="LayerRulesDefault"/>)
/// so a shard starts from today's rules. The JSON maps one to one onto the binary file: rules, their conditions and ranges
/// keep their order, so the default round-trips byte for byte. Names for layers, kinds, ops, flags and body groups; numbers
/// (graphics, AnimIDs, bodies) as JSON numbers or strings ("0x1F03", "0x399-0x39D" for a range). Parsing checks what the
/// binary loader checks (layer bounds, kinds, table shape, counts) and refuses unknown keys and duplicates; the client reads
/// only the binary.
/// </summary>
public static class LayerRulesJson
{
    public const int FormatVersion = 1;

    private static readonly string[] LayerNames =
    [
        "Invalid", "OneHanded", "TwoHanded", "Shoes", "Pants", "Shirt", "Helmet", "Gloves", "Ring", "Talisman", "Necklace", "Hair",
        "Waist", "Torso", "Bracelet", "Face", "Beard", "Tunic", "Earrings", "Arms", "Cloak", "Backpack", "Robe", "Skirt", "Legs"
    ];

    private static readonly string[] SetNames = ["paperdoll", "world"];
    private static readonly string[] KindNames = ["select", "reorder", "cover", "dirmove", "remap"];
    private static readonly string[] ReorderOps = ["moveTo", "moveAfter", "swapIfAfter"];
    private static readonly string[] DirOps = ["top", "bottom", "before"];
    private static readonly (string Name, byte Bit)[] FlagNames = [("female", LayerRulesConst.FlagFemale), ("gargoyleDraw", LayerRulesConst.FlagGargoyleDraw), ("cv7000", LayerRulesConst.FlagCv7000)];
    private static readonly string[] TopKeys = ["layerRules", "bodies", "tables", "rules"];
    private static readonly string[] CondKinds = ["present", "graphic", "anim", "flag", "group", "body"];

    // ---- export ---------------------------------------------------------------------------------------------------------

    /// <summary>The rules as JSON text (indented, one rule per line where it fits).</summary>
    public static string Export(LayerRules rules)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"layerRules\": ").Append(FormatVersion).Append(",\n");

        sb.Append("  \"bodies\": [\n");
        for (int i = 0; i < rules.Bodies.Length; i++)
        {
            BodyGroupEntry b = rules.Bodies[i];
            if (b.Pad != 0) throw new LayerRulesJsonException($"body entry {i}: pad byte {b.Pad} (not representable in JSON)");
            sb.Append($"    {{ \"body\": \"{Hex(b.Body)}\", \"group\": \"{GroupName(b.Group)}\" }}").Append(i + 1 < rules.Bodies.Length ? ",\n" : "\n");
        }

        sb.Append("  ],\n  \"tables\": [\n");
        int tables = rules.Tables.Length / LayerRulesConst.LayerCount;
        for (int t = 0; t < tables; t++)
        {
            sb.Append("    [");
            for (int i = 0; i < LayerRulesConst.LayerCount; i++)
            {
                sb.Append(i == 0 ? "" : ", ").Append('"').Append(LayerName(rules.Tables[t * LayerRulesConst.LayerCount + i])).Append('"');
            }

            sb.Append(']').Append(t + 1 < tables ? ",\n" : "\n");
        }

        sb.Append("  ],\n  \"rules\": [\n");
        for (int i = 0; i < rules.Rules.Length; i++)
        {
            sb.Append("    ").Append(ExportRule(rules, i)).Append(i + 1 < rules.Rules.Length ? ",\n" : "\n");
        }

        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private static string ExportRule(LayerRules rules, int index)
    {
        LayerRule r = rules.Rules[index];
        var f = new List<string> { $"\"set\": \"{SetNames[r.Set]}\"", $"\"kind\": \"{KindNames[r.Kind]}\"" };
        void Unused(string field, int value)
        {
            if (value != 0) throw new LayerRulesJsonException($"rule {index} ({KindNames[r.Kind]}): field {field} = {value} is not used by this kind (not representable in JSON)");
        }

        switch ((LayerRuleKind)r.Kind)
        {
            case LayerRuleKind.Select:
                Unused("layer", r.Layer); Unused("other", r.Other); Unused("op", r.Op); Unused("value2", r.Value2);
                f.Add($"\"table\": {r.Value}");
                break;
            case LayerRuleKind.Reorder:
                Unused("value", r.Value); Unused("value2", r.Value2);
                f.Add($"\"op\": \"{ReorderOps[r.Op]}\"");
                f.Add($"\"layer\": {LayerJson(r.Layer)}");
                f.Add($"\"other\": {LayerJson(r.Other)}");
                break;
            case LayerRuleKind.Cover:
                Unused("op", r.Op); Unused("value", r.Value); Unused("value2", r.Value2);
                f.Add($"\"layer\": {LayerJson(r.Layer)}");
                f.Add($"\"covered\": {(r.Other != 0 ? "true" : "false")}");
                break;
            case LayerRuleKind.DirMove:
                Unused("value2", r.Value2);
                if ((r.Value & ~0x1FF) != 0) throw new LayerRulesJsonException($"rule {index} (dirmove): direction mask 0x{r.Value:X} has bits above 8");
                var dirs = new List<string>();
                for (int d = 0; d < 8; d++) if ((r.Value & (1 << d)) != 0) dirs.Add(d.ToString(CultureInfo.InvariantCulture));
                if ((r.Value & LayerRulesConst.DirOther) != 0) dirs.Add("\"other\"");
                f.Add($"\"layer\": {LayerJson(r.Layer)}");
                f.Add($"\"directions\": [{string.Join(", ", dirs)}]");
                f.Add($"\"op\": \"{DirOps[r.Op]}\"");
                if (r.Other != 0 || r.Op == (byte)LayerDirOp.Before) f.Add($"\"anchor\": {LayerJson(r.Other)}");
                break;
            case LayerRuleKind.Remap:
                Unused("layer", r.Layer); Unused("other", r.Other); Unused("op", r.Op);
                f.Add($"\"from\": \"{Hex(r.Value)}\"");
                f.Add($"\"to\": \"{Hex(r.Value2)}\"");
                break;
        }

        var conds = new List<string>();
        for (int c = r.CondStart; c < r.CondStart + r.CondCount; c++) conds.Add(ExportCond(rules, index, c));
        f.Add($"\"when\": [{string.Join(", ", conds)}]");
        return "{ " + string.Join(", ", f) + " }";
    }

    private static string ExportCond(LayerRules rules, int rule, int index)
    {
        LayerCond c = rules.Conds[index];
        if (c.Pad != 0) throw new LayerRulesJsonException($"rule {rule} condition {index}: pad {c.Pad} (not representable in JSON)");
        var f = new List<string>();
        string Ranges(Func<ushort, string> name)
        {
            var r = new List<string>();
            for (int i = c.RangeStart; i < c.RangeStart + c.RangeCount; i++)
            {
                LayerRange g = rules.Ranges[i];
                r.Add(g.Lo == g.Hi ? $"\"{name(g.Lo)}\"" : $"\"{name(g.Lo)}-{name(g.Hi)}\"");
            }

            return "[" + string.Join(", ", r) + "]";
        }

        void NoRanges(string kind)
        {
            if (c.RangeCount != 0) throw new LayerRulesJsonException($"rule {rule} condition {index} ({kind}): has ranges (not representable in JSON)");
        }

        void NoLayer(string kind)
        {
            if (c.Layer != 0) throw new LayerRulesJsonException($"rule {rule} condition {index} ({kind}): layer byte {c.Layer} is not used (not representable in JSON)");
        }

        switch ((LayerCondKind)c.Kind)
        {
            case LayerCondKind.Present:
                NoRanges("present");
                f.Add($"\"present\": {LayerJson(c.Layer)}");
                break;
            case LayerCondKind.Graphic:
                f.Add($"\"graphic\": {LayerJson(c.Layer)}");
                f.Add($"\"in\": {Ranges(Hex)}");
                break;
            case LayerCondKind.AnimId:
                f.Add($"\"anim\": {LayerJson(c.Layer)}");
                f.Add($"\"in\": {Ranges(Hex)}");
                break;
            case LayerCondKind.Flag:
            {
                NoRanges("flag");
                var names = new List<string>();
                byte rest = c.Layer;
                foreach (var (name, bit) in FlagNames)
                {
                    if ((c.Layer & bit) != 0) { names.Add($"\"{name}\""); rest &= (byte)~bit; }
                }

                if (rest != 0) throw new LayerRulesJsonException($"rule {rule} condition {index} (flag): unknown flag bits 0x{rest:X2}");
                f.Add($"\"flag\": [{string.Join(", ", names)}]");
                break;
            }
            case LayerCondKind.Group:
                NoLayer("group");
                f.Add($"\"group\": {Ranges(v => GroupName(v))}");
                break;
            case LayerCondKind.Body:
                NoLayer("body");
                f.Add($"\"body\": {Ranges(Hex)}");
                break;
            default:
                throw new LayerRulesJsonException($"rule {rule} condition {index}: kind {c.Kind}");
        }

        if (c.Negate > 1) throw new LayerRulesJsonException($"rule {rule} condition {index}: negate {c.Negate} (0 or 1)");
        if (c.Negate != 0) f.Add("\"not\": true");
        return "{ " + string.Join(", ", f) + " }";
    }

    private static string Hex(ushort v) => $"0x{v:X4}";
    // a layer for the export: its name in quotes, or a bare JSON number when it has no name (the importer reads both)
    private static string LayerJson(byte l) => l < LayerNames.Length ? $"\"{LayerNames[l]}\"" : l.ToString(CultureInfo.InvariantCulture);

    private static string LayerName(byte l) => l < LayerNames.Length ? LayerNames[l] : l.ToString(CultureInfo.InvariantCulture);
    private static string GroupName(ushort g) => g <= (ushort)BodyGroup.GargoyleFemale ? ((BodyGroup)g).ToString() : g.ToString(CultureInfo.InvariantCulture);

    /// <summary>Writes the JSON text to <paramref name="path"/> (ReplacingFile: a temp file renamed over it; never into a
    /// published pack).</summary>
    public static void Save(LayerRules rules, string path) => ReplacingFile.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(Export(rules)));

    // ---- import ---------------------------------------------------------------------------------------------------------

    public static LayerRules Load(string path)
    {
        if (!File.Exists(path)) throw new LayerRulesJsonException($"{path}: file not found");
        return Parse(File.ReadAllText(path, Encoding.UTF8), path);
    }

    public static LayerRules Parse(string json, string name = "layer rules")
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            throw new LayerRulesJsonException($"{name}: not valid JSON ({e.Message})");
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new LayerRulesJsonException($"{name}: the top level must be an object");
            var seen = new HashSet<string>();
            foreach (JsonProperty p in root.EnumerateObject())
            {
                if (!TopKeys.Contains(p.Name)) throw new LayerRulesJsonException($"{name}: unknown key \"{p.Name}\" (expected {string.Join(", ", TopKeys)})");
                if (!seen.Add(p.Name)) throw new LayerRulesJsonException($"{name}: \"{p.Name}\" is given twice");
            }

            foreach (string k in TopKeys)
            {
                if (!seen.Contains(k)) throw new LayerRulesJsonException($"{name}: \"{k}\" is missing");
            }

            JsonElement version = root.GetProperty("layerRules");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int ver) || ver != FormatVersion)
                throw new LayerRulesJsonException($"{name}: \"layerRules\" must be {FormatVersion} (the format version), not {version.GetRawText()}");

            var b = new LayerRulesBuilder();

            // bodies
            JsonElement bodies = root.GetProperty("bodies");
            if (bodies.ValueKind != JsonValueKind.Array) throw new LayerRulesJsonException($"{name}: \"bodies\" must be an array of {{ \"body\": .., \"group\": .. }}");
            int bi = 0;
            foreach (JsonElement e in bodies.EnumerateArray())
            {
                string at = $"{name}: bodies[{bi++}]";
                Keys(e, at, ["body", "group"], ["body", "group"]);
                ushort body = Number(e.GetProperty("body"), $"{at}.body");
                ushort group = Group(e.GetProperty("group"), $"{at}.group");
                if (group > byte.MaxValue) throw new LayerRulesJsonException($"{at}.group: {group} is above 255");
                b.Body(body, (BodyGroup)group);
            }

            // tables
            JsonElement tables = root.GetProperty("tables");
            if (tables.ValueKind != JsonValueKind.Array) throw new LayerRulesJsonException($"{name}: \"tables\" must be an array of 25-layer arrays");
            int ti = 0;
            foreach (JsonElement t in tables.EnumerateArray())
            {
                string at = $"{name}: tables[{ti}]";
                if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() != LayerRulesConst.LayerCount)
                    throw new LayerRulesJsonException($"{at}: must list {LayerRulesConst.LayerCount} layers (each layer 0..24 once, back to front)");
                var layers = new byte[LayerRulesConst.LayerCount];
                var used = new bool[LayerRulesConst.LayerCount];
                int li = 0;
                foreach (JsonElement l in t.EnumerateArray())
                {
                    byte layer = Layer(l, $"{at}[{li}]", LayerRulesConst.LayerCount);
                    if (used[layer]) throw new LayerRulesJsonException($"{at}: layer {LayerName(layer)} is listed twice");
                    used[layer] = true;
                    layers[li++] = layer;
                }

                b.Table(layers);
                ti++;
            }

            if (ti > 255) throw new LayerRulesJsonException($"{name}: {ti} tables (at most 255)");

            // rules
            JsonElement rules = root.GetProperty("rules");
            if (rules.ValueKind != JsonValueKind.Array) throw new LayerRulesJsonException($"{name}: \"rules\" must be an array");
            int ri = 0, conds = 0, ranges = 0;
            foreach (JsonElement r in rules.EnumerateArray())
            {
                ParseRule(b, r, $"{name}: rules[{ri}]", ti, ref conds, ref ranges);
                ri++;
            }

            // the binary loader's own checks (layer bounds, kinds, the default select per set, ...) and its count limits
            LayerRules built;
            try
            {
                built = b.Build();
                using var ms = new MemoryStream();
                built.Write(ms);
                ms.Position = 0;
                LayerRules.Read(ms);
            }
            catch (InvalidDataException e)
            {
                throw new LayerRulesJsonException($"{name}: {e.Message}");
            }

            return built;
        }
    }

    private static void ParseRule(LayerRulesBuilder b, JsonElement r, string at, int tableCount, ref int condTotal, ref int rangeTotal)
    {
        if (r.ValueKind != JsonValueKind.Object) throw new LayerRulesJsonException($"{at}: must be an object");
        if (!r.TryGetProperty("kind", out JsonElement kindEl)) throw new LayerRulesJsonException($"{at}: \"kind\" is missing ({string.Join(", ", KindNames)})");
        int kind = OneOf(kindEl, KindNames, $"{at}.kind");
        string[] allowed = (LayerRuleKind)kind switch
        {
            LayerRuleKind.Select => ["set", "kind", "table", "when"],
            LayerRuleKind.Reorder => ["set", "kind", "op", "layer", "other", "when"],
            LayerRuleKind.Cover => ["set", "kind", "layer", "covered", "when"],
            LayerRuleKind.DirMove => ["set", "kind", "layer", "directions", "op", "anchor", "when"],
            _ => ["set", "kind", "from", "to", "when"],
        };
        string[] required = (LayerRuleKind)kind switch
        {
            LayerRuleKind.Select => ["set", "kind", "table"],
            LayerRuleKind.Reorder => ["set", "kind", "op", "layer", "other"],
            LayerRuleKind.Cover => ["set", "kind", "layer", "covered"],
            LayerRuleKind.DirMove => ["set", "kind", "layer", "directions", "op"],
            _ => ["set", "kind", "from", "to"],
        };
        Keys(r, at, allowed, required);
        var set = (LayerRuleSet)OneOf(r.GetProperty("set"), SetNames, $"{at}.set");
        C[] when = r.TryGetProperty("when", out JsonElement w) ? Conditions(w, $"{at}.when", ref condTotal, ref rangeTotal) : [];

        switch ((LayerRuleKind)kind)
        {
            case LayerRuleKind.Select:
            {
                ushort table = Number(r.GetProperty("table"), $"{at}.table");
                if (table >= tableCount) throw new LayerRulesJsonException($"{at}.table: {table}, but there are {tableCount} tables (0..{tableCount - 1})");
                b.Select(set, table, when);
                break;
            }
            case LayerRuleKind.Reorder:
                b.Reorder(set, (LayerReorderOp)OneOf(r.GetProperty("op"), ReorderOps, $"{at}.op"),
                    Layer(r.GetProperty("layer"), $"{at}.layer", LayerRulesConst.LayerCount), Layer(r.GetProperty("other"), $"{at}.other", LayerRulesConst.LayerCount), when);
                break;
            case LayerRuleKind.Cover:
            {
                JsonElement cov = r.GetProperty("covered");
                if (cov.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new LayerRulesJsonException($"{at}.covered: must be true or false");
                b.Cover(set, Layer(r.GetProperty("layer"), $"{at}.layer", LayerRulesConst.MaxLayer), cov.GetBoolean(), when);
                break;
            }
            case LayerRuleKind.DirMove:
            {
                JsonElement dirs = r.GetProperty("directions");
                if (dirs.ValueKind != JsonValueKind.Array || dirs.GetArrayLength() == 0)
                    throw new LayerRulesJsonException($"{at}.directions: must be a non-empty array of facings 0..7 and \"other\" (8 and up)");
                ushort mask = 0;
                foreach (JsonElement d in dirs.EnumerateArray())
                {
                    ushort bit;
                    if (d.ValueKind == JsonValueKind.String && d.GetString() == "other") bit = LayerRulesConst.DirOther;
                    else if (d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out int f) && f is >= 0 and < 8) bit = (ushort)(1 << f);
                    else throw new LayerRulesJsonException($"{at}.directions: {d.GetRawText()} is not a facing 0..7 or \"other\"");
                    if ((mask & bit) != 0) throw new LayerRulesJsonException($"{at}.directions: {d.GetRawText()} is listed twice");
                    mask |= bit;
                }

                var op = (LayerDirOp)OneOf(r.GetProperty("op"), DirOps, $"{at}.op");
                byte anchor = 0;
                if (r.TryGetProperty("anchor", out JsonElement a)) anchor = Layer(a, $"{at}.anchor", LayerRulesConst.LayerCount);
                else if (op == LayerDirOp.Before) throw new LayerRulesJsonException($"{at}: op \"before\" needs an \"anchor\" layer");
                b.DirMove(set, Layer(r.GetProperty("layer"), $"{at}.layer", LayerRulesConst.LayerCount), mask, op, anchor, when);
                break;
            }
            default:
                b.Remap(set, Number(r.GetProperty("from"), $"{at}.from"), Number(r.GetProperty("to"), $"{at}.to"), when);
                break;
        }
    }

    private static C[] Conditions(JsonElement w, string at, ref int condTotal, ref int rangeTotal)
    {
        if (w.ValueKind != JsonValueKind.Array) throw new LayerRulesJsonException($"{at}: must be an array of conditions (all must hold)");
        if (w.GetArrayLength() > byte.MaxValue) throw new LayerRulesJsonException($"{at}: {w.GetArrayLength()} conditions (at most 255 per rule)");
        var list = new List<C>();
        int i = 0;
        foreach (JsonElement c in w.EnumerateArray())
        {
            string ca = $"{at}[{i++}]";
            if (c.ValueKind != JsonValueKind.Object) throw new LayerRulesJsonException($"{ca}: must be an object");
            string? kind = null;
            foreach (JsonProperty p in c.EnumerateObject())
            {
                if (!CondKinds.Contains(p.Name)) continue;
                if (kind != null) throw new LayerRulesJsonException($"{ca}: one of {string.Join(", ", CondKinds)} per condition (\"{kind}\" and \"{p.Name}\")");
                kind = p.Name;
            }

            if (kind == null) throw new LayerRulesJsonException($"{ca}: needs one of {string.Join(", ", CondKinds)}");
            bool ranged = kind is "graphic" or "anim";
            Keys(c, ca, ranged ? [kind, "in", "not"] : [kind, "not"], ranged ? [kind, "in"] : [kind]);
            bool negate = false;
            if (c.TryGetProperty("not", out JsonElement n))
            {
                if (n.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new LayerRulesJsonException($"{ca}.not: must be true or false");
                negate = n.GetBoolean();
            }

            JsonElement v = c.GetProperty(kind);
            C cond = kind switch
            {
                "present" => new C(LayerCondKind.Present, Layer(v, $"{ca}.present", LayerRulesConst.LayerCount), negate, []),
                "graphic" => new C(LayerCondKind.Graphic, Layer(v, $"{ca}.graphic", LayerRulesConst.LayerCount), negate, Ranges(c.GetProperty("in"), $"{ca}.in", false)),
                "anim" => new C(LayerCondKind.AnimId, Layer(v, $"{ca}.anim", LayerRulesConst.LayerCount), negate, Ranges(c.GetProperty("in"), $"{ca}.in", false)),
                "flag" => new C(LayerCondKind.Flag, Flags(v, $"{ca}.flag"), negate, []),
                "group" => new C(LayerCondKind.Group, 0, negate, Ranges(v, $"{ca}.group", true)),
                _ => new C(LayerCondKind.Body, 0, negate, Ranges(v, $"{ca}.body", false)),
            };
            if (cond.R.Length > byte.MaxValue) throw new LayerRulesJsonException($"{ca}: {cond.R.Length} ranges (at most 255 per condition)");
            condTotal++;
            rangeTotal += cond.R.Length;
            if (condTotal > 0xFFFF || rangeTotal > 0xFFFF) throw new LayerRulesJsonException($"{ca}: more than 65535 conditions or ranges in the file");
            list.Add(cond);
        }

        return list.ToArray();
    }

    private static byte Flags(JsonElement v, string at)
    {
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0)
            throw new LayerRulesJsonException($"{at}: must be a non-empty array of {string.Join(", ", FlagNames.Select(f => f.Name))} (any of them set)");
        byte mask = 0;
        foreach (JsonElement f in v.EnumerateArray())
        {
            string? s = f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            int k = Array.FindIndex(FlagNames, x => x.Name == s);
            if (k < 0) throw new LayerRulesJsonException($"{at}: {f.GetRawText()} is not one of {string.Join(", ", FlagNames.Select(x => x.Name))}");
            if ((mask & FlagNames[k].Bit) != 0) throw new LayerRulesJsonException($"{at}: \"{s}\" is listed twice");
            mask |= FlagNames[k].Bit;
        }

        return mask;
    }

    private static (ushort, ushort)[] Ranges(JsonElement v, string at, bool groups)
    {
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0)
            throw new LayerRulesJsonException($"{at}: must be a non-empty array of values or \"lo-hi\" ranges");
        var list = new List<(ushort, ushort)>();
        int i = 0;
        foreach (JsonElement e in v.EnumerateArray())
        {
            string ea = $"{at}[{i++}]";
            ushort lo, hi;
            if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 1 } s && s.IndexOf('-', 1) is int dash and > 0)
            {
                lo = groups ? GroupText(s[..dash], ea) : NumberText(s[..dash], ea);
                hi = groups ? GroupText(s[(dash + 1)..], ea) : NumberText(s[(dash + 1)..], ea);
                if (lo > hi) throw new LayerRulesJsonException($"{ea}: range \"{s}\" runs backwards (lo above hi)");
            }
            else
            {
                lo = hi = groups ? Group(e, ea) : Number(e, ea);
            }

            list.Add((lo, hi));
        }

        return list.ToArray();
    }

    private static void Keys(JsonElement e, string at, string[] allowed, string[] required)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new LayerRulesJsonException($"{at}: must be an object");
        var seen = new HashSet<string>();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            if (!allowed.Contains(p.Name)) throw new LayerRulesJsonException($"{at}: unknown key \"{p.Name}\" (expected {string.Join(", ", allowed)})");
            if (!seen.Add(p.Name)) throw new LayerRulesJsonException($"{at}: \"{p.Name}\" is given twice");
        }

        foreach (string k in required)
        {
            if (!seen.Contains(k)) throw new LayerRulesJsonException($"{at}: \"{k}\" is missing");
        }
    }

    private static int OneOf(JsonElement v, string[] names, string at)
    {
        int k = v.ValueKind == JsonValueKind.String ? Array.IndexOf(names, v.GetString()) : -1;
        if (k < 0) throw new LayerRulesJsonException($"{at}: {v.GetRawText()} is not one of {string.Join(", ", names.Select(n => $"\"{n}\""))}");
        return k;
    }

    // a layer by name (Pants) or number (0..max-1)
    private static byte Layer(JsonElement v, string at, int max)
    {
        int l = -1;
        if (v.ValueKind == JsonValueKind.String) l = Array.IndexOf(LayerNames, v.GetString());
        else if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) l = n;
        if (l < 0 || l >= max)
            throw new LayerRulesJsonException($"{at}: {v.GetRawText()} is not a layer here (a name: {string.Join(", ", LayerNames)}; or a number below {max})");
        return (byte)l;
    }

    // a 16-bit value: a JSON number or a string (decimal or 0x hex)
    private static ushort Number(JsonElement v, string at)
    {
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt32(out int n) && n is >= 0 and <= 0xFFFF) return (ushort)n;
            throw new LayerRulesJsonException($"{at}: {v.GetRawText()} is not a value 0..65535");
        }

        if (v.ValueKind == JsonValueKind.String) return NumberText(v.GetString()!, at);
        throw new LayerRulesJsonException($"{at}: {v.GetRawText()} must be a number or a string like \"0x1F03\"");
    }

    private static ushort NumberText(string s, string at)
    {
        s = s.Trim();
        bool ok = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(s.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int n)
            : int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
        if (!ok || n < 0 || n > 0xFFFF) throw new LayerRulesJsonException($"{at}: \"{s}\" is not a value 0..65535 (decimal or 0x hex)");
        return (ushort)n;
    }

    private static ushort Group(JsonElement v, string at)
    {
        if (v.ValueKind == JsonValueKind.String) return GroupText(v.GetString()!, at);
        return Number(v, at);
    }

    private static ushort GroupText(string s, string at)
    {
        s = s.Trim();
        if (Enum.TryParse(s, ignoreCase: false, out BodyGroup g) && Enum.IsDefined(g) && !char.IsDigit(s[0])) return (ushort)g;
        if (s.Length > 0 && char.IsDigit(s[0])) return NumberText(s, at);
        throw new LayerRulesJsonException($"{at}: \"{s}\" is not a body group ({string.Join(", ", Enum.GetNames<BodyGroup>())}, or a number)");
    }
}
