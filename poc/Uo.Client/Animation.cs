using System.Runtime.CompilerServices;
using Uo.Pack;
using Uo.Render;

namespace Uo.Client;

/// <summary>Item graphic → equipment animation body (tiledata AnimID). The seam for the extended tiledata: once the
/// pack carries AnimID, implement this from <see cref="PackSet"/> and delete <see cref="TileDataMulAnimIds"/>.</summary>
public interface IItemAnimIds
{
    ushort Get(ushort graphic);
}

/// <summary>AnimID from the pack's extended tiledata (tiledata_ext.bin, format 1.0).</summary>
public sealed class PackAnimIds : IItemAnimIds
{
    readonly ushort[] _anim;

    public PackAnimIds(StaticTileInfo[] statics)
    {
        _anim = new ushort[statics.Length];
        for (int i = 0; i < statics.Length; i++) _anim[i] = statics[i].AnimId;
    }

    public ushort Get(ushort graphic) => graphic < _anim.Length ? _anim[graphic] : (ushort)0;

    public static IItemAnimIds TryLoad(string packRoot)
    {
        var path = Path.Combine(packRoot, TableConst.TileDataExtFile);
        return File.Exists(path) ? new PackAnimIds(TileDataExtFile.Read(path).Statics) : null;
    }
}

/// <summary>FALLBACK for packs without tiledata_ext.bin: reads AnimID straight from the client's tiledata.mul (7.0.9+ layout) until the pack's tiledata
/// has it. This is the only MUL read in the client; remove it once every pack in use is format 1.0.</summary>
public sealed class TileDataMulAnimIds : IItemAnimIds
{
    readonly ushort[] _anim;

    public TileDataMulAnimIds(string path)
    {
        var b = File.ReadAllBytes(path);
        const int landBytes = 512 * (4 + 32 * 30), group = 4 + 32 * 41;
        int groups = (b.Length - landBytes) / group;
        _anim = new ushort[groups * 32];
        for (int g = 0; g < groups; g++)
            for (int j = 0; j < 32; j++)
                _anim[g * 32 + j] = BitConverter.ToUInt16(b, landBytes + g * group + 4 + j * 41 + 14);
    }

    public ushort Get(ushort graphic) => graphic < _anim.Length ? _anim[graphic] : (ushort)0;

    public static IItemAnimIds TryDefault(string path)
    {
        path ??= @"C:\Ultima Online Classic\tiledata.mul";
        return File.Exists(path) ? new TileDataMulAnimIds(path) : null;
    }
}

/// <summary>Equipment layers (ClassicUO <c>Layer</c>) and the in-world draw order (ClassicUO <c>LayerOrder.UsedLayers</c>).</summary>
public static class Layers
{
    public const byte OneHanded = 0x01, TwoHanded = 0x02, Shoes = 0x03, Pants = 0x04, Shirt = 0x05, Helmet = 0x06, Gloves = 0x07,
        Ring = 0x08, Talisman = 0x09, Necklace = 0x0A, Hair = 0x0B, Waist = 0x0C, Torso = 0x0D, Bracelet = 0x0E, Face = 0x0F,
        Beard = 0x10, Tunic = 0x11, Earrings = 0x12, Arms = 0x13, Cloak = 0x14, Backpack = 0x15, Robe = 0x16, Skirt = 0x17,
        Legs = 0x18, Mount = 0x19;
    public const int Count = 0x1A;

    // Directions 1, 2 and 4..7 share one order; 0 puts the cloak last and 3 puts it first.
    static readonly byte[] Order0 = [Shirt, Pants, Shoes, Legs, Torso, Ring, Talisman, Bracelet, Face, Arms, Gloves, Skirt, Tunic, Robe, Necklace, Hair, Waist, Beard, Earrings, OneHanded, Helmet, TwoHanded, Cloak];
    static readonly byte[] Order1 = [Shirt, Pants, Shoes, Legs, Torso, Ring, Talisman, Bracelet, Face, Arms, Gloves, Skirt, Tunic, Robe, Necklace, Hair, Waist, Beard, Earrings, OneHanded, Cloak, Helmet, TwoHanded];
    static readonly byte[] Order3 = [Cloak, Shirt, Pants, Shoes, Legs, Torso, Ring, Talisman, Bracelet, Face, Arms, Gloves, Skirt, Tunic, Robe, Waist, Necklace, Hair, Beard, Earrings, Helmet, OneHanded, TwoHanded];

