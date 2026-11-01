using System.Runtime.InteropServices;

namespace Uo.Pack;

// T3b (ecs-transition-plan.md, T3 "Equipment layering as data"): the worn-layer draw order and covering rules as data
// (layerrules.bin, magic LAYR). Layers are the client's Layer byte values (0x01 OneHanded .. 0x18 Legs). See
// docs/modern-client/pack-format.md, "layerrules.bin", for the format and the evaluation rules.

public static class LayerRulesConst
{
    public const uint Magic = 0x5259414C; // "LAYR"
    public const string FileName = "layerrules.bin";

    /// <summary>Layers in a draw-order table (0x00..0x18, entry 0 is the Invalid sentinel), PaperdollOrder.N.</summary>
    public const int LayerCount = 25;

    /// <summary>Cover targets are indexed below this.</summary>
    public const int MaxLayer = 32;

    public const byte FlagFemale = 1;
    public const byte FlagGargoyleDraw = 2;

    /// <summary>The client is 7.0.0.0 or later (the paperdoll's gargoyle-ghost shroud remap).</summary>
    public const byte FlagCv7000 = 4;

    /// <summary>DirMove mask bit for any direction of 8 or more.</summary>
    public const ushort DirOther = 1 << 8;
}

public enum LayerRuleSet : byte { Paperdoll = 0, World = 1 }

public enum LayerRuleKind : byte { Select = 0, Reorder = 1, Cover = 2, DirMove = 3, Remap = 4 }

public enum LayerReorderOp : byte { MoveTo = 0, MoveAfter = 1, SwapIfAfter = 2 }

public enum LayerDirOp : byte { Top = 0, Bottom = 1, Before = 2 }

public enum LayerCondKind : byte { Present = 0, Graphic = 1, AnimId = 2, Flag = 3, Group = 4, Body = 5 }

