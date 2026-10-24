using System.Runtime.InteropServices;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>A decoded, trimmed sprite ready for packing. Pixels are RGBA8 (R in the low byte).</summary>
public sealed class SpriteImage
{
    public int Id;
    public int W, H, TrimX, TrimY, OrigW, OrigH;
    public uint[] Pixels = [];
    public SpriteFlags Flags;
    /// <summary>When &gt;= 0 this sprite shares the source data of another id (art.def / TexTerr.def alias) and its atlas rect.</summary>
    public int AliasOf = -1;

    /// <summary>Crops fully transparent borders. Returns null when every pixel is transparent.</summary>
    public static SpriteImage? Trim(int id, ReadOnlySpan<uint> px, int w, int h, SpriteFlags flags)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            var row = px.Slice(y * w, w);
            for (int x = 0; x < w; x++)
            {
                if ((row[x] >> 24) == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                maxY = y;
            }
        }
        if (maxX < 0) return null;

        int tw = maxX - minX + 1, th = maxY - minY + 1;
        var outPx = new uint[tw * th];
        for (int y = 0; y < th; y++)
            px.Slice((minY + y) * w + minX, tw).CopyTo(outPx.AsSpan(y * tw, tw));

        return new SpriteImage { Id = id, W = tw, H = th, TrimX = minX, TrimY = minY, OrigW = w, OrigH = h, Pixels = outPx, Flags = flags };
    }
}

/// <summary>Skyline bottom-left rectangle packer.</summary>
public sealed class SkylinePacker
{
    private readonly int _w, _h;
    private readonly List<(int X, int Y, int W)> _nodes = new();
    public long UsedArea { get; private set; }

    public SkylinePacker(int w, int h) { _w = w; _h = h; _nodes.Add((0, 0, w)); }

    public bool TryInsert(int w, int h, out int x, out int y)
    {
        int bestIdx = -1, bestTop = int.MaxValue, bestWidth = int.MaxValue;
        x = y = 0;
        for (int i = 0; i < _nodes.Count; i++)
        {
            int fy = Fit(i, w, h);
            if (fy < 0) continue;
            int top = fy + h;
            if (top < bestTop || (top == bestTop && _nodes[i].W < bestWidth))
            {
                bestIdx = i; bestTop = top; bestWidth = _nodes[i].W; x = _nodes[i].X; y = fy;
            }
        }
        if (bestIdx < 0) return false;
        Add(bestIdx, x, y, w, h);
        UsedArea += (long)w * h;
        return true;
    }

    private int Fit(int i, int w, int h)
    {
        int x = _nodes[i].X;
        if (x + w > _w) return -1;
        int widthLeft = w, y = 0;
        while (widthLeft > 0)
        {
            if (i >= _nodes.Count) return -1;
            y = Math.Max(y, _nodes[i].Y);
            if (y + h > _h) return -1;
            widthLeft -= _nodes[i].W;
            i++;
        }
        return y;
    }

    private void Add(int idx, int x, int y, int w, int h)
    {
        _nodes.Insert(idx, (x, y + h, w));
        for (int i = idx + 1; i < _nodes.Count; i++)
        {
            var prev = _nodes[i - 1];
            var n = _nodes[i];
            if (n.X < prev.X + prev.W)
            {
                int shrink = prev.X + prev.W - n.X;
                if (n.W - shrink <= 0) { _nodes.RemoveAt(i); i--; }
                else { _nodes[i] = (n.X + shrink, n.Y, n.W - shrink); break; }
            }
            else break;
        }
        for (int i = 0; i < _nodes.Count - 1; i++)
        {
            if (_nodes[i].Y == _nodes[i + 1].Y)
            {
                _nodes[i] = (_nodes[i].X, _nodes[i].Y, _nodes[i].W + _nodes[i + 1].W);
                _nodes.RemoveAt(i + 1);
                i--;
            }
        }
    }
}

public sealed class AtlasResult
{
    public SpriteEntry[] Entries = [];
    public int PageCount;
    public long PageBytes;
    public double Occupancy;
    public int Packed;
    public int Aliased;
}

