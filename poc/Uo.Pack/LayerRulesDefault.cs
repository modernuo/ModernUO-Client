namespace Uo.Pack;

/// <summary>
/// T3b: ClassicUO's hard-coded layering rules (PaperdollOrder.Build/ApplyDirectionCloak, MobileView.IsCovered,
/// FixGargoyleEquipments) as data: the one source of the default <c>layerrules.bin</c>. The converter writes it; a client
/// whose pack has no layerrules.bin uses the same <see cref="Create"/>. Inventory: cuo-poc-state handoffs/t3-layering.md.
/// </summary>
public static class LayerRulesDefault
{
    // Layer byte values (ClassicUO.Game.Data.Layer)
    public const byte Invalid = 0x00, OneHanded = 0x01, TwoHanded = 0x02, Shoes = 0x03, Pants = 0x04, Shirt = 0x05, Helmet = 0x06,
        Gloves = 0x07, Ring = 0x08, Talisman = 0x09, Necklace = 0x0A, Hair = 0x0B, Waist = 0x0C, Torso = 0x0D, Bracelet = 0x0E,
        Face = 0x0F, Beard = 0x10, Tunic = 0x11, Earrings = 0x12, Arms = 0x13, Cloak = 0x14, Backpack = 0x15, Robe = 0x16,
        Skirt = 0x17, Legs = 0x18;