public enum BodyGroup : byte { Other = 0, HumanMale = 1, HumanFemale = 2, ElfMale = 3, ElfFemale = 4, GargoyleMale = 5, GargoyleFemale = 6 }

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LayerRulesHeader
{
    public int BodyCount, LayerCount, TableCount, RuleCount, CondCount, RangeCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BodyGroupEntry
{
    public ushort Body;
    public byte Group;
    public byte Pad;
}

/// <summary>12 bytes. Meaning of the fields per <see cref="LayerRuleKind"/>: see pack-format.md.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LayerRule
{
    public byte Set, Kind, Layer, Other, Op, CondCount;
    public ushort CondStart, Value, Value2;
}

/// <summary>8 bytes. True when the value is in any of the ranges (Present and Flag ignore ranges); Negate inverts.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LayerCond
{
    public byte Kind, Layer, Negate, RangeCount;
    public ushort RangeStart, Pad;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LayerRange
{
    public ushort Lo, Hi;
}

/// <summary>Who wears the layers: the body group (from the file's body table), the body graphic and the caller's flags
/// (female, gargoyle draw, client 7.0.0.0+).</summary>
public struct LayerSubject
{
    public byte Group;
    public byte Flags;
    public ushort Body;

    public LayerSubject(byte group, byte flags, ushort body = 0)
    {
        Group = group;
        Flags = flags;
        Body = body;
    }
}

/// <summary>
/// The loaded rules and their evaluator. Allocation-free after load. Inputs are per-layer tables indexed by layer byte:
/// <c>present</c>, item <c>graphic</c> and tiledata <c>anim</c> (an absent layer reads 0). Order rules read the order key
/// (AnimIDs): pass it as both graphic and anim with an empty <c>present</c> (present = key != 0).
/// </summary>
public sealed class LayerRules
{
    public readonly BodyGroupEntry[] Bodies;
    public readonly byte[] Tables;
    public readonly LayerRule[] Rules;
    public readonly LayerCond[] Conds;
    public readonly LayerRange[] Ranges;

    // per set: rule indices by kind; covers by (set, target layer)
    private readonly int[][] _select = new int[2][], _reorder = new int[2][], _dirMove = new int[2][], _remap = new int[2][];
    private readonly int[] _coverIdx;
    private readonly int[] _coverStart = new int[2 * LayerRulesConst.MaxLayer + 1];

    // per set: a 1024-bit filter of the remapped AnimIDs (bit anim & 1023), so the common no-remap case skips the rule scan
    private readonly ulong[] _remapFilter = new ulong[2 * 16];

    // E2 (perf): body -> group without the table scan; per rule, the layers that must be worn for it to match (a Present
    // condition, or an AnimId/Graphic one whose ranges exclude 0, not negated), checked against the key's worn mask first
    private readonly byte[] _groupByBody;
    private readonly uint[] _require;

    public LayerRules(BodyGroupEntry[] bodies, byte[] tables, LayerRule[] rules, LayerCond[] conds, LayerRange[] ranges)
    {
        Bodies = bodies;
        Tables = tables;
        Rules = rules;
        Conds = conds;
        Ranges = ranges;

        int tableCount = tables.Length / LayerRulesConst.LayerCount;
        if (tables.Length % LayerRulesConst.LayerCount != 0) throw new InvalidDataException("layerrules: table bytes not a multiple of 25");

        foreach (byte l in tables)
        {
            if (l >= LayerRulesConst.LayerCount) throw new InvalidDataException($"layerrules: table layer {l} out of range");
        }

        for (int t = 0; t < tableCount; t++)
        {
            // a table lists each layer once (the reorder primitives find a layer's first index)
            Span<bool> seen = stackalloc bool[LayerRulesConst.LayerCount];
            for (int i = 0; i < LayerRulesConst.LayerCount; i++)
            {
                byte l = tables[t * LayerRulesConst.LayerCount + i];
                if (seen[l]) throw new InvalidDataException($"layerrules: table {t} lists layer {l} twice");
                seen[l] = true;
            }
        }

        foreach (ref readonly LayerCond c in conds.AsSpan())
        {
            if (c.Kind > (byte)LayerCondKind.Body) throw new InvalidDataException($"layerrules: cond kind {c.Kind}");
            // the evaluator's tables hold the layers 0..LayerCount-1 (PaperdollOrder.N entries)
            if (c.Kind <= (byte)LayerCondKind.AnimId && c.Layer >= LayerRulesConst.LayerCount) throw new InvalidDataException($"layerrules: cond layer {c.Layer}");
            if (c.RangeStart + c.RangeCount > ranges.Length) throw new InvalidDataException("layerrules: cond ranges out of bounds");
        }

        int maxBody = 0;
        foreach (BodyGroupEntry e in bodies) maxBody = Math.Max(maxBody, e.Body);
        _groupByBody = new byte[maxBody + 1];
        for (int i = bodies.Length - 1; i >= 0; i--) _groupByBody[bodies[i].Body] = bodies[i].Group; // the first entry wins, as GroupOf's scan

        _require = new uint[rules.Length];

        var lists = new List<int>[2, 5];
        var covers = new List<int>[2 * LayerRulesConst.MaxLayer];
        for (int s = 0; s < 2; s++)
        for (int k = 0; k < 5; k++) lists[s, k] = new List<int>();
        for (int i = 0; i < covers.Length; i++) covers[i] = new List<int>();

        for (int i = 0; i < rules.Length; i++)
        {
            ref readonly LayerRule r = ref rules[i];
            if (r.Set > 1 || r.Kind > (byte)LayerRuleKind.Remap) throw new InvalidDataException($"layerrules: rule {i} set {r.Set} kind {r.Kind}");
            if (r.CondStart + r.CondCount > conds.Length) throw new InvalidDataException($"layerrules: rule {i} conds out of bounds");

            for (int c = r.CondStart; c < r.CondStart + r.CondCount; c++)
            {
                LayerCond cd = conds[c];
                if (cd.Negate != 0 || cd.Kind > (byte)LayerCondKind.AnimId) continue;
                bool zeroMatches = false;
                for (int k = cd.RangeStart; k < cd.RangeStart + cd.RangeCount; k++) zeroMatches |= ranges[k].Lo == 0;
                if (cd.Kind == (byte)LayerCondKind.Present || !zeroMatches) _require[i] |= 1u << cd.Layer;
            }

            switch ((LayerRuleKind)r.Kind)
            {
                case LayerRuleKind.Select:
                    if (r.Value >= tableCount) throw new InvalidDataException($"layerrules: rule {i} table {r.Value}");
                    break;
                case LayerRuleKind.Reorder:
                    if (r.Op > (byte)LayerReorderOp.SwapIfAfter || r.Layer >= LayerRulesConst.LayerCount || r.Other >= LayerRulesConst.LayerCount)
                        throw new InvalidDataException($"layerrules: rule {i} reorder");
                    break;
                case LayerRuleKind.Cover:
                    if (r.Layer >= LayerRulesConst.MaxLayer || r.Other > 1) throw new InvalidDataException($"layerrules: rule {i} cover");
                    covers[r.Set * LayerRulesConst.MaxLayer + r.Layer].Add(i);
                    continue;
                case LayerRuleKind.DirMove:
                    if (r.Op > (byte)LayerDirOp.Before) throw new InvalidDataException($"layerrules: rule {i} dirmove");
                    break;
                case LayerRuleKind.Remap:
                    // a remap sees only the wearer (flags, group), not the other layers
                    for (int c = r.CondStart; c < r.CondStart + r.CondCount; c++)
                    {
                        if (conds[c].Kind < (byte)LayerCondKind.Flag) throw new InvalidDataException($"layerrules: remap rule {i} reads a layer");
                    }

                    _remapFilter[r.Set * 16 + ((r.Value & 1023) >> 6)] |= 1UL << (r.Value & 63);
                    break;
            }

            lists[r.Set, r.Kind].Add(i);
        }

        for (int s = 0; s < 2; s++)
        {
            _select[s] = lists[s, (int)LayerRuleKind.Select].ToArray();
            _reorder[s] = lists[s, (int)LayerRuleKind.Reorder].ToArray();
            _dirMove[s] = lists[s, (int)LayerRuleKind.DirMove].ToArray();
            _remap[s] = lists[s, (int)LayerRuleKind.Remap].ToArray();

            // the base table must always resolve: the last select is unconditional
            int[] sel = _select[s];
            if (sel.Length == 0 || rules[sel[^1]].CondCount != 0) throw new InvalidDataException($"layerrules: set {s} has no unconditional (default) select");
        }

        var idx = new List<int>();
        for (int i = 0; i < covers.Length; i++)
        {
            _coverStart[i] = idx.Count;
            idx.AddRange(covers[i]);
        }

        _coverStart[covers.Length] = idx.Count;
        _coverIdx = idx.ToArray();
    }

    /// <summary>The body group of a body graphic (<see cref="BodyGroup.Other"/> when the file doesn't list it).</summary>
    public byte GroupOf(ushort body) => body < _groupByBody.Length ? _groupByBody[body] : (byte)BodyGroup.Other;

    // the worn mask of an order key (bit l: key[l] != 0)
    private static uint WornMask(ReadOnlySpan<ushort> key)
    {
        uint m = 0;
        int n = Math.Min(key.Length, LayerRulesConst.LayerCount);
        for (int l = 1; l < n; l++) m |= key[l] != 0 ? 1u << l : 0;
        return m;
    }

    private bool Match(in LayerRule r, ReadOnlySpan<bool> present, ReadOnlySpan<ushort> graphic, ReadOnlySpan<ushort> anim, LayerSubject subject)
    {
        int end = r.CondStart + r.CondCount;
        for (int i = r.CondStart; i < end; i++)
        {
            ref readonly LayerCond c = ref Conds[i];
            bool result;

            switch ((LayerCondKind)c.Kind)
            {
                case LayerCondKind.Flag:
                    result = (subject.Flags & c.Layer) != 0;
                    break;
                case LayerCondKind.Group:
                    result = InRanges(c, subject.Group);
                    break;
                case LayerCondKind.Body:
                    result = InRanges(c, subject.Body);
                    break;
                default:
                {
                    bool p = present.IsEmpty ? anim[c.Layer] != 0 : present[c.Layer];
                    if (c.Kind == (byte)LayerCondKind.Present) result = p;
                    else result = InRanges(c, !p ? (ushort)0 : c.Kind == (byte)LayerCondKind.Graphic ? graphic[c.Layer] : anim[c.Layer]);
                    break;
                }
            }

            if (result == (c.Negate != 0)) return false;
        }

        return true;
    }

    private bool InRanges(in LayerCond c, ushort v)
    {
        int end = c.RangeStart + c.RangeCount;
        for (int i = c.RangeStart; i < end; i++)
        {
            if (v >= Ranges[i].Lo && v <= Ranges[i].Hi) return true;
        }

        return false;
    }

    /// <summary>MobileView.IsCovered / AnimationRules.IsCovered: the first matching cover rule for the layer, else not covered.</summary>
    public bool IsCovered(LayerRuleSet set, byte layer, ReadOnlySpan<bool> present, ReadOnlySpan<ushort> graphic, ReadOnlySpan<ushort> anim, LayerSubject subject)
    {
        if (layer >= LayerRulesConst.MaxLayer) return false;
        int k = (int)set * LayerRulesConst.MaxLayer + layer;

        for (int i = _coverStart[k]; i < _coverStart[k + 1]; i++)
        {
            ref readonly LayerRule r = ref Rules[_coverIdx[i]];
            if (Match(r, present, graphic, anim, subject)) return r.Other != 0;
        }

        return false;
    }

    /// <summary>PaperdollOrder.Build: the raw back-to-front order (25 entries, Invalid first) for the order key (AnimIDs per layer).</summary>
    public void BuildOrder(LayerRuleSet set, ReadOnlySpan<ushort> key, LayerSubject subject, Span<byte> order)
    {
        const int N = LayerRulesConst.LayerCount;
        int table = 0;
        uint worn = WornMask(key);

        foreach (int ri in _select[(int)set])
        {
            ref readonly LayerRule r = ref Rules[ri];
            if ((_require[ri] & ~worn) == 0 && Match(r, default, key, key, subject))
            {
                table = r.Value;
                break;
            }
        }

        Tables.AsSpan(table * N, N).CopyTo(order);
        Span<byte> o = order.Slice(0, N);

        foreach (int ri in _reorder[(int)set])
        {
            ref readonly LayerRule r = ref Rules[ri];
            if ((_require[ri] & ~worn) != 0 || !Match(r, default, key, key, subject)) continue;

            switch ((LayerReorderOp)r.Op)
            {
                case LayerReorderOp.MoveTo: MoveTo(o, r.Layer, r.Other); break;
                case LayerReorderOp.MoveAfter: MoveAfter(o, r.Layer, r.Other); break;
                default:
                {
                    int ia = o.IndexOf(r.Layer), ib = o.IndexOf(r.Other);
                    if (ia >= 0 && ib >= 0 && ib < ia)
                    {
                        o[ia] = r.Other;
                        o[ib] = r.Layer;
                    }

                    break;
                }
            }
        }
    }

    /// <summary>PaperdollOrder.ApplyDirectionCloak generalised: every matching DirMove rule moves its layer within the filtered list.</summary>
    public int ApplyDirection(LayerRuleSet set, Span<byte> layers, int count, byte dir, ReadOnlySpan<ushort> key, LayerSubject subject)
    {
        ushort bit = dir >= 8 ? LayerRulesConst.DirOther : (ushort)(1 << dir);

        foreach (int ri in _dirMove[(int)set])
        {
            ref readonly LayerRule r = ref Rules[ri];
            if ((r.Value & bit) == 0 || !Match(r, default, key, key, subject)) continue;

            int idx = layers.Slice(0, count).IndexOf(r.Layer);
            if (idx < 0) continue;

            for (int i = idx; i < count - 1; i++) layers[i] = layers[i + 1];
            count--;

            int insert = count;
            if (r.Op == (byte)LayerDirOp.Bottom) insert = 0;
            else if (r.Op == (byte)LayerDirOp.Before)
            {
                int a = layers.Slice(0, count).IndexOf(r.Other);
                if (a >= 0) insert = a;
            }

            for (int i = count; i > insert; i--) layers[i] = layers[i - 1];
            layers[insert] = r.Layer;
            count++;
        }

        return count;
    }

    /// <summary>FixGargoyleEquipment generalised: the first matching remap of a draw AnimID, else the AnimID itself.</summary>
    public ushort Remap(LayerRuleSet set, ushort anim, LayerSubject subject)
    {
        if ((_remapFilter[(int)set * 16 + ((anim & 1023) >> 6)] & (1UL << (anim & 63))) == 0) return anim;

        foreach (int ri in _remap[(int)set])
        {
            ref readonly LayerRule r = ref Rules[ri];
            if (r.Value == anim && Match(r, ReadOnlySpan<bool>.Empty, ReadOnlySpan<ushort>.Empty, ReadOnlySpan<ushort>.Empty, subject)) return r.Value2;
        }

        return anim;
    }

    // exact copies of PaperdollOrder's memmove primitives
    private static void MoveTo(Span<byte> a, byte A, byte B)
    {
        int i1 = a.IndexOf(A); if (i1 < 0) return;
        int i2 = a.IndexOf(B); if (i2 < 0 || i2 == i1) return;
        if (i2 < i1) { for (int k = i1; k > i2; k--) a[k] = a[k - 1]; a[i2] = A; }
        else { for (int k = i1; k < i2; k++) a[k] = a[k + 1]; a[i2] = A; }
    }

    private static void MoveAfter(Span<byte> a, byte A, byte B)
    {
        int i1 = a.IndexOf(A); if (i1 < 0) return;
        int i2 = a.IndexOf(B); if (i2 < 0 || i2 == i1) return;
        if (i2 < i1) { for (int k = i1; k > i2 + 1; k--) a[k] = a[k - 1]; a[i2 + 1] = A; }
        else { for (int k = i1; k < i2; k++) a[k] = a[k + 1]; a[i2] = A; }
    }

    // ---- file -------------------------------------------------------------------------------------------------------

    public void Write(string path)
    {
        using var s = ReplacingFile.Create(path);
        StructIo.WriteHeader(s, LayerRulesConst.Magic);
        var h = new LayerRulesHeader
        {
            BodyCount = Bodies.Length, LayerCount = LayerRulesConst.LayerCount, TableCount = Tables.Length / LayerRulesConst.LayerCount,
            RuleCount = Rules.Length, CondCount = Conds.Length, RangeCount = Ranges.Length
        };
        StructIo.Write(s, in h);
        StructIo.WriteSpan<BodyGroupEntry>(s, Bodies);
        StructIo.WriteSpan<byte>(s, Tables);
        StructIo.WriteSpan<LayerRule>(s, Rules);
        StructIo.WriteSpan<LayerCond>(s, Conds);
        StructIo.WriteSpan<LayerRange>(s, Ranges);
        s.Commit();
    }

    public static LayerRules Read(string path)
    {
        using var s = File.OpenRead(path);
        return Read(s);
    }

    public static LayerRules Read(Stream s)
    {
        StructIo.ReadHeader(s, LayerRulesConst.Magic);
        var h = StructIo.Read<LayerRulesHeader>(s);
        if (h.LayerCount != LayerRulesConst.LayerCount) throw new InvalidDataException($"layerrules: layer count {h.LayerCount}");
        const int max = 1 << 20;
        if ((uint)h.BodyCount > max || (uint)h.TableCount > 255 || (uint)h.RuleCount > max || (uint)h.CondCount > 0xFFFF || (uint)h.RangeCount > 0xFFFF)
            throw new InvalidDataException("layerrules: counts out of range");

        var bodies = ReadArray<BodyGroupEntry>(s, h.BodyCount);
        var tables = ReadArray<byte>(s, h.TableCount * LayerRulesConst.LayerCount);
        var rules = ReadArray<LayerRule>(s, h.RuleCount);
        var conds = ReadArray<LayerCond>(s, h.CondCount);
        var ranges = ReadArray<LayerRange>(s, h.RangeCount);
        if (s.Position != s.Length) throw new InvalidDataException("layerrules: trailing bytes");
        return new LayerRules(bodies, tables, rules, conds, ranges);
    }

    private static T[] ReadArray<T>(Stream s, int count) where T : unmanaged
    {
        var a = new T[count];
        s.ReadExactly(MemoryMarshal.AsBytes(a.AsSpan()));
        return a;
    }
}