    public static ReadOnlySpan<byte> InWorldOrder(int dir) => (dir & 7) switch { 0 => Order0, 3 => Order3, _ => Order1 };
}

/// <summary>Mount item graphic → (body, rider y offset). ClassicUO <c>Mounts</c>.</summary>
public static class Mounts
{
    static readonly Dictionary<ushort, (ushort Body, sbyte OffsetY)> Table = new();

    static Mounts()
    {
        ReadOnlySpan<(int Item, int Body, int Off)> t =
        [
            (0x3E90, 0x0114, 0), (0x3E91, 0x0115, 0), (0x3E92, 0x011C, 0), (0x3E94, 0x00F3, 0), (0x3E95, 0x00A9, 0), (0x3E97, 0x00C3, 0),
            (0x3E98, 0x00C2, 0), (0x3E9A, 0x00C1, 0), (0x3E9B, 0x00C0, -9), (0x3E9D, 0x00C0, -9), (0x3E9C, 0x00BF, 0), (0x3E9E, 0x00BE, 0),
            (0x3EA0, 0x00E2, 0), (0x3EA1, 0x00E4, 0), (0x3EA2, 0x00CC, 0), (0x3EA3, 0x00D2, 0), (0x3EA4, 0x00DA, 0), (0x3EA5, 0x00DB, 0),
            (0x3EA6, 0x00DC, 0), (0x3EA7, 0x0074, 0), (0x3EA8, 0x0075, 0), (0x3EA9, 0x0072, 0), (0x3EAA, 0x0073, 0), (0x3EAB, 0x00AA, 0),
            (0x3EAC, 0x00AB, 0), (0x3EAD, 0x0084, 0), (0x3EAF, 0x0078, 0), (0x3EB0, 0x0079, 0), (0x3EB1, 0x0077, 0), (0x3EB2, 0x0076, 0),
            (0x3EB3, 0x0090, 0), (0x3EB4, 0x007A, -9), (0x3EB5, 0x00B1, 0), (0x3EB6, 0x00B2, 0), (0x3EB7, 0x00B3, 0), (0x3EB8, 0x00BC, 0),
            (0x3EBA, 0x00BB, 0), (0x3EBB, 0x0319, -9), (0x3EBC, 0x0317, 0), (0x3EBD, 0x031A, 0), (0x3EBE, 0x031F, 0), (0x3EC3, 0x02D4, 0),
            (0x3ECE, 0x059A, 0), (0x3EC5, 0x00D5, 0), (0x3F3A, 0x00D5, 0), (0x3EC6, 0x01B0, 9), (0x3EC7, 0x04E6, 18), (0x3EC8, 0x04E7, 18),
            (0x3EC9, 0x042D, 3), (0x3ECA, 0x0579, 9), (0x3ECC, 0x0582, 0), (0x3ED1, 0x05E6, 0), (0x3ECB, 0x057F, 0), (0x3ED0, 0x05A1, 18),
            (0x3ED2, 0x05F6, 9), (0x3ECD, 0x0580, 0), (0x3ECF, 0x05A0, 9), (0x3ED3, 0x05F7, 18), (0x3ED4, 0x060A, 0), (0x3ED5, 0x060B, 0),
            (0x3ED6, 0x060C, 0), (0x3ED7, 0x060D, 0), (0x3ED8, 0x060F, 0), (0x3ED9, 0x0610, 0), (0x3EDA, 0x0590, 9), (0x3EDB, 0x0611, 12),
            (0x3EDC, 0x0666, 12), (0x3EDD, 0x0581, 0), (0x3EDE, 0x0673, -10), (0x3EDF, 0x0674, -10), (0x3EE0, 0x0675, -10), (0x3EE1, 0x0678,
            -10), (0x3EE2, 0x0679, -10), (0x3EE4, 0x06C1, -4),
        ];
        foreach (var (item, body, off) in t) Table[(ushort)item] = ((ushort)body, (sbyte)off);
    }

