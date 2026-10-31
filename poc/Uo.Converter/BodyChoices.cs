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

/// <summary>
/// An extra body: the frames of one variant (<c>anim.mul:76</c>, <c>anim2.mul:7</c>) copied into an unused id.
/// <paramref name="MountHeight"/>: given in bodies.json, or null for the default (see <see cref="BodyPlan.Resolve"/>).
/// </summary>
public sealed record BodyExtra(int Id, int FileIndex, ushort Graphic, sbyte? MountHeight = null)
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
    public static readonly string[] AdditionModes = ["standins", "newest"];
    public static readonly string[] SequenceModes = ["none", "target"];
    private static readonly string[] Keys = ["preset", "forceOldAnimations", "remodels", "additions", "sequences", "extras"];
    private static readonly string[] ExtraKeys = ["id", "from", "mountHeight"];

    /// <summary>
    /// The resolver policy, recorded in the manifest so --verify-only rebuilds a pack with the rules it was built with.
    /// 0 = legacy (packs up to classic-1.2, no bodies record): every Bodyconv.def column on, the last column wins, no
    /// built-in 9/26/28 conversion, no AnimationSequence for MUL bodies. 1 = bodies.json: eras and presets, remodels
    /// classified by their own anim.mul layout, the built-in 9/26/28 lines, opt-in target sequences.
    /// </summary>
    public const int LegacyPolicy = 0, CurrentPolicy = 1;

    public int Policy = CurrentPolicy;
    public string Preset = "aos";
    public bool ForceOldAnimations;
    /// <summary>"standins" (default): the era rule (an addition later than the preset draws its Body.def stand-in); "newest": never.</summary>
    public string Additions = "standins";
    /// <summary>
    /// "none" (default): MUL bodies use no AnimationSequence.uop (as ClassicUO). "target": a converted MUL body's actions
    /// follow its converted target's AnimationSequence (opt-in until the owner confirms in game which list the OSI client uses).
    /// </summary>
    public string Sequences = "none";
    /// <summary>Per remodel id: true = new (converted art), false = old (its own anim.mul art).</summary>
    public SortedDictionary<int, bool> Remodels = new();
    public List<BodyExtra> Extras = new();
    /// <summary>Where the choices came from ("default" or the file path) and the file's SHA-256 (empty for the default).</summary>
    public string Source = "default", Sha256 = "";

    public bool TargetSequences => Sequences == "target";

    public static BodyChoices Default => new();

    /// <summary>The rules every pack up to classic-1.2 was built with (a manifest without a bodies record).</summary>
    public static BodyChoices Legacy => new() { Policy = LegacyPolicy, Additions = "newest", Source = "legacy (manifest has no bodies record)" };

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
                    case "sequences":
                        c.Sequences = OneOf(v, SequenceModes, name, "sequences");
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
                        int pos = 0;
                        foreach (var e in v.EnumerateArray()) c.Extras.Add(ParseExtra(e, $"{name}: extras[{pos++}]"));
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

    /// <summary><paramref name="where"/> names the extra ("bodies.json: extras[2]"), so every error says which one.</summary>
    private static BodyExtra ParseExtra(JsonElement e, string where)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new BodyChoicesException($"{where}: each extra must be an object {{ \"id\": n, \"from\": \"anim.mul:76\" }}");
        int? id = null;
        string? from = null;
        sbyte? mountHeight = null;
        var seen = new HashSet<string>();
        foreach (var p in e.EnumerateObject())
        {
            if (!ExtraKeys.Contains(p.Name)) throw new BodyChoicesException($"{where}: unknown key \"{p.Name}\" in an extra (expected {string.Join(", ", ExtraKeys)})");
            if (!seen.Add(p.Name)) throw new BodyChoicesException($"{where}: \"{p.Name}\" is given twice in the same extra");
            switch (p.Name)
            {
                case "id":
                    id = p.Value.ValueKind switch
                    {
                        JsonValueKind.Number when p.Value.TryGetInt32(out var n) => ParseId(n.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{where}: extra id {n}"),
                        JsonValueKind.String => ParseId(p.Value.GetString()!, $"{where}: extra id \"{p.Value.GetString()}\""),
                        _ => throw new BodyChoicesException($"{where}: extra id must be a number, not {p.Value.GetRawText()}"),
                    };
                    break;
                case "from":
                    if (p.Value.ValueKind != JsonValueKind.String) throw new BodyChoicesException($"{where}: extra \"from\" must be a string like \"anim.mul:76\"");
                    from = p.Value.GetString();
                    break;
                case "mountHeight":
                    if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var mh) || mh < sbyte.MinValue || mh > sbyte.MaxValue)
                        throw new BodyChoicesException($"{where}: \"mountHeight\" must be a whole number from -128 to 127, not {p.Value.GetRawText()}");
                    mountHeight = (sbyte)mh;
                    break;
            }
        }
        if (id == null || from == null) throw new BodyChoicesException($"{where}: an extra needs both \"id\" and \"from\"");
        return ParseFrom(id.Value, from, mountHeight, where);
    }

    private static BodyExtra ParseFrom(int id, string from, sbyte? mountHeight, string where)
    {
        var m = FromPattern().Match(from);
        int fileIndex = m.Success ? (m.Groups[1].Value.Length == 0 ? 0 : int.TryParse(m.Groups[1].Value, out var n2) && n2 >= 2 && n2 <= 10 ? n2 - 1 : -1) : -1;
        if (fileIndex < 0)
            throw new BodyChoicesException($"{where}: extra {id}: \"from\" must be <file>:<body> with file anim.mul or anim2.mul..anim10.mul, not \"{from}\" (UOP bodies cannot be copied)");
        int graphic = ParseId(m.Groups[2].Value, $"{where}: extra {id}: \"from\" body \"{m.Groups[2].Value}\"");
        return new BodyExtra(id, fileIndex, (ushort)graphic, mountHeight);
    }

    public BodyChoicesRecord ToRecord() => new()
    {
        Policy = Policy, Preset = Preset, ForceOldAnimations = ForceOldAnimations, Additions = Additions, Sequences = Sequences,
        Remodels = Remodels.Select(kv => new BodyRemodelRecord { Id = kv.Key, Use = kv.Value ? "new" : "old" }).ToList(),
        Extras = Extras.Select(x => new BodyExtraRecord { Id = x.Id, From = x.From, MountHeight = x.MountHeight }).ToList(),
        Source = Source, Sha256 = Sha256,
    };

    /// <summary>
    /// The choices a pack was built with (its manifest). No record = a pack from before bodies.json, built with the
    /// legacy rules (<see cref="Legacy"/>), not today's default.
    /// </summary>
    public static BodyChoices FromRecord(BodyChoicesRecord? r)
    {
        if (r == null) return Legacy;
        if (r.Policy < CurrentPolicy)
            throw new BodyChoicesException($"manifest: the bodies record has resolver policy {r.Policy}, which no released converter wrote (a pre-release build); rebuild the anims (--only anims)");
        if (r.Policy > CurrentPolicy)
            throw new BodyChoicesException($"manifest: the bodies record has resolver policy {r.Policy}; this converter knows up to {CurrentPolicy}");
        if (!Presets.Contains(r.Preset) || !AdditionModes.Contains(r.Additions) || !SequenceModes.Contains(r.Sequences))
            throw new BodyChoicesException($"manifest: the bodies record has an unknown preset, additions or sequences value ({r.Preset}, {r.Additions}, {r.Sequences})");
        var c = new BodyChoices
        {
            Policy = r.Policy, Preset = r.Preset, ForceOldAnimations = r.ForceOldAnimations, Additions = r.Additions, Sequences = r.Sequences,
            Source = r.Source, Sha256 = r.Sha256,
        };
        foreach (var x in r.Remodels) c.Remodels[x.Id] = x.Use == "new";
        for (int i = 0; i < r.Extras.Count; i++)
        {
            var x = r.Extras[i];
            if (x.MountHeight is < sbyte.MinValue or > sbyte.MaxValue) throw new BodyChoicesException($"manifest: extras[{i}]: mount height {x.MountHeight} out of range");
            c.Extras.Add(ParseFrom(x.Id, x.From, (sbyte?)x.MountHeight, $"manifest: extras[{i}]"));
        }
        return c;
    }

    public override string ToString() =>
        Policy == LegacyPolicy
            ? $"legacy rules (policy 0: every conversion, no 9/26/28 lines, no sequences; {Source})"
            : $"policy {Policy}, preset {Preset}{(ForceOldAnimations ? " + forceOldAnimations" : "")}, additions {Additions}, sequences {Sequences}, {Remodels.Count} remodel overrides, {Extras.Count} extras ({Source}{(Sha256.Length > 0 ? " sha256 " + Sha256[..12] : "")})";
}

