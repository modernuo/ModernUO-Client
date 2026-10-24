using System.Diagnostics;
using Friflo.Engine.ECS;
using Uo.Pack;
using Uo.Render;

namespace Uo.Client;

/// <summary>One drawable frame for a mobile: an atlas rect plus where it sits relative to the tile.</summary>
public struct MobileFrame
{
    public SpriteEntry Sprite;
    /// <summary>Offset of the sprite's untrimmed top-left from the tile diamond's top-left (world px).</summary>
    public int OffsetX, OffsetY;
    /// <summary>Hue to apply (0 = none); 0x8000 = partial.</summary>
    public ushort Hue;
    public bool Mirror;
}

/// <summary>The seam where animation frames plug in. Given a mobile's body, direction, action and time it returns
/// the frame to draw. <see cref="PlaceholderMobileSprites"/> stands in until animations are converted; the real one
/// will look up the pack's animation tables (body → action → direction → frame), pick the frame from
/// <paramref name="nowTicks"/> and the motion state, and set <see cref="MobileFrame.Mirror"/> for directions 5–7
/// (needs a mirror flag in the sprite shader). Equipment layers will be extra frames from the same lookup.</summary>
public interface IMobileSprites
{
    bool TryGetFrame(ushort body, byte direction, bool moving, ushort hue, byte notoriety, bool isPlayer, long nowTicks, out MobileFrame frame);
}

/// <summary>Placeholder: a gray stone statue art tile, fully hued by notoriety; the player is a larger statue in magenta.</summary>
public sealed class PlaceholderMobileSprites : IMobileSprites
{
    public const ushort StatueGraphic = 0x1226, PlayerStatueGraphic = 0x1228;
    public const ushort PlayerHue = 0x0015;
    readonly SpriteEntry _statue, _playerStatue;
    readonly bool _ok;

    // ClassicUO notoriety hues: innocent, friend, gray, criminal, enemy, murderer, invulnerable
    static readonly ushort[] NotorietyHues = [0x03B2, 0x0059, 0x003F, 0x03B2, 0x03B2, 0x0090, 0x0022, 0x0035];

    public PlaceholderMobileSprites(PackSet pack)
    {
        _statue = pack.Sprites[PackConst.StaticSpriteBase + StatueGraphic];
        _playerStatue = pack.Sprites[PackConst.StaticSpriteBase + PlayerStatueGraphic];
        if (!_playerStatue.Present) _playerStatue = _statue;
        _ok = _statue.Present;
    }

    public bool TryGetFrame(ushort body, byte direction, bool moving, ushort hue, byte notoriety, bool isPlayer, long nowTicks, out MobileFrame frame)
    {
        frame = default;
        if (!_ok) return false;
        ref readonly var sp = ref isPlayer ? ref _playerStatue : ref _statue;
        frame.Sprite = sp;
        frame.OffsetX = -((sp.OrigW >> 1) - 22);
        frame.OffsetY = -(sp.OrigH - 44);
        frame.Hue = isPlayer ? PlayerHue : NotorietyHues[notoriety < NotorietyHues.Length ? notoriety : 0];
        return true;
    }
}

/// <summary>Per frame: queries the ECS for visible mobiles and ground items and writes their sprite instances into a
/// preallocated dynamic instance buffer. Depth keys use the same ClassicUO-derived key as the baked static world
/// (<see cref="BakeJob.DepthKey"/>), so one depth-tested draw interleaves dynamic and static sprites correctly
/// without a CPU sort. Zero allocation.</summary>
public sealed class RenderExtract
{
    public readonly SpriteInstance[] Instances;
    public int Count;
    public int MobilesDrawn, ItemsDrawn, Culled;
    public long Ticks;

    readonly PackSet _pack;
    readonly IMobileSprites _mobiles;
    readonly ArchetypeQuery<Position, Motion, Graphic, Hue, Facing> _playerQ, _mobileQ;
    readonly ArchetypeQuery<Position, Graphic, Hue> _itemQ;

    public RenderExtract(EntityStore store, PackSet pack, IMobileSprites mobiles, int capacity)
    {
        _pack = pack;
        _mobiles = mobiles;
        Instances = new SpriteInstance[capacity];
        _playerQ = store.Query<Position, Motion, Graphic, Hue, Facing>().AllTags(Tags.Get<MobileTag, PlayerTag>());
        _mobileQ = store.Query<Position, Motion, Graphic, Hue, Facing>().AllTags(Tags.Get<MobileTag>()).WithoutAnyTags(Tags.Get<PlayerTag>());
        _itemQ = store.Query<Position, Graphic, Hue>().AllTags(Tags.Get<ItemTag, OnGround>()).WithoutAnyTags(Tags.Get<MultiTag>());
    }

    /// <summary>Interpolated tile-space position (and the tile used for depth) of an entity at <paramref name="now"/>.
    /// While stepping, depth uses the nearer of the two tiles (max x, max y, max z), as ClassicUO's
    /// View.CalculateDepthZ does with the step offset.</summary>
    public static void Interpolate(in Position p, in Motion m, long now, out float fx, out float fy, out float fz, out int dx, out int dy, out int dz, out bool moving)
    {
        fx = p.X; fy = p.Y; fz = p.Z; dx = p.X; dy = p.Y; dz = p.Z;
        moving = false;
        if (m.Duration <= 0 || now >= m.Start + m.Duration) return;
        float t = Math.Clamp((float)(now - m.Start) / m.Duration, 0f, 1f);
        fx = m.FromX + (p.X - m.FromX) * t;
        fy = m.FromY + (p.Y - m.FromY) * t;
        fz = m.FromZ + (p.Z - m.FromZ) * t;
        dx = Math.Max(p.X, m.FromX); dy = Math.Max(p.Y, m.FromY); dz = Math.Max(p.Z, m.FromZ);
        moving = true;
    }