    public static bool TryGet(ushort item, out ushort body, out sbyte offsetY)
    {
        if (Table.TryGetValue(item, out var v)) { body = v.Body; offsetY = v.OffsetY; return true; }
        body = 0; offsetY = 0;
        return false;
    }
}

/// <summary>A mobile's state as the animation lookup needs it.</summary>
public struct MobileAnimInput
{
    public ushort Body, Hue;
    public byte Direction;        // UO direction 0..7
    public bool Moving, Running, WarMode;
    public uint Serial;
}

/// <summary>One sprite of a mobile: an atlas rect placed relative to the mobile's anchor, plus how to draw it.</summary>
public struct MobileFrame
{
    public SpriteEntry Sprite;
    /// <summary>Trimmed rect's top-left relative to the anchor (tile diamond centre, 3 px up).</summary>
    public int X, Y;
    public ushort Hue;            // 0 = none; 0x8000 = partial
    public bool Partial, Mirror, Indexed;
    /// <summary>Logical page in the GPU page table (anim pages start at <see cref="PackSet.AnimPageBase"/>).</summary>
    public int Page;
    public ushort Palette;
}

/// <summary>The seam where animation frames plug in: fills the sprites of one mobile (mount, body, equipment) in
/// ClassicUO's draw order, bottom first.</summary>
public interface IMobileSprites
{
    int GetFrames(in MobileAnimInput m, in Equipment equipment, long nowTicks, Span<MobileFrame> frames);
}

/// <summary>Real animation frames from the pack's anim tables: body → action → direction → frame, with ClassicUO's
/// action selection (<c>Mobile.GetGroupForAnimation</c>, walk/run/stand per animation type), its 80 ms frame clock,
/// mirrored directions, MobileView anchoring, mounts and equipment layers.</summary>
public sealed class AnimMobileSprites : IMobileSprites
{
    public static readonly long FrameTicks = System.Diagnostics.Stopwatch.Frequency * 80 / 1000; // CHARACTER_ANIMATION_DELAY

    readonly PackSet _pack;
    readonly AnimBodyEntry[] _bodies;
    readonly AnimDirEntry[] _dirs;
    readonly AnimFrameEntry[] _frames;
    readonly uint[] _framePalette;
    readonly StaticTileData[] _tiles;
    readonly IItemAnimIds _animIds;
    readonly EquipConvEntry[] _equipConv;
    public int MissingActions;

    const uint FlagLowGroupExtended = 0x20, FlagLowGroup = 0x40, FlagPeopleGroup = 0x400, FlagIdleAt8 = 0x4, FlagCanFly = 0x8, FlagUop = 0x10000;

    public AnimMobileSprites(PackSet pack, IItemAnimIds animIds, EquipConvEntry[] equipConv = null)
    {
        _equipConv = equipConv ?? [];
        _pack = pack;
        _bodies = pack.AnimBodies;
        _dirs = pack.AnimDirs;
        _frames = pack.AnimFrames;
        _framePalette = pack.FramePalette;
        _tiles = pack.Statics;
        _animIds = animIds;
    }

    /// <summary>ClassicUO <c>Mobile.IsHuman</c>.</summary>
    public static bool IsHuman(int g) =>
        g is >= 0x0190 and <= 0x0193 or >= 0x00B7 and <= 0x00BA or >= 0x025D and <= 0x0260
        or 0x029A or 0x029B or 0x02B6 or 0x02B7 or 0x03DB or 0x03DF or 0x03E2 or 0x02E8 or 0x02E9 or 0x04E5;