/// <summary>
/// Bodyconv.def resolved per the import choices: which conversion (file, graphic, mount height) each body id gets.
/// A <b>remodel</b> is a body with usable art of its own in anim.mul (walk and stand frames in its own anim.mul layout)
/// and a conversion; "old" draws the own art, "new" the converted art (last column). Every other converted body is an
/// <b>addition</b>; its only older look is a Body.def stand-in, if it has one. Eras: see <see cref="Era"/>.
/// </summary>
public sealed class BodyPlan
{
    /// <summary>A Bodyconv.def target. <paramref name="Era"/>: see <see cref="Era"/>.</summary>
    public readonly record struct Column(int Col, ushort Graphic, sbyte MountHeight, int Era);

    /// <summary>Eras (owner, 2026-10-31): 0 T2A/UOR (anim.mul), then by Bodyconv.def column: 1 anim2 LBR, 2 anim3 AOS, 3 SE, 4 ML, 5 later.</summary>
    public const int EraT2a = 0, EraLbr = 1, EraAos = 2, EraSe = 3, EraMl = 4, EraLater = 5;
    public static readonly string[] EraNames = ["T2A", "LBR", "AOS", "SE", "ML", "later"];

    public static int Era(int col) => Math.Clamp(col, EraLbr, EraLater);

