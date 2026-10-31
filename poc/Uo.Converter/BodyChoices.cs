using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Utility;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>A bodies.json value the importer cannot use. The message says which entry and why.</summary>
public sealed class BodyChoicesException(string message) : Exception(message);

/// <summary>An extra body: the frames of one variant (<c>anim.mul:76</c>, <c>anim2.mul:7</c>) copied into an unused id.</summary>
public sealed record BodyExtra(int Id, int FileIndex, ushort Graphic)
{
    public string From => $"{FileName(FileIndex)}:{Graphic}";
    public static string FileName(int fileIndex) => fileIndex == 0 ? "anim.mul" : $"anim{fileIndex + 1}.mul";
}

/// <summary>
/// The shard admin's body import choices (bodies.json, --body-choices). The pack holds exactly the body set these
/// choices resolve to; the client does no body remapping at run time. See docs/modern-client/bodies.md.
/// </summary>
public sealed partial class BodyChoices
{
    public static readonly string[] Presets = ["t2a", "uor", "lbr", "aos"];
    public static readonly string[] AdditionModes = ["newest", "standins"];
    private static readonly string[] Keys = ["preset", "forceOldAnimations", "remodels", "additions", "extras"];

    public string Preset = "aos";
    public bool ForceOldAnimations;
    public string Additions = "newest";
    /// <summary>Per remodel id: true = new (converted art), false = old (its own anim.mul art).</summary>
    public SortedDictionary<int, bool> Remodels = new();
    public List<BodyExtra> Extras = new();
    /// <summary>Where the choices came from ("default" or the file path) and the file's SHA-256 (empty for the default).</summary>
    public string Source = "default", Sha256 = "";

    public static BodyChoices Default => new();

    public static BodyChoices Load(string path)
    {
        if (!File.Exists(path)) throw new BodyChoicesException($"{path}: file not found");
        var bytes = File.ReadAllBytes(path);
        var c = Parse(System.Text.Encoding.UTF8.GetString(bytes), path);
        c.Source = Path.GetFullPath(path);
        c.Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return c;
    }

