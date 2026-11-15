using System.Runtime.InteropServices;
using ClassicUO.Assets;
using ClassicUO.Utility;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>Debug PNGs, all produced from the written pack (not from the source), so they double as a round-trip check.</summary>
public static class DebugOut
{
    public static string Run(UOFileManager fm, string outDir, string debugDir)
    {
        Directory.CreateDirectory(debugDir);
        int S = ConvertOptions.PageSize;
        int written = 0;

        // (a) page previews: mip1 (1024x1024) straight from the page file.
        var artDir = Path.Combine(outDir, "art");
        var texDir = Path.Combine(outDir, "tex");
        var art = SpriteTable.Read(Path.Combine(artDir, "sprites.bin"));
        var tex = SpriteTable.Read(Path.Combine(texDir, "texmaps.bin"));
        int midStaticPage = art[0x4000 + 0x0CCA].Present ? art[0x4000 + 0x0CCA].Page : 0;
        foreach (var (dir, page, name) in new[] { (artDir, 0, "art_page_0000_land"), (artDir, midStaticPage, $"art_page_{midStaticPage:D4}_statics"), (texDir, 0, "tex_page_0000") })
        {
            if (!File.Exists(PackConst.PagePath(dir, page))) continue; // --pages run
            var mip1 = PageCodec.ReadMip(PackConst.PagePath(dir, page), 1, out _);
            Png.Write(Path.Combine(debugDir, name + "_mip1.png"), S / 2, S / 2, Png.OverChecker(mip1, S / 2, S / 2, 16));
            written++;
        }
        // Full-res crop of the top-left of the statics page to inspect gutters.
        if (File.Exists(PackConst.PagePath(artDir, midStaticPage)))
        {
            var mip0 = PageCodec.ReadMip(PackConst.PagePath(artDir, midStaticPage), 0, out _).AsSpan();
            const int C = 384;
            var crop = new uint[C * C];
            for (int y = 0; y < C; y++) mip0.Slice(y * S, C).CopyTo(crop.AsSpan(y * C, C));
            Png.Write(Path.Combine(debugDir, $"art_page_{midStaticPage:D4}_crop_mip0.png"), C, C, Png.OverChecker(crop, C, C, 4));
            written++;
        }

        // (b) individual sprites at 1:1, rebuilt to their original size from the atlas.
        var picks = new (string Name, bool Tex, int Id)[]
        {
            ("land_0003_grass", false, 0x0003), ("land_00A8_water", false, 0x00A8), ("land_0071_dirt", false, 0x0071),
            ("land_00E8_mountain", false, 0x00E8),
            ("static_0E75_backpack", false, 0x4000 + 0x0E75), ("static_0CCA_tree", false, 0x4000 + 0x0CCA),
            ("static_0EED_gold", false, 0x4000 + 0x0EED), ("static_0FAF_anvil", false, 0x4000 + 0x0FAF),
            ("static_13B9_sword", false, 0x4000 + 0x13B9), ("static_0064_wall", false, 0x4000 + 0x0064),
            ("static_0B41_sign", false, 0x4000 + 0x0B41), ("static_1F14_recall_rune", false, 0x4000 + 0x1F14),
            ("tex_0003", true, 0x0003), ("tex_0002", true, 0x0002),
        };
        var sheetImgs = new List<(uint[] Px, int W, int H)>();
        foreach (var grp in picks.GroupBy(p => (p.Tex, p.Tex ? tex[p.Id].Page : art[p.Id].Page)))
        {
            var dir = grp.Key.Tex ? texDir : artDir;
            if (grp.Key.Item2 == PackConst.NoPage || !File.Exists(PackConst.PagePath(dir, grp.Key.Item2))) continue;
            var mip0 = PageCodec.ReadMip(PackConst.PagePath(dir, grp.Key.Item2), 0, out _).AsSpan();
            foreach (var p in grp)
            {
                var e = (p.Tex ? tex : art)[p.Id];
                var img = new uint[e.OrigW * e.OrigH];
                for (int y = 0; y < e.H; y++)
                    mip0.Slice((e.Y + y) * S + e.X, e.W).CopyTo(img.AsSpan((e.TrimY + y) * e.OrigW + e.TrimX, e.W));
                Png.Write(Path.Combine(debugDir, p.Name + ".png"), e.OrigW, e.OrigH, img);
                sheetImgs.Add((img, e.OrigW, e.OrigH));
                written++;
            }
        }
        // contact sheet at 3x, over a checkerboard, with each sprite's original bounds framed in red.
        if (sheetImgs.Count > 0)
        {
            const int K = 3, Pad = 6;
            int sw = sheetImgs.Sum(i => i.W * K + Pad) + Pad, sh = sheetImgs.Max(i => i.H) * K + 2 * Pad;
            var sheet = new uint[sw * sh];
            int ox = Pad;
            foreach (var (px, w, h) in sheetImgs)
            {
                var chk = Png.OverChecker(px, w, h, 4);
                for (int y = 0; y < h * K; y++)
                    for (int x = 0; x < w * K; x++)
                    {
                        bool frame = x == 0 || y == 0 || x == w * K - 1 || y == h * K - 1;
                        sheet[(Pad + y) * sw + ox + x] = frame ? 0xFF0000FF : chk[(y / K) * w + x / K];
                    }
                ox += w * K + Pad;
            }
            Png.Write(Path.Combine(debugDir, "sprites_sheet_3x.png"), sw, sh, Png.OverChecker(sheet, sw, sh));
            written++;
        }

        // (b2) partial-hue fidelity: source | stored (decoded) | gray-broken map, 4x. Red = was exact gray, no longer is
        // (would not be hued); blue = was coloured, became exact gray (would be hued wrongly); dim = unchanged.
        {
            var stat = fm.TileData.StaticData;
            bool Ph(int i) => i - 0x4000 < stat.Length && stat[i - 0x4000].IsPartialHue; // tiledata, not the retired sprite bit
            int pid = 0x4000 + 0x1F00; // partial-hue in this client
            if (!art[pid].Present || !Ph(pid))
                pid = Enumerable.Range(0x4000, art.Length - 0x4000).FirstOrDefault(i => art[i].Present && Ph(i) && art[i].W * art[i].H > 1500);
            var e = art[pid];
            if (pid != 0 && File.Exists(PackConst.PagePath(artDir, e.Page)))
            {
                var page = PageCodec.ReadMip(PackConst.PagePath(artDir, e.Page), 0, out var ph);
                var srcInfo = fm.Arts.GetArt((uint)pid);
                var src = srcInfo.Pixels.ToArray();
                int w = e.W, h = e.H;
                var a0 = new uint[w * h]; var b0 = new uint[w * h]; var m0 = new uint[w * h];
                int broken = 0, turned = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        uint sp = src[(e.TrimY + y) * srcInfo.Width + e.TrimX + x], dp = page[(e.Y + y) * S + e.X + x];
                        a0[y * w + x] = sp; b0[y * w + x] = dp;
                        if ((sp >> 24) == 0) { m0[y * w + x] = 0xFF202020; continue; }
                        bool gs = (sp & 0xFF) == ((sp >> 8) & 0xFF) && (sp & 0xFF) == ((sp >> 16) & 0xFF);
                        bool gd = (dp & 0xFF) == ((dp >> 8) & 0xFF) && (dp & 0xFF) == ((dp >> 16) & 0xFF);
                        uint dim = ((sp & 0xFEFEFE) >> 1) | 0xFF000000;
                        m0[y * w + x] = gs && !gd ? 0xFF0000FF : !gs && gd ? 0xFFFF4000 : dim;
                        if (gs && !gd) broken++; else if (!gs && gd) turned++;
                    }
                const int K = 4, Gap = 8;
                int W = 3 * w * K + 2 * Gap, H = h * K;
                var o = new uint[W * H];
                Array.Fill(o, 0xFFFFFFFF);
                var panels = new[] { Png.OverChecker(a0, w, h, 4), Png.OverChecker(b0, w, h, 4), m0 };
                for (int i = 0; i < 3; i++)
                    for (int y = 0; y < H; y++)
                        for (int x = 0; x < w * K; x++)
                            o[y * W + i * (w * K + Gap) + x] = panels[i][(y / K) * w + x / K];
                Png.Write(Path.Combine(debugDir, $"partialhue_{pid - 0x4000:X4}_{ph.Format}_4x.png"), W, H, o);
                Console.WriteLine($"  partial-hue sample 0x{pid - 0x4000:X4} ({ph.Format}): {broken} gray px broken, {turned} coloured px turned gray");
                written++;
            }
        }