    /// <summary>The newest era a preset shows: t2a/uor nothing converted, lbr column 1, aos everything.</summary>
    public static int PresetEra(string preset) => preset switch { "t2a" or "uor" => EraT2a, "lbr" => EraLbr, _ => int.MaxValue };

    /// <summary>
    /// The era of every remodel for the presets (owner, 2026-10-31: most LBR admins don't want the daemon, spider and other
    /// remodels, so they count as AOS: lbr shows them old, aos new), whatever column their art is in. One constant so the
    /// owner can move it.
    /// </summary>
    public const int RemodelEra = EraAos;

    /// <summary>
    /// forceOldAnimations: which remodels it turns old. Owner, 2026-10-31 (still under investigation): every remodel.
    /// Additions are never affected. One rule, so it is easy to change (e.g. <c>RemodelEra &gt;= EraAos</c>).
    /// </summary>
    public static bool ForceOldReverts(int body, List<Column> cols) => true;

    /// <summary>
    /// 7.x Bodyconv.def turns these three back with all -1 lines ("reverts the Daemon back to the original style"), yet
    /// the OSI client still draws the anim2 art unless old animations are forced (owner, 2026-10-31; survey
    /// cuo-poc-state/surveys/bodies.md §6). They are remodels with their original column-1 targets.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, ushort> RevertedLbrRemodels = new Dictionary<int, ushort> { [9] = 7, [26] = 24, [28] = 5 };

    /// <summary>The era of the built-in 9/26/28 columns (the owner places them at the end of LBR). Only an addition's era is read from it.</summary>
    public const int RevertedEra = EraLbr;

    private const AnimationFlags LayoutFlags = AnimationFlags.CalculateOffsetByPeopleGroup | AnimationFlags.CalculateOffsetByLowGroup | AnimationFlags.CalculateOffsetLowGroupExtended;

    /// <summary>What the import data says, independent of the choices.</summary>
    public sealed class SourceData
    {
        /// <summary>Per body with a Bodyconv.def conversion: its columns (1 = anim2 ...) in file order, files present only.</summary>
        public Dictionary<int, List<Column>> Columns = new();
        public HashSet<int> Uop = new(), OwnArt = new(), BodyDef = new();
        /// <summary>Remodels whose own anim.mul layout differs from their mobtypes.txt layout: the kind and flags that read it.</summary>
        public Dictionary<int, (AnimationGroupsType Type, AnimationFlags Flags)> OwnLayout = new();
        /// <summary>Per own-art body: a description of its old art, for the report ("anim.mul:9 (Monster layout)").</summary>
        public Dictionary<int, string> OldSource = new();
        /// <summary>Converted bodies with index entries in their mobtypes layout but no usable art in their own layout (not remodels).</summary>
        public SortedSet<int> MisreadSlots = new();
        /// <summary>Body.def stand-ins: body -> (target, hue).</summary>
        public Dictionary<int, (int Body, int Hue)> StandIn = new();
        /// <summary>For each extra id: why it is already in use (absent = free).</summary>
        public Dictionary<int, string> InUse = new();
        public int BodyCount;
    }