    public int GetFrames(in MobileAnimInput m, in Equipment eq, long now, Span<MobileFrame> outFrames)
    {
        int body = m.Body;
        if ((uint)body >= (uint)_bodies.Length) return 0;
        ushort fileHue = 0;
        if (_bodies[body].AliasBody != AnimConst.NoBody) { fileHue = _bodies[body].AliasHue; body = _bodies[body].AliasBody; }
        if ((uint)body >= (uint)_bodies.Length) return 0;

        bool human = IsHuman(m.Body);
        int dir = m.Direction & 7;
        bool hand2 = eq.Slots[Layers.TwoHanded] != 0;
        int mountBody = -1;
        sbyte mountOffset = 0;
        ushort mountHue = 0;
        uint mountSlot = eq.Slots[Layers.Mount];
        if (human && mountSlot != 0)
        {
            ushort item = (ushort)mountSlot;
            if (Mounts.TryGet(item, out var mb, out mountOffset)) mountBody = mb;
            ushort aid = _animIds?.Get(item) ?? 0;
            if (aid != 0) mountBody = aid;
            if ((uint)mountBody < (uint)_bodies.Length && _bodies[mountBody].AliasBody != AnimConst.NoBody)
            {
                mountHue = _bodies[mountBody].AliasHue;
                mountBody = _bodies[mountBody].AliasBody;
            }
            if ((uint)mountBody >= (uint)_bodies.Length || !_bodies[mountBody].HasFrames) mountBody = -1;
        }

        int action = SelectAction(body, m.Moving, m.Running, m.WarMode, mountBody >= 0, hand2);
        var bodyFrames = Lookup(body, action, dir, out var bodyDir);
        if (bodyFrames.Length == 0)
        {
            // Anim1/Anim2.def group fallbacks are not converted yet: show the first action this body has.
            MissingActions++;
            for (int a = 0; a < _bodies[body].ActionCount && bodyFrames.Length == 0; a++) bodyFrames = Lookup(body, action = a, dir, out bodyDir);
            if (bodyFrames.Length == 0) return 0;
        }

        // ClassicUO advances one shared AnimIndex every 80 ms per mobile and wraps it at the body's frame count;
        // the per-serial phase keeps mobiles out of lockstep.
        long counter = now / FrameTicks + ((m.Serial * 0x9E3779B1u) >> 24);
        int index = (int)(counter % bodyFrames.Length);
        int n = 0;

        if (mountBody >= 0)
        {
            int ma = SelectAction(mountBody, m.Moving, m.Running, m.WarMode, true, hand2); // "mounted" is the rider's state: equipment-type mount anims use 23/24/25
            var mf = Lookup(mountBody, ma, dir, out var md);
            if (mf.Length > 0)
                {
                ushort mh = (ushort)((eq.Slots[Layers.Mount] >> 16) & 0x7FFF);
                Add(outFrames, ref n, mf, md, Math.Min(index, mf.Length - 1), 0, mh != 0 ? mh : mountHue, false);
            }
        }

        ushort hue = m.Hue;
        bool partial = (hue & 0x8000) != 0;
        hue &= 0x7FFF;
        if (hue == 0) { hue = fileHue; partial = false; }
        Add(outFrames, ref n, bodyFrames, bodyDir, index, mountOffset, hue, partial);

        if (human && _animIds != null)
        {
            foreach (byte layer in Layers.InWorldOrder(dir))
            {
                uint slot = eq.Slots[layer];
                if (slot == 0 || n >= outFrames.Length) continue;
                ushort graphic = (ushort)slot;
                ushort anim = _animIds.Get(graphic);
                ushort convHue = 0;
                if (anim != 0 && _equipConv.Length > 0 && EquipConvFile.TryGet(_equipConv, m.Body, anim, out var conv)) { anim = conv.NewGraphic; convHue = conv.Color; }
                if (anim == 0 || anim >= _bodies.Length) continue;
                var t = _bodies[anim].Type;
                if (t != AnimType.Equipment && t != AnimType.Human) continue;
                var ef = Lookup(anim, action, dir, out var ed);
                if (ef.Length == 0) continue;
                ushort ih = (ushort)(slot >> 16);
                bool ip = (ih & 0x8000) != 0 || graphic < _tiles.Length && (_tiles[graphic].Flags & TileFlag.PartialHue) != 0;
                if ((ih & 0x7FFF) == 0) { ih = convHue; ip = false; }
                Add(outFrames, ref n, ef, ed, Math.Min(index, ef.Length - 1), mountOffset, (ushort)(ih & 0x7FFF), ip);
            }
        }
        return n;
    }

    ReadOnlySpan<AnimFrameEntry> Lookup(int body, int action, int dir, out AnimDirEntry d)
        => AnimTableFile.Frames(_bodies, _dirs, _frames, body, action, dir, out d);