        // (c) radar of facet 0 from the region files.
        var fdir = Path.Combine(outDir, "map", "0");
        if (Directory.Exists(fdir) && ConvertOptions.MaxRegions == int.MaxValue)
        {
            var fi = FacetFileIo.Read(fdir);
            var radar = fm.Hues.RadarCol;
            var full = new uint[fi.Width * fi.Height];
            var zbuf = new sbyte[fi.Width * fi.Height];
            int rxc = (fi.BlocksX + 63) / 64, ryc = (fi.BlocksY + 63) / 64;
            Parallel.For(0, rxc * ryc, ConvertOptions.Par, r =>
            {
                int rx = r % rxc, ry = r / rxc;
                var (h, blocks) = RegionFile.Read(PackConst.RegionPath(fdir, rx, ry));
                for (int by = 0; by < h.BlocksH; by++)
                    for (int bx = 0; bx < h.BlocksW; bx++)
                    {
                        var b = blocks[by * h.BlocksW + bx];
                        if (b.IsEmpty) continue;
                        int cx0 = (rx * 64 + bx) * 8, cy0 = (ry * 64 + by) * 8;
                        for (int c = 0; c < 64; c++)
                        {
                            int i = (cy0 + c / 8) * fi.Width + cx0 + c % 8;
                            full[i] = Color(radar, b.Land[c].TileId);
                            zbuf[i] = b.Land[c].Z;
                        }
                        foreach (var s in b.Statics)
                        {
                            int i = (cy0 + s.Y) * fi.Width + cx0 + s.X;
                            if (s.Z >= zbuf[i]) { full[i] = Color(radar, 0x4000 + s.Graphic); zbuf[i] = s.Z; }
                        }
                    }
            });
            int f = 4, dw = fi.Width / f, dh = fi.Height / f;
            var small = new uint[dw * dh];
            Parallel.For(0, dh, ConvertOptions.Par, y =>
            {
                for (int x = 0; x < dw; x++)
                {
                    uint r = 0, g = 0, b = 0;
                    for (int yy = 0; yy < f; yy++)
                        for (int xx = 0; xx < f; xx++)
                        {
                            uint p = full[(y * f + yy) * fi.Width + x * f + xx];
                            r += p & 0xFF; g += (p >> 8) & 0xFF; b += (p >> 16) & 0xFF;
                        }
                    int n = f * f;
                    small[y * dw + x] = (uint)(r / n) | ((uint)(g / n) << 8) | ((uint)(b / n) << 16) | 0xFF000000;
                }
            });
            Png.Write(Path.Combine(debugDir, $"radar_facet0_{dw}x{dh}.png"), dw, dh, small);
            // and a 1:1 crop around Britain
            const int CW = 640, CH = 480, CX = 1200, CY = 1400;
            var crop = new uint[CW * CH];
            for (int y = 0; y < CH; y++) full.AsSpan((CY + y) * fi.Width + CX, CW).CopyTo(crop.AsSpan(y * CW, CW));
            Png.Write(Path.Combine(debugDir, "radar_facet0_britain_1to1.png"), CW, CH, crop);
            written += 2;
        }
        return $"{written} PNGs in {debugDir}";
    }

    private static uint Color(ushort[] radar, int idx) =>
        HuesHelper.Color16To32(idx < radar.Length ? radar[idx] : (ushort)0) | 0xFF000000;
}