    public BodyChoices Choices = BodyChoices.Default;
    public Dictionary<int, (int FileIndex, ushort Graphic, sbyte MountHeight)> Conv = new();
    /// <summary>mobtypes.txt entries the plan installs: old remodels read in their own layout, and every extra.</summary>
    public Dictionary<int, (AnimationGroupsType Type, AnimationFlags Flags)> MobTypes = new();
    /// <summary>Extra id -> the body whose kind and flags it inherits; -1 = none.</summary>
    public Dictionary<int, int> ExtraMobTypeFrom = new();
    public int BodyCount;
    public int RemodelsOld, RemodelsNew, AdditionsStandIn, AdditionsNew;
    /// <summary>Every remodel id, those that draw their old art, and the additions drawn as their Body.def stand-in.</summary>
    public List<int> RemodelIds = new(), OldRemodelIds = new(), StandInIds = new();
    public SourceData Source = new();

    /// <summary>The newest column whose era the preset shows (null = none).</summary>
    public static Column? ByEra(List<Column> cols, int era)
    {
        for (int i = cols.Count - 1; i >= 0; i--)
            if (cols[i].Era <= era) return cols[i];
        return null;
    }

    /// <summary>The pure resolution step (tests use it on synthetic data).</summary>
    public static BodyPlan Resolve(SourceData s, BodyChoices c)
    {
        foreach (var id in c.Remodels.Keys)
        {
            if (s.Uop.Contains(id)) throw new BodyChoicesException($"remodels: body {id} draws UOP frames (mobtypes 0x10000), which are never a remodel");
            if (!s.Columns.ContainsKey(id)) throw new BodyChoicesException($"remodels: body {id} has no Bodyconv.def conversion, so it is not a remodel");
            if (!s.OwnArt.Contains(id))
                throw new BodyChoicesException($"remodels: body {id} has no anim.mul art of its own (no walk and stand frames in its own layout){(s.StandIn.TryGetValue(id, out var si) ? $"; its older look is the Body.def stand-in {si.Body} hue {si.Hue}" : "")}, so it is an addition (set those with \"additions\")");
        }
        var plan = new BodyPlan { Choices = c, BodyCount = s.BodyCount, Source = s };
        int presetEra = PresetEra(c.Preset);
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
                if (c.Remodels.TryGetValue(body, out var isNew)) chosen = isNew ? last : null;
                else if (c.ForceOldAnimations && ForceOldReverts(body, cols)) chosen = null;
                else chosen = RemodelEra <= presetEra ? last : null;
                plan.RemodelIds.Add(body);
                if (chosen == null)
                {
                    plan.RemodelsOld++;
                    plan.OldRemodelIds.Add(body);
                    if (s.OwnLayout.TryGetValue(body, out var layout)) plan.MobTypes[body] = layout;
                }
                else plan.RemodelsNew++;
            }
            else
            {
                // newest: the last column, always. standins (the era rule): the newest column the preset's era shows; an
                // addition later than that draws its Body.def stand-in, or keeps its art when it has none (never invisible).
                chosen = c.Additions == "newest" ? last : ByEra(cols, presetEra) ?? (s.BodyDef.Contains(body) ? null : last);
                if (chosen == null) { plan.AdditionsStandIn++; plan.StandInIds.Add(body); }
                else plan.AdditionsNew++;
            }
            if (chosen is { } ch) plan.Conv[body] = (ch.Col, ch.Graphic, ch.MountHeight);
        }
        foreach (var x in c.Extras)
        {
            if (s.InUse.TryGetValue(x.Id, out var why)) throw new BodyChoicesException($"extras: id {x.Id} is already in use ({why}); pick an id nothing else uses");
            if (s.Uop.Contains(x.Id)) throw new BodyChoicesException($"extras: id {x.Id} is already in use (UOP body)");
            // Kind and flags: anim.mul -> that body; animN -> the lowest non-UOP body converting to it (the layout that
            // art is drawn with today), else the lowest UOP one. Mount height: given, else that body's column, else 0.
            int mobFrom = -1;
            sbyte mh = 0;
            if (x.FileIndex == 0) mobFrom = x.Graphic;
            else
            {
                foreach (bool uop in new[] { false, true })
                {
                    foreach (var (body, cols) in s.Columns.OrderBy(kv => kv.Key))
                    {
                        if (s.Uop.Contains(body) != uop) continue;
                        var hit = cols.FirstOrDefault(k => k.Col == x.FileIndex && k.Graphic == x.Graphic);
                        if (hit.Col == 0) continue;
                        mobFrom = body;
                        mh = hit.MountHeight;
                        break;
                    }
                    if (mobFrom >= 0) break;
                }
            }
            plan.Conv[x.Id] = (x.FileIndex, x.Graphic, x.MountHeight ?? mh);
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
                list.Add(new Column(i, (ushort)body, MountHeight(index, i), Era(i)));
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

    /// <summary>
    /// True when a MUL body has usable walk and stand art: every stored direction of both groups decodes to at least one
    /// non-empty frame (walk 0; stand 1 for 22-group monsters, 2 for 13-group animals, 4 for 35-group people). An index
    /// entry alone is not enough.
    /// </summary>
    public static bool Usable(UOFileManager fm, AnimationsLoader l, AnimConvert.BodyMeta m)
    {
        if (m.Uop || m.Idx.Length == 0) return false;
        int stand = (m.Idx.Length / AnimationsLoader.MAX_DIRECTIONS) switch { 22 => 1, 13 => 2, 35 => 4, _ => -1 };
        if (stand < 0) return false;
        foreach (var g in new[] { 0, stand })
            for (int d = 0; d < AnimationsLoader.MAX_DIRECTIONS; d++)
            {
                var job = AnimConvert.Job(l, m, g, d);
                if (job == null) return false;
                var fr = AnimConvert.ReadFrames(fm, job);
                if (fr is not { Length: > 0 } || fr[0].W <= 0 || fr[0].H <= 0) return false;
            }
        return true;
    }

    private static readonly Dictionary<int, (int, ushort, sbyte)> NoConv = new();

    /// <summary>
    /// A body's own anim.mul art, read in the layout it has in anim.mul: the pre-mobtypes layout by id (0-199 monster,
    /// 200-399 animal, 400+ people), not today's mobtypes.txt layout, which for some bodies starts inside another body's
    /// slots (253, 689, 704, 318, 319, the mount ids on 7.0.103). Returns whether it is usable, and the kind/flags to read
    /// it with when the mobtypes layout differs.
    /// </summary>
    private static (bool Usable, (AnimationGroupsType, AnimationFlags)? Layout) OwnArt(UOFileManager fm, AnimationsLoader l, int body)
    {
        bool had = l.TryGetMobType(body, out var t0, out var f0);
        var mobIdx = AnimConvert.Meta(l, fm.Version, body, NoConv, false).Idx;
        var type = l.TypeByGraphic((ushort)body, 0);
        var flags = f0 & ~LayoutFlags & ~AnimationFlags.UseUopAnimation;
        l.SetMobType(body, type, flags);
        try
        {
            var m = AnimConvert.Meta(l, fm.Version, body, NoConv, false);
            bool same = m.Idx.Length == mobIdx.Length;
            for (int i = 0; same && i < mobIdx.Length; i++) same = m.Idx[i].Position == mobIdx[i].Position && m.Idx[i].Size == mobIdx[i].Size;
            return (Usable(fm, l, m), same ? null : (type, flags));
        }
        finally
        {
            if (had) l.SetMobType(body, t0, f0);
            else l.RemoveMobType(body);
        }
    }

    /// <summary>Reads the import data and resolves <paramref name="c"/>; throws BodyChoicesException on a bad choice.</summary>
    public static BodyPlan Build(UOFileManager fm, BodyChoices c)
    {
        using var w = new AnimWorker(fm, null); // no conversions: GetIndices returns each body's own art
        var l = w.Loader;
        var s = new SourceData { BodyCount = AnimConvert.BodyCount(fm), Columns = ReadColumns(fm, out var lines) };
        if (c.Policy >= 1)
            foreach (var (body, g) in RevertedLbrRemodels)
                if (lines.Contains(body) && !s.Columns.ContainsKey(body) && AnimFileExists(fm, 1))
                    s.Columns[body] = [new Column(1, g, MountHeight(body, 1), RevertedEra)];

        var bodyDef = DefTargets(fm, l, "Body.def", corpse: false);
        var corpseDef = DefTargets(fm, l, "Corpse.def", corpse: true);
        s.BodyDef.UnionWith(bodyDef.Keys);
        foreach (var (k, v) in bodyDef) s.StandIn[k] = v;

        foreach (var body in s.Columns.Keys)
        {
            var mob = AnimConvert.Meta(l, fm.Version, body, NoConv, false);
            if (mob.Uop) { s.Uop.Add(body); continue; }
            var (usable, layout) = OwnArt(fm, l, body);
            if (usable)
            {
                s.OwnArt.Add(body);
                if (layout is { } lt) s.OwnLayout[body] = lt;
                var natural = l.TypeByGraphic((ushort)body, 0);
                s.OldSource[body] = $"anim.mul:{body} ({natural} layout{(layout != null && l.TryGetMobType(body, out var mt, out _) ? $"; mobtypes.txt says {mt}" : "")})";
            }
            else if (HasFrames(mob.Idx, false)) s.MisreadSlots.Add(body);
        }
        foreach (var id in c.Remodels.Keys.Where(id => !s.Columns.ContainsKey(id)))
            if (AnimConvert.Meta(l, fm.Version, id, NoConv, false).Uop) s.Uop.Add(id);

        if (c.Extras.Count > 0)
        {
            var reserved = ReservedAnimIds(fm, l);
            foreach (var x in c.Extras)
            {
                string? why = null;
                var own = AnimConvert.Meta(l, fm.Version, x.Id, NoConv, false);
                if (own.Uop) why = "a UOP body in mobtypes.txt";
                else if (HasFrames(own.Idx, false) || OwnArt(fm, l, x.Id).Usable) why = "it has its own anim.mul art";
                else if (l.HasMobType(x.Id)) why = "mobtypes.txt lists it";
                else if (lines.Contains(x.Id)) why = "Bodyconv.def has a line for it";
                else if (bodyDef.ContainsKey(x.Id)) why = "Body.def maps it to another body";
                else if (corpseDef.ContainsKey(x.Id)) why = "Corpse.def maps it to another body";
                else if (bodyDef.FirstOrDefault(kv => kv.Value.Body == x.Id) is { Key: var bk, Value.Body: > 0 }) why = $"Body.def uses it as the stand-in for {bk}";
                else if (corpseDef.FirstOrDefault(kv => kv.Value.Body == x.Id) is { Key: var ck, Value.Body: > 0 }) why = $"Corpse.def uses it as the stand-in for {ck}";
                else if (reserved.TryGetValue(x.Id, out var r)) why = r;
                else if (c.Extras.Any(o => o.Id != x.Id && o.FileIndex == 0 && o.Graphic == x.Id)) why = "another extra copies its art";
                if (why != null) s.InUse[x.Id] = why;
            }
        }

        var plan = Resolve(s, c);

        // Extras: kind and behaviour flags are inherited, never the source selection. UseUopAnimation is cleared (an
        // explicit MUL source must not turn into a UOP lookup), and an anim.mul source is read in its own layout.
        for (int i = 0; i < c.Extras.Count; i++)
        {
            var x = c.Extras[i];
            if (x.FileIndex > 0 && !AnimFileExists(fm, x.FileIndex))
                throw new BodyChoicesException($"extras[{i}] (id {x.Id}): {x.From}: {BodyExtra.FileName(x.FileIndex)} is not in this install");
            int from = plan.ExtraMobTypeFrom[x.Id];
            bool found = from >= 0 & l.TryGetMobType(from, out var type, out var flags);
            flags &= ~AnimationFlags.UseUopAnimation;
            if (x.FileIndex == 0) { type = l.TypeByGraphic(x.Graphic, 0); flags &= ~LayoutFlags; }
            else if (!found) { type = l.TypeByGraphic(x.Graphic, x.FileIndex); flags = AnimationFlags.None; }
            plan.MobTypes[x.Id] = (type, flags);
        }

        // Install the plan and check every extra draws the requested MUL source with usable frames.
        l.SetBodyConv(plan.Conv);
        foreach (var (id, mt) in plan.MobTypes) l.SetMobType(id, mt.Type, mt.Flags);
        for (int i = 0; i < c.Extras.Count; i++)
        {
            var x = c.Extras[i];
            var m = AnimConvert.Meta(l, fm.Version, x.Id, plan.Conv, false);
            if (m.Uop || m.FileIndex != x.FileIndex || m.SourceBody != x.Graphic)
                throw new BodyChoicesException($"extras[{i}] (id {x.Id}): would not draw {x.From} (resolves to {(m.Uop ? "UOP" : BodyExtra.FileName(m.FileIndex))}:{m.SourceBody})");
            if (!Usable(fm, l, m))
                throw new BodyChoicesException($"extras[{i}] (id {x.Id}): {x.From} has no frames (no walk and stand art in the {plan.MobTypes[x.Id].Type} layout{(plan.ExtraMobTypeFrom[x.Id] >= 0 && x.FileIndex > 0 ? $" of body {plan.ExtraMobTypeFrom[x.Id]}, which converts to it" : "")})");
        }
        return plan;
    }

    /// <summary>
    /// Anim ids that equipment already draws: nonzero tiledata AnimIDs (wearables, mounts) and Equipconv.def sources and
    /// targets. An extra there would change how those items render. Value: which item or line uses it.
    /// </summary>
    private static Dictionary<int, string> ReservedAnimIds(UOFileManager fm, AnimationsLoader l)
    {
        var res = new Dictionary<int, string>();
        if (fm.TileData.StaticData is not { Length: > 0 }) fm.TileData.Load();
        var statics = fm.TileData.StaticData;
        for (int i = 0; i < statics.Length; i++)
        {
            int a = statics[i].AnimID;
            if (a != 0) res.TryAdd(a, $"tiledata item 0x{i:X4} \"{statics[i].Name?.Trim()}\" uses it as its AnimID");
        }
        foreach (var (body, dict) in l.EquipConversions)
            foreach (var (graphic, data) in dict)
            {
                res.TryAdd(data.Graphic, $"Equipconv.def converts equipment anim {graphic} to it on body {body}");
                res.TryAdd(graphic, $"Equipconv.def converts it to equipment anim {data.Graphic} on body {body}");
            }
        return res;
    }

    /// <summary>Body.def / Corpse.def: id -> the body it is replaced by and the hue (single hop, as the loader reads it).</summary>
    private static Dictionary<int, (int Body, int Hue)> DefTargets(UOFileManager fm, AnimationsLoader l, string name, bool corpse)
    {
        var res = new Dictionary<int, (int, int)>();
        var p = fm.GetUOFilePath(name);
        if (!File.Exists(p)) return res;
        foreach (var line in File.ReadLines(p))
        {
            var t = line.TrimStart();
            int n = 0;
            while (n < t.Length && char.IsDigit(t[n])) n++;
            if (n == 0 || !int.TryParse(t.AsSpan(0, n), out var id) || id >= 0x10000) continue;
            ushort b = (ushort)id, hue = 0;
            if (corpse ? l.ReplaceCorpse(ref b, ref hue) : l.ReplaceBody(ref b, ref hue)) res[id] = (b, hue);
        }
        return res;
    }

    /// <summary>Console report: each remodel's old and new source, the stand-ins drawn, and the bodies that are not remodels although their mobtypes layout finds index entries.</summary>
    public IEnumerable<string> Describe()
    {
        var s = Source;
        yield return $"remodels ({RemodelIds.Count}; own walk/stand art in their own anim.mul layout): " + string.Join(", ", RemodelIds.Select(b =>
        {
            var last = s.Columns[b][^1];
            return $"{b} old {s.OldSource.GetValueOrDefault(b, "anim.mul:" + b)} / new {BodyExtra.FileName(last.Col)}:{last.Graphic}{(OldRemodelIds.Contains(b) ? " [old]" : " [new]")}";
        }));
        if (StandInIds.Count > 0)
            yield return $"additions drawn as their Body.def stand-in ({StandInIds.Count}): " + string.Join(" ", StandInIds.Select(b => $"{b}->{s.StandIn[b].Body}"));
        if (s.MisreadSlots.Count > 0)
            yield return $"not remodels (their mobtypes.txt layout reads other bodies' anim.mul slots; no walk/stand art in their own layout): " +
                         string.Join(" ", s.MisreadSlots.Select(b => s.StandIn.TryGetValue(b, out var si) ? $"{b} (stand-in {si.Body} hue {si.Hue})" : $"{b} (no stand-in)"));
    }

    public override string ToString() =>
        $"{Choices}: remodels {RemodelsNew} new / {RemodelsOld} old, additions {AdditionsNew} converted / {AdditionsStandIn} Body.def stand-ins, {Choices.Extras.Count} extras; remodels: {string.Join(" ", RemodelIds.Select(b => OldRemodelIds.Contains(b) ? b + "(old)" : b.ToString()))}";
}
