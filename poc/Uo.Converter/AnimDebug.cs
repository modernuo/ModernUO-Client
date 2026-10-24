using Uo.Pack;

namespace Uo.Converter;

/// <summary>
/// Walk-cycle sheets read back from the written pack: one row per UO direction 0..7 (mirrored rows flipped), one column
/// per frame. Every frame is placed by the ClassicUO anchor rule against a common tile, drawn as a gray 44x44 diamond.
/// </summary>
public static class AnimDebug
{
    /// <summary>Human male, human female, horse, dragon. The first UOP body with a walk cycle is added at run time.</summary>
    public static readonly int[] Bodies = [400, 401, 0xC8, 0x3B];

    public static string Run(string outDir, string debugDir)
    {
        if (Directory.Exists(debugDir)) Directory.Delete(debugDir, true);
        Directory.CreateDirectory(debugDir);
        var dir = Path.Combine(outDir, "anim");
        var (bodies, dirs, frames) = AnimTableFile.Read(Path.Combine(dir, AnimTableFile.FileName));
        var picks = Bodies.Select(b => (b, "")).ToList();
        int uop = Enumerable.Range(0, bodies.Length).FirstOrDefault(b => (bodies[b].Source & AnimSource.Uop) != 0 && bodies[b].Source != AnimSource.None &&
            Enumerable.Range(0, 8).All(d => AnimTableFile.Frames(bodies, dirs, frames, b, 0, d, out _).Length > 0), -1);
        if (uop >= 0) picks.Add((uop, "_uop"));
        var pages = new Dictionary<int, uint[]>();
        var written = new List<string>();
        foreach (var (body, tag) in picks)
        {
            var name = Draw(body, tag, bodies, dirs, frames, dir, pages, debugDir);
            if (name != null) written.Add(name);
        }
        return $"{written.Count} PNGs in {debugDir}: {string.Join(", ", written)}";
    }

    private static string? Draw(int body, string tag, AnimBodyEntry[] bodies, AnimDirEntry[] dirs, AnimFrameEntry[] frames, string dir, Dictionary<int, uint[]> pages, string debugDir)
    {
        const int Pad = 8, K = 2;
        int S = Verify.PageSide(dir);
        const int action = 0; // walk for every animation type (people WalkUnarmed, low/high Walk)
        int cols = 0, minX = -22, minY = -44, maxX = 22, maxY = 4;
        for (int d = 0; d < AnimConst.Directions; d++)
        {
            var fr = AnimTableFile.Frames(bodies, dirs, frames, body, action, d, out var de);
            cols = Math.Max(cols, fr.Length);
            foreach (var f in fr)
            {
                if (!f.Sprite.Present) continue;
                var (x0, y0) = Place(f, (de.Flags & AnimDirFlags.Mirrored) != 0);
                minX = Math.Min(minX, x0); minY = Math.Min(minY, y0);
                maxX = Math.Max(maxX, x0 + f.Sprite.W); maxY = Math.Max(maxY, y0 + f.Sprite.H);
            }
        }
        if (cols == 0) return null;
        int cw = maxX - minX + Pad, ch = maxY - minY + Pad;
        int w = cols * cw, h = AnimConst.Directions * ch;
        var img = new uint[w * h];
        for (int d = 0; d < AnimConst.Directions; d++)
        {
            var fr = AnimTableFile.Frames(bodies, dirs, frames, body, action, d, out var de);
            bool mirrored = (de.Flags & AnimDirFlags.Mirrored) != 0;
            for (int i = 0; i < fr.Length; i++)
            {
                int ax = i * cw + Pad / 2 - minX, ay = d * ch + Pad / 2 - minY; // anchor = tile (x + 22, y + 22 - 3)
                for (int t = 0; t <= 22; t++) // tile diamond, centred 3 px below the anchor
                    foreach (var (px, py) in new[] { (ax - 22 + t, ay + 3 - t / 2), (ax + 22 - t, ay + 3 - t / 2), (ax - 22 + t, ay + 3 + t / 2), (ax + 22 - t, ay + 3 + t / 2) })
                        if (px >= 0 && py >= 0 && px < w && py < h) img[py * w + px] = 0xFF909090;
                var f = fr[i];
                if (!f.Sprite.Present) continue;
                var e = f.Sprite;
                if (!pages.TryGetValue(e.Page, out var page)) pages[e.Page] = page = PageCodec.ReadMip(PackConst.PagePath(dir, e.Page), 0, out _);
                var (x0, y0) = Place(f, mirrored);
                for (int y = 0; y < e.H; y++)
                    for (int x = 0; x < e.W; x++)
                    {
                        uint p = page[(e.Y + y) * S + e.X + (mirrored ? e.W - 1 - x : x)];
                        if ((p >> 24) == 0) continue;
                        img[(ay + y0 + y) * w + ax + x0 + x] = p;
                    }
                img[ay * w + ax] = 0xFF0000FF;
            }
        }
        var big = new uint[w * K * h * K];
        var chk = Png.OverChecker(img, w, h, 8);
        for (int y = 0; y < h * K; y++)
            for (int x = 0; x < w * K; x++)
                big[y * w * K + x] = chk[(y / K) * w + x / K];
        var name = $"body_{body:D4}_0x{body:X3}{tag}_walk_{cols}f_2x.png";
        Png.Write(Path.Combine(debugDir, name), w * K, h * K, big);
        return name;
    }

    /// <summary>Top-left of the trimmed rect relative to the anchor (MobileView.DrawInternal, with the trim applied).</summary>
    private static (int X, int Y) Place(AnimFrameEntry f, bool mirrored)
    {
        var e = f.Sprite;
        int x = mirrored ? -(e.OrigW - f.CenterX) + (e.OrigW - e.TrimX - e.W) : -f.CenterX + e.TrimX;
        int y = -(e.OrigH + f.CenterY) + e.TrimY;
        return (x, y);
    }
}