public static class AtlasBuilder
{
    /// <summary>
    /// Packs sprites (already in id order) into pages and writes page files plus nothing else; returns the sprite table.
    /// Packing is sequential in id order so neighbouring ids share a page (streaming locality); a page is closed when
    /// the next sprite doesn't fit, except that we keep the previous page open for small sprites to fill holes.
    /// </summary>
    public static AtlasResult Build(string dir, int tableSize, IReadOnlyList<SpriteImage> sprites, SpriteEntry[]? preset = null)
    {
        Directory.CreateDirectory(dir);
        const int S = PackConst.PageSize, G = PackConst.Gutter;
        var entries = new SpriteEntry[tableSize];
        for (int i = 0; i < tableSize; i++) entries[i] = preset != null ? preset[i] : SpriteEntry.Absent;

        var pages = new List<List<SpriteImage>>();
        var packers = new List<SkylinePacker>();
        int Open() { packers.Add(new SkylinePacker(S, S)); pages.Add(new()); return packers.Count - 1; }
        int cur = Open();
        int prev = -1;
        int packed = 0, aliased = 0;

        foreach (var sp in sprites)
        {
            if (sp.AliasOf >= 0) continue;
            int pw = sp.W + 2 * G, ph = sp.H + 2 * G;
            if (pw > S || ph > S) throw new InvalidOperationException($"sprite {sp.Id} {sp.W}x{sp.H} too large for a page");
            int page, x, y;
            if (packers[cur].TryInsert(pw, ph, out x, out y)) page = cur;
            else if (prev >= 0 && packers[prev].TryInsert(pw, ph, out x, out y)) page = prev;
            else
            {
                prev = cur;
                cur = Open();
                if (!packers[cur].TryInsert(pw, ph, out x, out y)) throw new InvalidOperationException("cannot pack");
                page = cur;
            }
            pages[page].Add(sp);
            entries[sp.Id] = new SpriteEntry
            {
                Page = (ushort)page, X = (ushort)(x + G), Y = (ushort)(y + G), W = (ushort)sp.W, H = (ushort)sp.H,
                TrimX = (short)sp.TrimX, TrimY = (short)sp.TrimY, OrigW = (ushort)sp.OrigW, OrigH = (ushort)sp.OrigH, Flags = sp.Flags,
            };
            packed++;
        }
        foreach (var sp in sprites)
        {
            if (sp.AliasOf < 0) continue;
            var e = entries[sp.AliasOf];
            e.Flags = sp.Flags;
            entries[sp.Id] = e;
            aliased++;
        }

        int chainBytes = AtlasPage.ChainBytes(S, PackConst.PageMipCount);
        Parallel.For(0, pages.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 12) }, p =>
        {
            var chain = GC.AllocateUninitializedArray<byte>(chainBytes);
            var mip0 = MemoryMarshal.Cast<byte, uint>(chain.AsSpan(0, AtlasPage.MipBytes(S, 0)));
            mip0.Clear();
            foreach (var sp in pages[p])
            {
                ref var e = ref entries[sp.Id];
                Blit(mip0, S, e.X, e.Y, sp.Pixels, sp.W, sp.H, G);
            }
            BuildMips(chain, S, PackConst.PageMipCount);
            AtlasPage.Write(PackConst.PagePath(dir, p), S, PackConst.PageMipCount, chain);
        });

        long used = 0;
        foreach (var pk in packers) used += pk.UsedArea;
        return new AtlasResult
        {
            Entries = entries, PageCount = pages.Count, Packed = packed, Aliased = aliased,
            PageBytes = (long)pages.Count * (chainBytes + 32),
            Occupancy = used / (double)((long)pages.Count * S * S),
        };
    }

    /// <summary>Copies a sprite to (x,y) and fills a gutter of g pixels around it by clamping (edge extrusion).</summary>
    public static void Blit(Span<uint> dst, int stride, int x, int y, ReadOnlySpan<uint> src, int w, int h, int g)
    {
        for (int yy = -g; yy < h + g; yy++)
        {
            int sy = Math.Clamp(yy, 0, h - 1);
            var srow = src.Slice(sy * w, w);
            var drow = dst.Slice((y + yy) * stride + x - g, w + 2 * g);
            for (int i = 0; i < g; i++) { drow[i] = srow[0]; drow[w + g + i] = srow[w - 1]; }
            srow.CopyTo(drow.Slice(g, w));
        }
    }

    /// <summary>Alpha-aware 2×2 box filter: colors are weighted by alpha (premultiply, average, unpremultiply).</summary>
    public static void BuildMips(byte[] chain, int size, int mips)
    {
        int off = 0;
        for (int m = 1; m < mips; m++)
        {
            int ss = size >> (m - 1), ds = size >> m;
            var src = MemoryMarshal.Cast<byte, uint>(chain.AsSpan(off, AtlasPage.MipBytes(size, m - 1)));
            off += AtlasPage.MipBytes(size, m - 1);
            var dst = MemoryMarshal.Cast<byte, uint>(chain.AsSpan(off, AtlasPage.MipBytes(size, m)));
            for (int y = 0; y < ds; y++)
            {
                var r0 = src.Slice(2 * y * ss, ss);
                var r1 = src.Slice((2 * y + 1) * ss, ss);
                var drow = dst.Slice(y * ds, ds);
                for (int x = 0; x < ds; x++)
                {
                    uint a = r0[2 * x], b = r0[2 * x + 1], c = r1[2 * x], d = r1[2 * x + 1];
                    if ((a | b | c | d) == 0) { drow[x] = 0; continue; }
                    uint aa = a >> 24, ab = b >> 24, ac = c >> 24, ad = d >> 24;
                    uint sa = aa + ab + ac + ad;
                    if (sa == 0) { drow[x] = 0; continue; }
                    uint sr = (a & 0xFF) * aa + (b & 0xFF) * ab + (c & 0xFF) * ac + (d & 0xFF) * ad;
                    uint sg = ((a >> 8) & 0xFF) * aa + ((b >> 8) & 0xFF) * ab + ((c >> 8) & 0xFF) * ac + ((d >> 8) & 0xFF) * ad;
                    uint sb = ((a >> 16) & 0xFF) * aa + ((b >> 16) & 0xFF) * ab + ((c >> 16) & 0xFF) * ac + ((d >> 16) & 0xFF) * ad;
                    uint half = sa >> 1;
                    uint r = (sr + half) / sa, gg = (sg + half) / sa, bb = (sb + half) / sa;
                    uint al = (sa + 2) >> 2;
                    drow[x] = r | (gg << 8) | (bb << 16) | (al << 24);
                }
            }
        }
    }
}