    public static BodyChoices Parse(string json, string name = "bodies.json")
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            throw new BodyChoicesException($"{name}: not valid JSON ({e.Message})");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new BodyChoicesException($"{name}: the top level must be an object");
            var c = new BodyChoices();
            var seen = new HashSet<string>();
            foreach (var p in root.EnumerateObject())
            {
                if (!Keys.Contains(p.Name)) throw new BodyChoicesException($"{name}: unknown key \"{p.Name}\" (expected {string.Join(", ", Keys)})");
                if (!seen.Add(p.Name)) throw new BodyChoicesException($"{name}: \"{p.Name}\" is given twice");
                var v = p.Value;
                switch (p.Name)
                {
                    case "preset":
                        c.Preset = OneOf(v, Presets, name, "preset");
                        break;
                    case "additions":
                        c.Additions = OneOf(v, AdditionModes, name, "additions");
                        break;
                    case "forceOldAnimations":
                        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new BodyChoicesException($"{name}: \"forceOldAnimations\" must be true or false");
                        c.ForceOldAnimations = v.GetBoolean();
                        break;
                    case "remodels":
                        if (v.ValueKind != JsonValueKind.Object) throw new BodyChoicesException($"{name}: \"remodels\" must be an object of body id -> \"old\" | \"new\"");
                        foreach (var r in v.EnumerateObject())
                        {
                            int id = ParseId(r.Name, $"{name}: remodels key \"{r.Name}\"");
                            if (r.Value.ValueKind != JsonValueKind.String || r.Value.GetString() is not ("old" or "new"))
                                throw new BodyChoicesException($"{name}: remodels \"{r.Name}\" must be \"old\" or \"new\", not {r.Value.GetRawText()}");
                            if (!c.Remodels.TryAdd(id, r.Value.GetString() == "new")) throw new BodyChoicesException($"{name}: remodels lists body {id} twice");
                        }
                        break;
                    case "extras":
                        if (v.ValueKind != JsonValueKind.Array) throw new BodyChoicesException($"{name}: \"extras\" must be an array of {{ \"id\": n, \"from\": \"anim.mul:76\" }}");
                        foreach (var e in v.EnumerateArray()) c.Extras.Add(ParseExtra(e, name));
                        foreach (var g in c.Extras.GroupBy(x => x.Id).Where(g => g.Count() > 1))
                            throw new BodyChoicesException($"{name}: extras use id {g.Key} more than once");
                        break;
                }
            }
            return c;
        }
    }

    private static string OneOf(JsonElement v, string[] allowed, string name, string key)
    {
        if (v.ValueKind != JsonValueKind.String || !allowed.Contains(v.GetString()))
            throw new BodyChoicesException($"{name}: \"{key}\" must be one of {string.Join(", ", allowed.Select(a => $"\"{a}\""))}, not {v.GetRawText()}");
        return v.GetString()!;
    }

    /// <summary>A body id: decimal or 0x hex, 0..0xFFFE (0xFFFF means "no body" in the pack).</summary>
    public static int ParseId(string s, string what)
    {
        s = s.Trim();
        bool hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(hex ? s[2..] : s, hex ? System.Globalization.NumberStyles.AllowHexSpecifier : System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int id) || id < 0 || id >= AnimConst.NoBody)
            throw new BodyChoicesException($"{what}: not a body id (decimal or 0x hex, 0..{AnimConst.NoBody - 1})");
        return id;
    }

    [GeneratedRegex(@"^anim(\d*)\.mul:(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FromPattern();

    private static BodyExtra ParseExtra(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new BodyChoicesException($"{name}: each extra must be an object {{ \"id\": n, \"from\": \"anim.mul:76\" }}");
        int? id = null;
        string? from = null;
        foreach (var p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "id":
                    id = p.Value.ValueKind switch
                    {
                        JsonValueKind.Number when p.Value.TryGetInt32(out var n) => ParseId(n.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{name}: extra id {n}"),
                        JsonValueKind.String => ParseId(p.Value.GetString()!, $"{name}: extra id \"{p.Value.GetString()}\""),
                        _ => throw new BodyChoicesException($"{name}: extra id must be a number, not {p.Value.GetRawText()}"),
                    };
                    break;
                case "from":
                    if (p.Value.ValueKind != JsonValueKind.String) throw new BodyChoicesException($"{name}: extra \"from\" must be a string like \"anim.mul:76\"");
                    from = p.Value.GetString();
                    break;
                default:
                    throw new BodyChoicesException($"{name}: unknown key \"{p.Name}\" in an extra (expected id, from)");
            }
        }
        if (id == null || from == null) throw new BodyChoicesException($"{name}: an extra needs both \"id\" and \"from\"");
        return ParseFrom(id.Value, from, name);
    }

    private static BodyExtra ParseFrom(int id, string from, string name)
    {
        var m = FromPattern().Match(from);
        int fileIndex = m.Success ? (m.Groups[1].Value.Length == 0 ? 0 : int.TryParse(m.Groups[1].Value, out var n2) && n2 >= 2 && n2 <= 10 ? n2 - 1 : -1) : -1;
        if (fileIndex < 0)
            throw new BodyChoicesException($"{name}: extra {id}: \"from\" must be <file>:<body> with file anim.mul or anim2.mul..anim10.mul, not \"{from}\" (UOP bodies cannot be copied)");
        int graphic = ParseId(m.Groups[2].Value, $"{name}: extra {id}: \"from\" body \"{m.Groups[2].Value}\"");
        return new BodyExtra(id, fileIndex, (ushort)graphic);
    }

    public BodyChoicesRecord ToRecord() => new()
    {
        Preset = Preset, ForceOldAnimations = ForceOldAnimations, Additions = Additions,
        Remodels = Remodels.Select(kv => new BodyRemodelRecord { Id = kv.Key, Use = kv.Value ? "new" : "old" }).ToList(),
        Extras = Extras.Select(x => new BodyExtraRecord { Id = x.Id, From = x.From }).ToList(),
        Source = Source, Sha256 = Sha256,
    };

    /// <summary>The choices a pack was built with (its manifest); packs from before bodies.json used the default.</summary>
    public static BodyChoices FromRecord(BodyChoicesRecord? r)
    {
        if (r == null) return Default;
        var c = new BodyChoices
        {
            Preset = r.Preset, ForceOldAnimations = r.ForceOldAnimations, Additions = r.Additions, Source = r.Source, Sha256 = r.Sha256,
        };
        foreach (var x in r.Remodels) c.Remodels[x.Id] = x.Use == "new";
        foreach (var x in r.Extras) c.Extras.Add(ParseFrom(x.Id, x.From, "manifest"));
        return c;
    }

    public override string ToString() =>
        $"preset {Preset}{(ForceOldAnimations ? " + forceOldAnimations" : "")}, additions {Additions}, {Remodels.Count} remodel overrides, {Extras.Count} extras ({Source}{(Sha256.Length > 0 ? " sha256 " + Sha256[..12] : "")})";
}

/// <summary>
/// Bodyconv.def resolved per the import choices: which conversion (file, graphic, mount height) each body id gets.
/// A <b>remodel</b> is a body with its own anim.mul art and a conversion; "old" draws the own art, "new" the converted
/// art (last column). An <b>addition</b> is a converted body with no anim.mul art of its own.
/// </summary>
public sealed class BodyPlan
{
    public readonly record struct Column(int Col, ushort Graphic, sbyte MountHeight);

    /// <summary>
    /// 7.x Bodyconv.def turns these three back with all -1 lines ("reverts the Daemon back to the original style"), yet
    /// the OSI client still draws the anim2 art unless old animations are forced (owner, 2026-10-31; survey
    /// cuo-poc-state/surveys/bodies.md §6). They are column-1 remodels with their original LBR/AOS/ML targets.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, ushort> RevertedLbrRemodels = new Dictionary<int, ushort> { [9] = 7, [26] = 24, [28] = 5 };

    /// <summary>What the import data says, independent of the choices.</summary>
    public sealed class SourceData
    {
        /// <summary>Per body with a Bodyconv.def conversion: its columns (1 = anim2 ...) in file order, files present only.</summary>
        public Dictionary<int, List<Column>> Columns = new();
        public HashSet<int> Uop = new(), OwnArt = new(), BodyDef = new();
        /// <summary>For each extra id: why it is already in use (absent = free).</summary>
        public Dictionary<int, string> InUse = new();
        public int BodyCount;
    }

    public BodyChoices Choices = BodyChoices.Default;
    public Dictionary<int, (int FileIndex, ushort Graphic, sbyte MountHeight)> Conv = new();
    /// <summary>Extra id -> the body whose mobtypes.txt entry (kind and flags) it takes; -1 = none.</summary>
    public Dictionary<int, int> ExtraMobTypeFrom = new();
    public int BodyCount;
    public int RemodelsOld, RemodelsNew, AdditionsStandIn, AdditionsNew;
    /// <summary>Every remodel id (own anim.mul art and a conversion), and those that draw their old art.</summary>
    public List<int> RemodelIds = new(), OldRemodelIds = new();

    public static Column? PresetPick(List<Column> cols, BodyChoices c)
    {
        bool col1 = cols.Any(x => x.Col == 1);
        if (c.ForceOldAnimations && col1) return null;
        return c.Preset switch
        {
            "t2a" or "uor" => null,
            "lbr" => col1 ? cols.Last(x => x.Col == 1) : null,
            _ => cols[^1],
        };
    }

    /// <summary>The pure resolution step (tests use it on synthetic data).</summary>
    public static BodyPlan Resolve(SourceData s, BodyChoices c)
    {
        foreach (var id in c.Remodels.Keys)
        {
            if (s.Uop.Contains(id)) throw new BodyChoicesException($"remodels: body {id} draws UOP frames (mobtypes 0x10000), which are never a remodel");
            if (!s.Columns.ContainsKey(id)) throw new BodyChoicesException($"remodels: body {id} has no Bodyconv.def conversion, so it is not a remodel");
            if (!s.OwnArt.Contains(id)) throw new BodyChoicesException($"remodels: body {id} has no anim.mul art of its own, so it is an addition (set those with \"additions\")");
        }
        var plan = new BodyPlan { Choices = c, BodyCount = s.BodyCount };
        foreach (var (body, cols) in s.Columns.OrderBy(kv => kv.Key))
        {
            var last = cols[^1];
            if (s.Uop.Contains(body))
            {
                plan.Conv[body] = (last.Col, last.Graphic, last.MountHeight); // ignored: UOP frames win, as today
                continue;
            }
            Column? chosen;
            if (s.OwnArt.Contains(body))
            {
                chosen = c.Remodels.TryGetValue(body, out var isNew) ? (isNew ? last : null) : PresetPick(cols, c);
                plan.RemodelIds.Add(body);
                if (chosen == null) { plan.RemodelsOld++; plan.OldRemodelIds.Add(body); }
                else plan.RemodelsNew++;
            }
            else
            {
                chosen = PresetPick(cols, c);
                if (chosen == null && (c.Additions != "standins" || !s.BodyDef.Contains(body))) chosen = last;
                if (chosen == null) plan.AdditionsStandIn++; else plan.AdditionsNew++;
            }
            if (chosen is { } ch) plan.Conv[body] = (ch.Col, ch.Graphic, ch.MountHeight);
        }
        foreach (var x in c.Extras)
        {
            if (s.InUse.TryGetValue(x.Id, out var why)) throw new BodyChoicesException($"extras: id {x.Id} is already in use ({why}); pick an id nothing else uses");
            if (s.Uop.Contains(x.Id)) throw new BodyChoicesException($"extras: id {x.Id} is already in use (UOP body)");
            int mobFrom = -1;
            sbyte mh = 0;
            if (x.FileIndex == 0) mobFrom = x.Graphic;
            else
            {
                foreach (var (body, cols) in s.Columns.OrderBy(kv => kv.Key))
                {
                    var hit = cols.FirstOrDefault(k => k.Col == x.FileIndex && k.Graphic == x.Graphic);
                    if (hit.Col == 0) continue;
                    mobFrom = body;
                    mh = hit.MountHeight;
                    break;
                }
            }
            plan.Conv[x.Id] = (x.FileIndex, x.Graphic, mh);
            plan.ExtraMobTypeFrom[x.Id] = mobFrom;
            plan.BodyCount = Math.Max(plan.BodyCount, x.Id + 1);
        }
        return plan;
    }

    /// <summary>Reads Bodyconv.def columns, mount heights as ClassicUO's ProcessBodyConvDef sets them; <paramref name="lines"/> = every id with a line.</summary>
    public static Dictionary<int, List<Column>> ReadColumns(UOFileManager fm, out HashSet<int> lines)
    {
        var res = new Dictionary<int, List<Column>>();
        lines = new HashSet<int>();
        var file = fm.GetUOFilePath("Bodyconv.def");
        if (fm.Version < ClientVersion.CV_300 || !File.Exists(file)) return res;
        using var r = new DefReader(file);
        while (r.Next())
        {
            ushort index = (ushort)r.ReadInt();
            lines.Add(index);
            for (int i = 1; i < r.PartsCount; i++)
            {
                int body = r.ReadInt();
                if (body < 0) continue;
                if (i >= 10 || !AnimFileExists(fm, i)) continue;
                if (!res.TryGetValue(index, out var list)) res[index] = list = new();
                list.Add(new Column(i, (ushort)body, MountHeight(index, i)));
            }
        }
        return res;
    }

    public static sbyte MountHeight(int index, int col) => col switch
    {
        1 => index == 0x00C0 || index == 793 ? (sbyte)-9 : (sbyte)0,
        2 => index == 0x0579 ? (sbyte)9 : (sbyte)0,
        4 => index == 0x0115 || index == 0x00C0 ? (sbyte)0 : index == 0x042D ? (sbyte)3 : (sbyte)-9,
        _ => 0,
    };

    private static bool AnimFileExists(UOFileManager fm, int fileIndex)
    {
        string n = fileIndex == 0 ? "" : (fileIndex + 1).ToString();
        return File.Exists(fm.GetUOFilePath($"anim{n}.mul")) && File.Exists(fm.GetUOFilePath($"anim{n}.idx"));
    }

    public static bool HasFrames(ReadOnlySpan<AnimationsLoader.AnimationDirection> idx, bool uop)
    {
        foreach (var d in idx)
        {
            if (d.Position == 0 && d.Size == 0) continue;
            if (!uop && (d.Position == uint.MaxValue || d.Size == uint.MaxValue || d.Size == 0)) continue;
            return true;
        }
        return false;
    }

    /// <summary>Reads the import data and resolves <paramref name="c"/>; throws BodyChoicesException on a bad choice.</summary>
    public static BodyPlan Build(UOFileManager fm, BodyChoices c)
    {
        using var w = new AnimWorker(fm, null); // no conversions: GetIndices returns each body's own art
        var l = w.Loader;
        var s = new SourceData { BodyCount = AnimConvert.BodyCount(fm), Columns = ReadColumns(fm, out var lines) };
        foreach (var (body, g) in RevertedLbrRemodels)
            if (lines.Contains(body) && !s.Columns.ContainsKey(body) && AnimFileExists(fm, 1))
                s.Columns[body] = [new Column(1, g, MountHeight(body, 1))];

        (bool Uop, bool Frames) Own(int body)
        {
            ushort hue = 0;
            AnimationFlags flags = 0;
            var idx = l.GetIndices(fm.Version, (ushort)body, ref hue, ref flags, out _, out _);
            bool uop = (flags & AnimationFlags.UseUopAnimation) != 0;
            return (uop, HasFrames(idx, uop));
        }
        foreach (var body in s.Columns.Keys)
        {
            var (uop, frames) = Own(body);
            if (uop) s.Uop.Add(body);
            else if (frames) s.OwnArt.Add(body);
        }
        foreach (var id in c.Remodels.Keys.Where(id => !s.Columns.ContainsKey(id)))
            if (Own(id).Uop) s.Uop.Add(id);

        var bodyDef = DefTargets(fm, l, "Body.def", corpse: false);
        var corpseDef = DefTargets(fm, l, "Corpse.def", corpse: true);
        s.BodyDef.UnionWith(bodyDef.Keys);
        foreach (var x in c.Extras)
        {
            string? why = null;
            var (uop, frames) = Own(x.Id);
            if (uop) why = "a UOP body in mobtypes.txt";
            else if (frames) why = "it has its own anim.mul art";
            else if (l.HasMobType(x.Id)) why = "mobtypes.txt lists it";
            else if (lines.Contains(x.Id)) why = "Bodyconv.def has a line for it";
            else if (bodyDef.ContainsKey(x.Id)) why = "Body.def maps it to another body";
            else if (corpseDef.ContainsKey(x.Id)) why = "Corpse.def maps it to another body";
            else if (bodyDef.FirstOrDefault(kv => kv.Value == x.Id) is { Value: > 0 } b) why = $"Body.def uses it as the stand-in for {b.Key}";
            else if (corpseDef.FirstOrDefault(kv => kv.Value == x.Id) is { Value: > 0 } cb) why = $"Corpse.def uses it as the stand-in for {cb.Key}";
            else if (c.Extras.Any(o => o.Id != x.Id && o.FileIndex == 0 && o.Graphic == x.Id)) why = "another extra copies its art";
            if (why != null) s.InUse[x.Id] = why;
        }

        var plan = Resolve(s, c);

        // Each extra's variant must have frames.
        l.SetBodyConv(plan.Conv);
        foreach (var x in c.Extras)
        {
            l.CopyMobType(x.Id, plan.ExtraMobTypeFrom[x.Id]);
            ushort hue = 0;
            AnimationFlags flags = 0;
            var idx = l.GetIndices(fm.Version, (ushort)x.Id, ref hue, ref flags, out _, out _);
            if (!HasFrames(idx, false))
                throw new BodyChoicesException($"extras: id {x.Id}: {x.From} has no frames{(x.FileIndex > 0 && !AnimFileExists(fm, x.FileIndex) ? $" ({BodyExtra.FileName(x.FileIndex)} is not in this install)" : "")}");
        }
        return plan;
    }

    /// <summary>Body.def / Corpse.def: id -> the body it is replaced by (single hop, as the loader reads it).</summary>
    private static Dictionary<int, int> DefTargets(UOFileManager fm, AnimationsLoader l, string name, bool corpse)
    {
        var res = new Dictionary<int, int>();
        var p = fm.GetUOFilePath(name);
        if (!File.Exists(p)) return res;
        foreach (var line in File.ReadLines(p))
        {
            var t = line.TrimStart();
            int n = 0;
            while (n < t.Length && char.IsDigit(t[n])) n++;
            if (n == 0 || !int.TryParse(t.AsSpan(0, n), out var id) || id >= 0x10000) continue;
            ushort b = (ushort)id, hue = 0;
            if (corpse ? l.ReplaceCorpse(ref b, ref hue) : l.ReplaceBody(ref b, ref hue)) res[id] = b;
        }
        return res;
    }

    public override string ToString() =>
        $"{Choices}: remodels {RemodelsNew} new / {RemodelsOld} old, additions {AdditionsNew} converted / {AdditionsStandIn} Body.def stand-ins, {Choices.Extras.Count} extras; remodels: {string.Join(" ", RemodelIds.Select(b => OldRemodelIds.Contains(b) ? b + "(old)" : b.ToString()))}";
}
