namespace Uo.Pack;

/// <summary>
/// ClassicUO's <c>Mounts</c> table (mount item graphic -> mount body, rider y offset) and <c>Item.GetGraphicForAnimation</c>
/// for a mount-layer item. One copy shared by the client (Mounts, AnimationRules), the PoC client (Uo.Client) and the
/// converter, which needs the per-item offsets to compute a pack's rider shifts (<see cref="AnimBodyEntry.MountShift"/>).
/// The offsets were set for the default (aos) art.
/// </summary>
public static class MountTable
{
    public static readonly (ushort Item, ushort Body, sbyte OffsetY)[] Entries =
    [
        (0x3E90, 0x0114, 0), // 16016 Reptalon
        (0x3E91, 0x0115, 0), // 16017
        (0x3E92, 0x011C, 0), // 16018
        (0x3E94, 0x00F3, 0), // 16020
        (0x3E95, 0x00A9, 0), // 16021
        (0x3E97, 0x00C3, 0), // 16023 Ethereal Giant Beetle
        (0x3E98, 0x00C2, 0), // 16024 Ethereal Swamp Dragon
        (0x3E9A, 0x00C1, 0), // 16026 Ethereal Ridgeback
        (0x3E9B, 0x00C0, -9), // 16027
        (0x3E9D, 0x00C0, -9), // 16029 Ethereal Unicorn
        (0x3E9C, 0x00BF, 0), // 16028 Ethereal Kirin
        (0x3E9E, 0x00BE, 0), // 16030
        (0x3EA0, 0x00E2, 0), // 16032 light grey/horse3
        (0x3EA1, 0x00E4, 0), // 16033 greybrown/horse4
        (0x3EA2, 0x00CC, 0), // 16034 dark brown/horse
        (0x3EA3, 0x00D2, 0), // 16035 desert ostard
        (0x3EA4, 0x00DA, 0), // 16036 frenzied ostard (=zostrich)
        (0x3EA5, 0x00DB, 0), // 16037 forest ostard
        (0x3EA6, 0x00DC, 0), // 16038 Llama
        (0x3EA7, 0x0074, 0), // 16039 Nightmare / Vortex
        (0x3EA8, 0x0075, 0), // 16040 Silver Steed
        (0x3EA9, 0x0072, 0), // 16041 Nightmare
        (0x3EAA, 0x0073, 0), // 16042 Ethereal Horse
        (0x3EAB, 0x00AA, 0), // 16043 Ethereal Llama
        (0x3EAC, 0x00AB, 0), // 16044 Ethereal Ostard
        (0x3EAD, 0x0084, 0), // 16045 Kirin
        (0x3EAF, 0x0078, 0), // 16047 War Horse (Blood Red)
        (0x3EB0, 0x0079, 0), // 16048 War Horse (Light Green)
        (0x3EB1, 0x0077, 0), // 16049 War Horse (Light Blue)
        (0x3EB2, 0x0076, 0), // 16050 War Horse (Purple)
        (0x3EB3, 0x0090, 0), // 16051 Sea Horse (Medium Blue)
        (0x3EB4, 0x007A, -9), // 16052 Unicorn
        (0x3EB5, 0x00B1, 0), // 16053 Nightmare
        (0x3EB6, 0x00B2, 0), // 16054 Nightmare 4
        (0x3EB7, 0x00B3, 0), // 16055 Dark Steed
        (0x3EB8, 0x00BC, 0), // 16056 Ridgeback
        (0x3EBA, 0x00BB, 0), // 16058 Ridgeback, Savage
        (0x3EBB, 0x0319, -9), // 16059 Skeletal Mount
        (0x3EBC, 0x0317, 0), // 16060 Beetle
        (0x3EBD, 0x031A, 0), // 16061 SwampDragon
        (0x3EBE, 0x031F, 0), // 16062 Armored Swamp Dragon
        (0x3EC3, 0x02D4, 0), // 16067 Beetle
        (0x3ECE, 0x059A, 0), // serpentine dragon
        (0x3EC5, 0x00D5, 0), // 16069
        (0x3F3A, 0x00D5, 0), // 16186 snow bear ???
        (0x3EC6, 0x01B0, 9), // 16070 Boura
        (0x3EC7, 0x04E6, 18), // 16071 Tiger
        (0x3EC8, 0x04E7, 18), // 16072 Tiger
        (0x3EC9, 0x042D, 3), // 16073
        (0x3ECA, 0x0579, 9), // tarantula
        (0x3ECC, 0x0582, 0), // 16016
        (0x3ED1, 0x05E6, 0), // CoconutCrab
        (0x3ECB, 0x057F, 0), // Lasher
        (0x3ED0, 0x05A1, 18), // SkeletalCat
        (0x3ED2, 0x05F6, 9), // war boar
        (0x3ECD, 0x0580, 0), // Palomino
        (0x3ECF, 0x05A0, 9), // Eowmu
        (0x3ED3, 0x05F7, 18), // capybara
        (0x3ED4, 0x060A, 0), // (no description provided)
        (0x3ED5, 0x060B, 0), // a wolf
        (0x3ED6, 0x060C, 0), // an orange dog 2?
        (0x3ED7, 0x060D, 0), // (no description provided)
        (0x3ED8, 0x060F, 0), // a black dog?
        (0x3ED9, 0x0610, 0), // a doberman?
        (0x3EDA, 0x0590, 9), // Frostmites Beetles
        (0x3EDB, 0x0611, 12), // Manticore
        (0x3EDC, 0x0666, 12), // Bear Zombie
        (0x3EDD, 0x0581, 0), // Dragon_Hildebrandt
        (0x3EDE, 0x0673, -10), // Horse_Clydesdale
        (0x3EDF, 0x0674, -10), // Horse_Elemental_Earth
        (0x3EE0, 0x0675, -10), // Horse_Elemental_Fire
        (0x3EE1, 0x0678, -10), // Horse_Elemental_Water
        (0x3EE2, 0x0679, -10), // Horse_Elemental_Air
        (0x3EE4, 0x06C1, -4), // True Britannian Horse
    ];

    private static readonly Dictionary<ushort, (ushort Body, sbyte OffsetY)> ByItem = Build();

    private static Dictionary<ushort, (ushort, sbyte)> Build()
    {
        var d = new Dictionary<ushort, (ushort, sbyte)>(Entries.Length);
        foreach (var (item, body, off) in Entries) d[item] = (body, off);
        return d;
    }

    public static bool TryGet(ushort item, out ushort body, out sbyte offsetY)
    {
        if (ByItem.TryGetValue(item, out var v)) { body = v.Body; offsetY = v.OffsetY; return true; }
        body = 0; offsetY = 0;
        return false;
    }

    /// <summary>The table's rider offset for <paramref name="item"/> (0 when it has no entry).</summary>
    public static sbyte OffsetY(ushort item) => ByItem.TryGetValue(item, out var v) ? v.OffsetY : (sbyte)0;

    /// <summary>
    /// Item.GetGraphicForAnimation for an item on Layer.Mount: the ethereal unicorn and kirin are fixed, else the table's
    /// body, overridden by a nonzero tiledata AnimID.
    /// </summary>
    public static ushort MountBody(ushort itemGraphic, ushort animId)
    {
        if (itemGraphic == 0x3E9B || itemGraphic == 0x3E9D) return 0x00C0; // ethereal unicorn
        if (itemGraphic == 0x3E9C) return 0x00BF; // ethereal kirin
        ushort graphic = ByItem.TryGetValue(itemGraphic, out var v) ? v.Body : itemGraphic;
        return animId != 0 ? animId : graphic;
    }
}