    void Add(Span<MobileFrame> outFrames, ref int n, ReadOnlySpan<AnimFrameEntry> frames, in AnimDirEntry d, int index, int offsetY, ushort hue, bool partial)
    {
        if (n >= outFrames.Length) return;
        ref readonly var fe = ref frames[index];
        ref readonly var sp = ref fe.Sprite;
        if (!sp.Present) return;
        bool mirror = (d.Flags & AnimDirFlags.Mirrored) != 0;
        ref var f = ref outFrames[n++];
        f.Sprite = sp;
        // MobileView anchoring (pack-format-v0.md "v1 animations")
        f.X = mirror ? -(sp.OrigW - fe.CenterX) + (sp.OrigW - sp.TrimX - sp.W) : -fe.CenterX + sp.TrimX;
        f.Y = -(sp.OrigH + fe.CenterY) + sp.TrimY + offsetY;
        f.Mirror = mirror;
        f.Hue = hue;
        f.Partial = partial;
        f.Indexed = _pack.AnimIndexed;
        f.Page = _pack.AnimPageBase + sp.Page;
        f.Palette = _framePalette != null ? (ushort)_framePalette[(int)d.FirstFrame + index] : (ushort)0;
    }

    bool Exists(int body, int action)
    {
        ref readonly var b = ref _bodies[body];
        if (action >= b.ActionCount) return false;
        int r = (int)b.FirstDir + action * AnimConst.Directions;
        return _dirs[r + 3].FrameCount != 0 || _dirs[r + 4].FrameCount != 0;
    }

    /// <summary>ClassicUO <c>GetGroupForAnimation</c> without server-driven animations (walk, run, stand).</summary>
    int SelectAction(int body, bool moving, bool run, bool war, bool mounted, bool hand2)
    {
        ref readonly var b = ref _bodies[body];
        uint flags = b.Flags;
        bool uop = (flags & FlagUop) != 0;
        switch (b.Type)
        {
            case AnimType.Animal:
                if ((flags & FlagLowGroupExtended) != 0) return CalculateHeight(body, flags, moving, run, war);
                if (!moving) return uop ? (war && Exists(body, 1) ? 1 : 25) : 2;
                if (run) return uop ? 24 : Exists(body, 1) ? 1 : 2;
                return uop && (!war || !Exists(body, 0)) ? 22 : 0;
            case AnimType.Monster:
                return CalculateHeight(body, flags, moving, run, war);
            case AnimType.SeaMonster:
                return !moving ? 2 : run ? 1 : 0;
            default:
            {
                bool equip = b.Type == AnimType.Equipment;
                if (!moving)
                {
                    if (mounted) return 25;
                    if (!war) return uop && equip && Exists(body, 37) ? 37 : 4;
                    return 7;
                }
                if (mounted) return run ? 24 : 23;
                if (!run && war) return 15;
                int r;
                if (uop)
                    r = run && Exists(body, 24) ? 24 : run ? (equip && !Exists(body, 2) ? 3 : 2) : (equip && !Exists(body, 0) ? 1 : 0);
                else
                    r = run ? (hand2 ? 3 : 2) : (hand2 ? 1 : 0);
                if (hand2) r = run ? 3 : 1;
                return r;
            }
        }
    }

    int CalculateHeight(int body, uint flags, bool moving, bool run, bool war)
    {
        if ((flags & FlagPeopleGroup) != 0) return 0;
        if ((flags & FlagLowGroup) != 0) return !moving ? 2 : run ? 1 : 0;
        bool uop = (flags & FlagUop) != 0;
        if (!moving) return (flags & FlagIdleAt8) != 0 && Exists(body, 8) ? 8 : uop && !war ? 25 : 1;
        if (run) return (flags & FlagCanFly) != 0 && Exists(body, 19) ? 19 : uop ? 24 : 0;
        return uop && !war ? 22 : 0;
    }
}

public sealed class NoMobileSprites : IMobileSprites
{
    public int GetFrames(in MobileAnimInput m, in Equipment equipment, long nowTicks, Span<MobileFrame> frames) => 0;
}