    /// <summary>World pixel of a tile diamond's top-left (BakeJob convention: (x−y)·22 − 22, (x+y)·22 − 22 − z·4).</summary>
    public static void TileToWorld(float x, float y, float z, out int px, out int py)
    {
        px = (int)MathF.Round((x - y) * 22f) - 22;
        py = (int)MathF.Round((x + y) * 22f - z * 4f) - 22;
    }

    public void Run(in RectI view, PageResidency pages, long frame, long now)
    {
        long t0 = Stopwatch.GetTimestamp();
        Count = 0; MobilesDrawn = 0; ItemsDrawn = 0; Culled = 0;
        var sprites = _pack.Sprites;
        var staticData = _pack.Statics;
        var cap = Instances.Length;

        // ---- ground items (static art + hue)
        foreach (var (pos, gfx, hue, _) in _itemQ.Chunks)
        {
            var ps = pos.Span; var gs = gfx.Span; var hs = hue.Span;
            for (int i = 0; i < ps.Length && Count < cap; i++)
            {
                int g = gs[i].Value;
                if (g >= staticData.Length) continue;
                ref readonly var sp = ref sprites[PackConst.StaticSpriteBase + g];
                if (!sp.Present) continue;
                ref readonly var p = ref ps[i];
                TileToWorld(p.X, p.Y, p.Z, out int ax, out int ay);
                int x = ax - ((sp.OrigW >> 1) - 22) + sp.TrimX, y = ay - (sp.OrigH - 44) + sp.TrimY;
                if (x > view.X1 || y > view.Y1 || x + sp.W < view.X0 || y + sp.H < view.Y0) { Culled++; continue; }
                // Chunk.AddGameObject default case
                ulong flags = staticData[g].Flags;
                int pz = p.Z;
                if ((flags & TileFlag.Background) != 0) pz--;
                if (staticData[g].Height != 0) pz++;
                if ((flags & TileFlag.MultiMovable) != 0) pz++;
                if (g == 0x2006) pz = p.Z + 1; // corpse
                Emit(x, y, sp, hs[i].Value, (flags & TileFlag.PartialHue) != 0, BakeJob.DepthKey(p.X, p.Y, pz, 6), pages, frame);
                ItemsDrawn++;
            }
        }

        // ---- mobiles (placeholder or animation frames through IMobileSprites)
        ExtractMobiles(_playerQ, true, view, pages, frame, now, cap);
        ExtractMobiles(_mobileQ, false, view, pages, frame, now, cap);
        Ticks = Stopwatch.GetTimestamp() - t0;
    }

    void ExtractMobiles(ArchetypeQuery<Position, Motion, Graphic, Hue, Facing> q, bool isPlayer, in RectI view, PageResidency pages, long frame, long now, int cap)
    {
        foreach (var (pos, motion, gfx, hue, facing, ents) in q.Chunks)
        {
            var ps = pos.Span; var ms = motion.Span; var gs = gfx.Span; var hs = hue.Span; var fs = facing.Span;
            for (int i = 0; i < ps.Length && Count < cap; i++)
            {
                Interpolate(ps[i], ms[i], now, out float fx, out float fy, out float fz, out int dx, out int dy, out int dz, out bool moving);
                TileToWorld(fx, fy, fz, out int ax, out int ay);
                // cheap reject before the frame lookup: mobiles are at most ~200 px tall
                if (ax + 120 < view.X0 || ax - 80 > view.X1 || ay + 80 < view.Y0 || ay - 200 > view.Y1) { Culled++; continue; }
                byte noto = isPlayer ? (byte)0 : ents.EntityAt(i).GetComponent<Notoriety>().Value;
                if (!_mobiles.TryGetFrame(gs[i].Value, (byte)(fs[i].Value & 7), moving, hs[i].Value, noto, isPlayer, now, out var f)) continue;
                ref readonly var sp = ref f.Sprite;
                int x = ax + f.OffsetX + sp.TrimX, y = ay + f.OffsetY + sp.TrimY;
                if (x > view.X1 || y > view.Y1 || x + sp.W < view.X0 || y + sp.H < view.Y0) { Culled++; continue; }
                // Chunk.AddGameObject: mobile priorityZ = z + 1
                Emit(x, y, sp, f.Hue, false, BakeJob.DepthKey(dx, dy, dz + 1, 7), pages, frame);
                MobilesDrawn++;
            }
        }
    }

    void Emit(int x, int y, in SpriteEntry sp, int hue, bool partial, uint depth, PageResidency pages, long frame)
    {
        uint flags = 0, row = 0;
        if ((hue & 0x8000) != 0) { partial = true; hue &= 0x7FFF; }
        if (hue != 0 && (hue & 0x4000) == 0 && hue - 1 < _pack.HueCount)
        {
            row = (uint)(hue - 1);
            flags = SpriteInstance.FlagHued | (partial ? SpriteInstance.FlagPartial : 0);
        }
        ref var inst = ref Instances[Count++];
        inst.X = x;
        inst.Y = y;
        inst.Whf = SpriteInstance.PackWhf(sp.W, sp.H, flags);
        inst.Uv = sp.X | ((uint)sp.Y << 16);
        inst.PageHue = sp.Page | (row << 16);
        inst.Depth = depth;
        inst.Light = 0;
        inst.Corners = 0;
        pages.Touch(sp.Page, frame);
    }
}
