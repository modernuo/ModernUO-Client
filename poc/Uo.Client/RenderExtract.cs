using System.Diagnostics;
using Friflo.Engine.ECS;
using Uo.Pack;
using Uo.Render;

namespace Uo.Client;

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
    readonly MobileFrame[] _frames = new MobileFrame[Layers.Count + 2];
    PageResidency _animPages;
    public int SpritesDrawn;
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

    /// <param name="animPages">Indexed8 anim page residency, or null when anim pages share the art array.</param>
    public void Run(in RectI view, PageResidency pages, PageResidency animPages, long frame, long now)
    {
        long t0 = Stopwatch.GetTimestamp();
        _animPages = animPages;
        Count = 0; MobilesDrawn = 0; ItemsDrawn = 0; Culled = 0; SpritesDrawn = 0;
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
        var frames = _frames.AsSpan();
        foreach (var (pos, motion, gfx, hue, facing, ents) in q.Chunks)
        {
            var ps = pos.Span; var ms = motion.Span; var gs = gfx.Span; var hs = hue.Span; var fs = facing.Span;
            for (int i = 0; i < ps.Length && Count < cap; i++)
            {
                Interpolate(ps[i], ms[i], now, out float fx, out float fy, out float fz, out int dx, out int dy, out int dz, out bool moving);
                TileToWorld(fx, fy, fz, out int tx, out int ty);
                int ax = tx + 22, ay = ty + 22 - 3;
                // cheap reject before the frame lookup: mobiles (dragons included) stay within ~260 px of the anchor
                if (ax + 200 < view.X0 || ax - 200 > view.X1 || ay + 120 < view.Y0 || ay - 260 > view.Y1) { Culled++; continue; }
                var e = ents.EntityAt(i);
                var input = new MobileAnimInput
                {
                    Body = gs[i].Value, Hue = hs[i].Value, Direction = (byte)(fs[i].Value & 7), Moving = moving,
                    Running = (fs[i].Value & 0x80) != 0, WarMode = (e.GetComponent<Flags>().Value & 0x40) != 0, Serial = e.GetComponent<Serial>().Value,
                };
                int n = _mobiles.GetFrames(input, e.GetComponent<Equipment>(), now, frames);
                if (n == 0) continue;
                // Chunk.AddGameObject: mobile priorityZ = z + 1. All of a mobile's sprites share one depth key and the
                // depth test is LESS, so the first drawn wins ties: emit the top layer first.
                uint depth = BakeJob.DepthKey(dx, dy, dz + 1, 7);
                bool any = false;
                for (int k = n - 1; k >= 0 && Count < cap; k--)
                {
                    ref readonly var f = ref frames[k];
                    ref readonly var sp = ref f.Sprite;
                    int x = ax + f.X, y = ay + f.Y;
                    if (x > view.X1 || y > view.Y1 || x + sp.W < view.X0 || y + sp.H < view.Y0) continue;
                    EmitFrame(x, y, f, depth, pages, frame);
                    any = true;
                }
                if (any) MobilesDrawn++; else Culled++;
            }
        }
    }

    void EmitFrame(int x, int y, in MobileFrame f, uint depth, PageResidency pages, long frame)
    {
        ref readonly var sp = ref f.Sprite;
        uint flags = (f.Mirror ? SpriteInstance.FlagMirror : 0) | (f.Indexed ? SpriteInstance.FlagIndexed : 0), row = 0;
        int hue = f.Hue;
        if (hue != 0 && (hue & 0x4000) == 0 && hue - 1 < _pack.HueCount)
        {
            row = (uint)(hue - 1);
            flags |= SpriteInstance.FlagHued | (f.Partial ? SpriteInstance.FlagPartial : 0);
        }
        ref var inst = ref Instances[Count++];
        inst.X = x;
        inst.Y = y;
        inst.Whf = SpriteInstance.PackWhf(sp.W, sp.H, flags);
        inst.Uv = sp.X | ((uint)sp.Y << 16);
        inst.PageHue = (uint)f.Page | (row << 16);
        inst.Depth = depth;
        inst.Light = 0;
        inst.Corners = f.Palette;
        if (f.Indexed) _animPages?.Touch(f.Page - _pack.AnimPageBase, frame);
        else pages.Touch(f.Page, frame);
        SpritesDrawn++;
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