    public static LayerRules Create()
    {
        var b = new LayerRulesBuilder();

        // body groups (only the gargoyle group is read by today's rules: the hood exception)
        b.Body(0x0190, BodyGroup.HumanMale); b.Body(0x0191, BodyGroup.HumanFemale);
        b.Body(0x0192, BodyGroup.HumanMale); b.Body(0x0193, BodyGroup.HumanFemale);
        b.Body(0x025D, BodyGroup.ElfMale); b.Body(0x025E, BodyGroup.ElfFemale);
        b.Body(0x025F, BodyGroup.ElfMale); b.Body(0x0260, BodyGroup.ElfFemale);
        b.Body(0x029A, BodyGroup.GargoyleMale); b.Body(0x029B, BodyGroup.GargoyleFemale);
        b.Body(0x02B6, BodyGroup.GargoyleMale); b.Body(0x02B7, BodyGroup.GargoyleFemale);

        // base tables (PaperdollOrder T1 arms late, T2 default arms early, T3 chest-under)
        int t1 = b.Table(Invalid, Cloak, Shirt, Pants, Shoes, Legs, Torso, Tunic, Ring, Bracelet, Face, Arms, Gloves, Skirt, Robe,
            Waist, Necklace, Hair, Beard, Earrings, Helmet, OneHanded, TwoHanded, Backpack, Talisman);
        int t2 = b.Table(Invalid, Cloak, Shirt, Pants, Shoes, Legs, Arms, Torso, Tunic, Ring, Bracelet, Face, Gloves, Skirt, Robe,
            Waist, Necklace, Hair, Beard, Earrings, Helmet, OneHanded, TwoHanded, Backpack, Talisman);
        int t3 = b.Table(Invalid, Cloak, Torso, Shirt, Pants, Shoes, Legs, Tunic, Ring, Bracelet, Face, Arms, Gloves, Skirt, Robe,
            Waist, Necklace, Hair, Beard, Earrings, Helmet, OneHanded, TwoHanded, Backpack, Talisman);

        foreach (LayerRuleSet set in new[] { LayerRuleSet.Paperdoll, LayerRuleSet.World })
        {
            // ---- order (keys are AnimIDs) ----
            b.Select(set, t1, C.Anim(Arms, 0x210, 0x3B3, 0x3CF, 0x3DD));                                    // O1
            b.Select(set, t1, C.Anim(Torso, 0x21A));                                                       // O2
            b.Select(set, t3, C.AnimRange(Torso, 0x399, 0x39D), C.Flag(LayerRulesConst.FlagFemale));        // O3
            b.Select(set, t3, C.AnimRange(Torso, 0x399, 0x39D), C.Flag(LayerRulesConst.FlagGargoyleDraw));
            b.Select(set, t2);                                                                              // O4

            b.Reorder(set, LayerReorderOp.MoveTo, Pants, Shirt, C.Present(Shirt), C.Anim(Pants, 0x398));      // O5
            b.Reorder(set, LayerReorderOp.SwapIfAfter, Shoes, Pants, C.Anim(Pants, 0x1EB, 0x1FA, 0x200));    // O6
            b.Reorder(set, LayerReorderOp.MoveTo, Pants, Shoes, C.Anim(Pants, 0x513, 0x514), C.Present(Shoes)); // O7
            b.Reorder(set, LayerReorderOp.MoveTo, Pants, Shoes, C.Anim(Pants, 0x3E4), C.Present(Shoes));      // O8
            b.Reorder(set, LayerReorderOp.MoveAfter, Tunic, Waist, C.Anim(Tunic, 0x238));                    // O9
            b.Reorder(set, LayerReorderOp.MoveTo, Robe, Necklace, C.Anim(Tunic, 0x238), C.Ranges(LayerCondKind.AnimId, Robe, false, (0x4E8, 0x4EB), (0x5E2, 0x5E5)));
            b.Reorder(set, LayerReorderOp.MoveAfter, Cloak, Robe, C.Anim(Cloak, 0x380, 0x5F3));              // O10
            b.Reorder(set, LayerReorderOp.MoveAfter, Necklace, Helmet, C.Anim(Helmet, 0x201, 0x1A9), C.Anim(Necklace, 0x1C8, 0x1D7, 0x1D8)); // O11
            b.Reorder(set, LayerReorderOp.MoveTo, Robe, Helmet, C.AnimRange(Helmet, 0x5E9, 0x5EA), C.AnimRange(Robe, 0x5E2, 0x5E5)); // O12

            // ---- covered (first match per layer; default not covered) ----
            C closedRobe = C.Ranges(LayerCondKind.Graphic, Robe, true, (0, 0), (0x9985, 0x9986), (0xA2CA, 0xA2CB), (0xA412, 0xA412), (0xB1DE, 0xB1DE));
            C openRobe = C.Ranges(LayerCondKind.Graphic, Robe, false, (0x9985, 0x9986), (0xA2CA, 0xA2CB), (0xA412, 0xA412), (0xB1DE, 0xB1DE));

            b.Cover(set, Shoes, true, C.Anim(Robe, 0x504));                                                  // C1
            b.Cover(set, Shoes, true, C.Anim(Pants, 0x513, 0x514));
            b.Cover(set, Shoes, true, C.Gfx(Pants, 0xAEB1, 0x1411, 0xAEC0, 0xAEA2, 0xAECF));
            b.Cover(set, Shoes, true, C.Present(Legs));

            b.Cover(set, Pants, true, C.Present(Legs));                                                      // C2
            b.Cover(set, Pants, true, C.Anim(Robe, 0x504));
            b.Cover(set, Pants, false, C.Not(C.Anim(Pants, 0x1EB, 0x1FA, 0x200)));
            b.Cover(set, Pants, true, C.Present(Skirt), C.Not(C.Anim(Skirt, 0x1C7, 0x1E4)));
            b.Cover(set, Pants, false, C.Not(C.Present(Robe)));
            b.Cover(set, Pants, false, C.Ranges(LayerCondKind.AnimId, Robe, false, (0x229, 0x229), (0x4E8, 0x4EB), (0x5E2, 0x5E5)));
            b.Cover(set, Pants, true);

            b.Cover(set, Tunic, true, C.Present(Tunic), C.Anim(Tunic, 0x238), C.Present(Robe), C.Not(openRobe)); // C3

            b.Cover(set, Torso, true, closedRobe);                                                           // C4
            b.Cover(set, Torso, true, C.Present(Tunic), C.Not(C.Gfx(Tunic, 0x1541, 0x1542)), C.Present(Torso), C.Gfx(Torso, 0x782A, 0x782B));

            b.Cover(set, Arms, true, closedRobe);                                                            // C5

            b.Cover(set, Necklace, false, C.Not(C.Present(Robe)));                                           // C6
            b.Cover(set, Necklace, false, C.Ranges(LayerCondKind.AnimId, Robe, false, (0x5F2, 0x5F2), (0x5F5, 0x5F5), (0x4E8, 0x4EB), (0x5E2, 0x5E5)));
            b.Cover(set, Necklace, true, C.Present(Necklace), C.Anim(Necklace, 0x5EC));

            b.Cover(set, Bracelet, true, C.Present(Bracelet), C.Gfx(Bracelet, 0xB1C0), C.Present(Arms));      // C7

            b.Cover(set, Hair, true, C.Present(Helmet), C.GfxRange(Helmet, 0xA42B, 0xA42C));                  // C8

            // C9: hoods, on Hair and Helmet
            C everyBody = C.Ranges(LayerCondKind.Graphic, Robe, false, (0x204E, 0x204F), (0x2683, 0x2687), (0x2FB9, 0x2FB9), (0x3173, 0x3173));
            C notGargoyle = C.Ranges(LayerCondKind.Graphic, Robe, false, (0x4B9D, 0x4B9D), (0x7816, 0x7816), (0xA0AB, 0xA0AF), (0xB2B7, 0xB2B7));
            C gargoyle = C.Ranges(LayerCondKind.Group, 0, true, ((ushort)BodyGroup.GargoyleMale, (ushort)BodyGroup.GargoyleFemale));

            foreach (byte target in new[] { Hair, Helmet })
            {
                b.Cover(set, target, true, everyBody);
                b.Cover(set, target, true, notGargoyle, gargoyle);
            }

            b.Cover(set, Skirt, true, C.Anim(Skirt, 0x1C7, 0x1E4), C.Anim(Pants, 0x1EB, 0x1FA, 0x200));     // C10
        }

        // ---- world only ----
        // D1: the cloak by facing direction: on top facing north, behind facing south, else just below the helmet
        b.DirMove(LayerRuleSet.World, Cloak, 1 << 0, LayerDirOp.Top, 0);
        b.DirMove(LayerRuleSet.World, Cloak, 1 << 3, LayerDirOp.Bottom, 0);
        b.DirMove(LayerRuleSet.World, Cloak, (ushort)(0xFF & ~(1 << 0) & ~(1 << 3) | LayerRulesConst.DirOther), LayerDirOp.Before, Helmet);

        // R1: gargoyle draw remaps (robe, death shroud, spellbooks, pirate shield)
        C garg = C.Flag(LayerRulesConst.FlagGargoyleDraw);
        b.Remap(LayerRuleSet.World, 0x01D5, 0x0156, garg);
        b.Remap(LayerRuleSet.World, 0x03CA, 0x0223, garg);
        b.Remap(LayerRuleSet.World, 0x03D8, 329, garg);
        b.Remap(LayerRuleSet.World, 0x0372, 330, garg);
        b.Remap(LayerRuleSet.World, 0x0374, 328, garg);
        b.Remap(LayerRuleSet.World, 0x036F, 327, garg);
        b.Remap(LayerRuleSet.World, 0x036E, 328, garg);
        b.Remap(LayerRuleSet.World, 0x0426, 0x042B, garg);
        b.Remap(LayerRuleSet.World, 1529, 1531, garg);
        b.Remap(LayerRuleSet.World, 1530, 1532, garg);

        return b.Build();
    }
}

/// <summary>A condition under construction.</summary>
public readonly record struct C(LayerCondKind Kind, byte Layer, bool Negate, (ushort Lo, ushort Hi)[] R)
{
    public static C Present(byte layer) => new(LayerCondKind.Present, layer, false, Array.Empty<(ushort, ushort)>());
    public static C Flag(byte mask) => new(LayerCondKind.Flag, mask, false, Array.Empty<(ushort, ushort)>());
    public static C Anim(byte layer, params ushort[] v) => Values(LayerCondKind.AnimId, layer, v);
    public static C Gfx(byte layer, params ushort[] v) => Values(LayerCondKind.Graphic, layer, v);
    public static C AnimRange(byte layer, ushort lo, ushort hi) => new(LayerCondKind.AnimId, layer, false, new[] { (lo, hi) });
    public static C GfxRange(byte layer, ushort lo, ushort hi) => new(LayerCondKind.Graphic, layer, false, new[] { (lo, hi) });
    public static C Ranges(LayerCondKind kind, byte layer, bool negate, params (ushort, ushort)[] r) => new(kind, layer, negate, r);
    public static C Not(C c) => c with { Negate = !c.Negate };

    private static C Values(LayerCondKind kind, byte layer, ushort[] v)
    {
        var r = new (ushort, ushort)[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = (v[i], v[i]);
        return new C(kind, layer, false, r);
    }
}

public sealed class LayerRulesBuilder
{
    private readonly List<BodyGroupEntry> _bodies = new();
    private readonly List<byte> _tables = new();
    private readonly List<LayerRule> _rules = new();
    private readonly List<LayerCond> _conds = new();
    private readonly List<LayerRange> _ranges = new();

    public void Body(ushort body, BodyGroup group) => _bodies.Add(new BodyGroupEntry { Body = body, Group = (byte)group });

    public int Table(params byte[] layers)
    {
        if (layers.Length != LayerRulesConst.LayerCount) throw new ArgumentException("a table has 25 layers");
        _tables.AddRange(layers);
        return _tables.Count / LayerRulesConst.LayerCount - 1;
    }

    public void Select(LayerRuleSet set, int table, params C[] conds) => Add(set, LayerRuleKind.Select, 0, 0, 0, (ushort)table, 0, conds);
    public void Reorder(LayerRuleSet set, LayerReorderOp op, byte a, byte b, params C[] conds) => Add(set, LayerRuleKind.Reorder, a, b, (byte)op, 0, 0, conds);
    public void Cover(LayerRuleSet set, byte layer, bool covered, params C[] conds) => Add(set, LayerRuleKind.Cover, layer, covered ? (byte)1 : (byte)0, 0, 0, 0, conds);
    public void DirMove(LayerRuleSet set, byte layer, ushort dirMask, LayerDirOp op, byte anchor, params C[] conds) => Add(set, LayerRuleKind.DirMove, layer, anchor, (byte)op, dirMask, 0, conds);
    public void Remap(LayerRuleSet set, ushort from, ushort to, params C[] conds) => Add(set, LayerRuleKind.Remap, 0, 0, 0, from, to, conds);

    private void Add(LayerRuleSet set, LayerRuleKind kind, byte layer, byte other, byte op, ushort value, ushort value2, C[] conds)
    {
        var rule = new LayerRule
        {
            Set = (byte)set, Kind = (byte)kind, Layer = layer, Other = other, Op = op, CondCount = (byte)conds.Length,
            CondStart = (ushort)_conds.Count, Value = value, Value2 = value2
        };

        foreach (C c in conds)
        {
            _conds.Add(new LayerCond { Kind = (byte)c.Kind, Layer = c.Layer, Negate = c.Negate ? (byte)1 : (byte)0, RangeCount = (byte)c.R.Length, RangeStart = (ushort)_ranges.Count });
            foreach (var (lo, hi) in c.R) _ranges.Add(new LayerRange { Lo = lo, Hi = hi });
        }

        _rules.Add(rule);
    }

    public LayerRules Build() => new(_bodies.ToArray(), _tables.ToArray(), _rules.ToArray(), _conds.ToArray(), _ranges.ToArray());
}
