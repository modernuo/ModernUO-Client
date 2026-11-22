using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicUO.Assets;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using ClassicUO.UnitTests.Assets;
using Uo.Pack;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// light_occlusion round 4 (owner repro 2026-11-22, release b00f10801, light level 05): the owner's spots built from the
    /// real Felucca layout (map statics, ModernUO's Moonglow decoration items) with the pack's tiledata and art pixel masks.
    /// Each light's occluders are the objects LightOcclusion.Hides accepts: drawn after the source, an occluder by tiledata,
    /// and art covering the light point (LightOcclusion.LightPoint / CoverPoint and the art's pixel there), as
    /// GameScene.CollectOccluders tests them. Round 3 masked every object drawn after the source.
    /// </summary>
    public sealed class LightOcclusionSceneTests
    {
        private readonly ITestOutputHelper _output;

        public LightOcclusionSceneTests(ITestOutputHelper output) => _output = output;

        private readonly record struct Obj(int X, int Y, int Z, ushort Graphic)
        {
            public override string ToString() => $"0x{Graphic:X4} at {X},{Y},{Z}";
        }

        // the pack's static tiledata and art masks, loaded once per test run
        private sealed class PackArt
        {
            public readonly StaticTiles[] Statics;
            private readonly SpriteEntry[] _sprites;
            private readonly PackPages _pages;
            private readonly PixelPicker _picker = new PixelPicker();
            private readonly Dictionary<ushort, (int W, int H, int BX, int BY, int BW, int BH)> _sizes = new Dictionary<ushort, (int W, int H, int BX, int BY, int BW, int BH)>();

            private PackArt(string dir)
            {
                TileDataLoader.Read(Path.Combine(dir, TableConst.TileDataFile), out _, out Statics);
                _sprites = SpriteTable.Read(Path.Combine(dir, "art", "sprites.bin"));
                _pages = new PackPages(Path.Combine(dir, "art"), 64 << 20);
                (_lights, _) = LightFile.Read(Path.Combine(dir, TableConst.LightsFile));
            }

            private readonly LightEntry[] _lights;

            // light shape id's texture size (LightsLoader.Extent)
            public (int W, int H) Light(int id) => (uint)id < (uint)_lights.Length ? (_lights[id].Width, _lights[id].Height) : (0, 0);

            private static PackArt _instance;
            private static readonly object _lock = new object();

            public static PackArt Get()
            {
                lock (_lock)
                {
                    return _instance ??= new PackArt(TileDrawOracleTests.PackDir());
                }
            }

            public (int W, int H) Size(ushort g)
            {
                var a = Art(g);

                return (a.W, a.H);
            }

            // the art's size and its visible bounds (Art's _realArtBounds)
            public (int W, int H, int BX, int BY, int BW, int BH) Art(ushort g)
            {
                lock (_lock)
                {
                    if (_sizes.TryGetValue(g, out var size))
                    {
                        return size;
                    }

                    ref readonly SpriteEntry e = ref _sprites[0x4000 + g];

                    if (!e.Present || e.OrigW == 0 || e.OrigH == 0)
                    {
                        return _sizes[g] = (0, 0, 0, 0, 0, 0);
                    }

                    uint[] pixels = PackSprites.DecodeColor(_pages, in e);
                    var trim = PackSprites.Trim(in e);
                    _picker.SetTrimmed(g, e.OrigW, e.OrigH, trim.X, trim.Y, trim.W, trim.H, pixels);
                    PixelPicker.VisibleBounds(pixels, e.OrigW, e.OrigH, trim.X, trim.Y, trim.W, trim.H, out int x0, out int y0, out int x1, out int y1);

                    return _sizes[g] = (e.OrigW, e.OrigH, x0, y0, x1 - x0, y1 - y0);
                }
            }

            public bool Pixel(ushort g, int x, int y)
            {
                lock (_lock)
                {
                    return _picker.Get(g, x, y);
                }
            }
        }

        // ViewMargin.Anchor with no offset: the static's anchor
        private static (int X, int Y) Anchor(Obj o) => ((o.X - o.Y) * 22 - 22, (o.X + o.Y) * 22 - (o.Z << 2) - 22);

        // the objects that hide the glow of src's light with the switch on (GameScene.Occluder without the draw state): art
        // covering the light point, or for a wall source a wall piece under the light quad (the light shape's size, centred
        // on anchor + 22 as AddLight places it) facing away from it
        // (srcHeight: the source's top, LightOcclusion.SourceTop when null)
        private static List<Obj> Occluders(PackArt pack, Obj src, IEnumerable<Obj> scene, bool on = true, int? srcHeight = null)
        {
            var (sax, say) = Anchor(src);
            var a = pack.Art(src.Graphic);
            LightOcclusion.LightPoint(sax, say, a.W, a.H, a.BX, a.BY, a.BW, a.BH, out int px, out int py);
            int srcTop = srcHeight ?? LampTop(pack, src);
            byte srcFacing = pack.Statics[src.Graphic].IsWall ? LightOcclusion.Facing(a.W, a.BX, a.BW) : LightOcclusion.FacingNone;
            var (lw, lh) = pack.Light(pack.Statics[src.Graphic].Layer);
            int left = sax + 22 - lw / 2, top = say + 22 - lh / 2;
            var hidden = new List<Obj>();

            foreach (Obj o in scene)
            {
                if (o == src)
                {
                    continue;
                }

                ref StaticTiles data = ref pack.Statics[o.Graphic];
                var oa = pack.Art(o.Graphic);
                var (ax, ay) = Anchor(o);
                bool covers = oa.W != 0 && LightOcclusion.CoverPoint(ax, ay, oa.W, oa.H, px, py, out int lx, out int ly) && pack.Pixel(o.Graphic, lx, ly);
                bool facesAway = data.IsWall && oa.W != 0 && LightOcclusion.ArtOverlaps(ax, ay, oa.W, oa.H, left, top, left + lw, top + lh) &&
                    LightOcclusion.FacesAway(srcFacing, LightOcclusion.Facing(oa.W, oa.BX, oa.BW), o.X - src.X, o.Y - src.Y);

                if (LightOcclusion.Hides(on, false, o.X - src.X, o.Y - src.Y, o.Z - src.Z, srcTop, ref data, covers, facesAway))
                {
                    hidden.Add(o);
                }
            }

            return hidden;
        }

        // the source's height (LightOcclusion.SourceTop as GameScene.CollectOccluders takes it)
        private static int LampTop(PackArt pack, Obj src)
        {
            var (_, say) = Anchor(src);
            var a = pack.Art(src.Graphic);
            LightOcclusion.LightPoint(0, say, a.W, a.H, a.BX, a.BY, a.BW, a.BH, out _, out int py);

            return LightOcclusion.SourceTop(pack.Statics[src.Graphic].Height, say, py);
        }

        // round 3's rule: every occluder drawn after the source (for the record: these cases are the owner's regression)
        private static int Round3Count(PackArt pack, Obj src, IEnumerable<Obj> scene, int reach)
        {
            int n = 0;

            foreach (Obj o in scene)
            {
                int dx = o.X - src.X, dy = o.Y - src.Y;

                if (o != src && Math.Abs(dx) <= reach && Math.Abs(dy) <= reach && (dx + dy > 0 || dx == 0 && dy == 0 && o.Z > src.Z) &&
                    LightOcclusion.OccluderData(ref pack.Statics[o.Graphic]))
                {
                    n++;
                }
            }

            return n;
        }

        private static IEnumerable<Obj> Parse(string layout)
        {
            foreach (string line in layout.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                yield return new Obj(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), Convert.ToUInt16(p[3], 16));
            }
        }

        // Moonglow bank, Felucca: map statics (walls, windows, counters, tables) of the hall around 4464 1159 and the
        // candles on the table (ModernUO Decoration/Britannia/moonglow.cfg: CandleLarge 0x0B1A at 4470 1161 4, 4474 1161 4)
        private const string BankHall = @"
4462 1154 0 015F
4462 1154 20 0162
4462 1155 0 0162
4462 1155 20 0162
4462 1156 0 015F
4462 1156 20 0162
4462 1157 0 015F
4462 1157 20 0162
4462 1158 0 0162
4462 1158 20 0162
4462 1159 0 015F
4462 1159 20 0162
4462 1160 0 015F
4462 1160 20 0162
4462 1161 0 0162
4462 1161 20 0162
4462 1162 0 015F
4462 1162 20 0162
4463 1154 0 0B3D
4463 1155 0 0B3F
4463 1158 0 0B3F
4463 1159 0 0B3D
4463 1160 0 0B3F
4463 1163 0 0B3F
4467 1154 0 015F
4467 1154 20 0162
4467 1155 0 015F
4467 1155 20 0162
4468 1155 0 0160
4468 1155 20 0163
4469 1155 0 0163
4469 1155 20 0163
4470 1155 0 0160
4470 1155 20 0163
4468 1160 0 0B86
4468 1161 0 0B86
4468 1162 0 0B85
4469 1160 0 0B86
4469 1161 0 0B86
4469 1162 0 0B86
4470 1160 0 0B86
4470 1161 0 0B86
4470 1162 0 0B86
4471 1160 0 0B86
4471 1161 0 0B86
4471 1162 0 0B86
4472 1160 0 0B86
4472 1161 0 0B86
4472 1162 0 0B85
4473 1160 0 0B86
4473 1161 0 0B86
4473 1162 0 0B86
4474 1160 0 0B86
4474 1161 0 0B86
4474 1162 0 0B86
4475 1160 0 0B86
4475 1161 0 0B86
4475 1162 0 0B86
4476 1160 0 0B83
4476 1161 0 0B87
4476 1162 0 0B84
4470 1161 4 0B1A
4474 1161 4 0B1A
";

        [TileDataPackFact]
        public void Bank_candles_on_the_table_light_the_table_pieces_drawn_after_them()
        {
            PackArt pack = PackArt.Get();
            var scene = Parse(BankHall).ToList();

            foreach (Obj candle in scene.Where(o => o.Graphic == 0x0B1A))
            {
                var hidden = Occluders(pack, candle, scene);
                _output.WriteLine($"candle {candle}: round 3 masked {Round3Count(pack, candle, scene, 8)}, now {hidden.Count}: {string.Join(", ", hidden)}");
                Assert.True(Round3Count(pack, candle, scene, 8) > 0);
                Assert.Empty(hidden);
            }
        }

        // "x y z graphic" per object: Felucca map statics (statics0; flat h0 surfaces left out, they never occlude) plus ModernUO's
        // Moonglow decoration items. ZigZag: the bank's south-west outer walls (owner's first repro, 4462 1169 0). Lamps:
        // 4436..4452 x 1160..1178 and the LampPost1/LampPost2 items (owner repro 4444 1171 0). Fountain: 4464..4480 x
        // 1172..1185, the decoration statics and LampPost1 there, and StoneFountainAddon's components (owner repro 4471 1178 0).
        private const string ZigZag = @"
4462 1160 0 015F; 4462 1160 20 0162; 4462 1161 0 0162; 4462 1161 20 0162; 4463 1160 0 0B3F; 4462 1162 0 015F; 4462 1162 20 0162; 4462 1163 0 015F
4462 1163 20 0162; 4462 1164 0 0162; 4462 1164 20 0162; 4463 1163 0 0B3F; 4462 1165 0 015F; 4462 1165 20 0162; 4463 1164 0 0B3D; 4462 1166 0 015F
4462 1166 20 0162; 4463 1165 0 0B3F; 4468 1160 0 0B86; 4462 1167 0 015F; 4462 1167 20 0162; 4468 1161 0 0B86; 4469 1160 0 0B86; 4463 1167 0 0160
4463 1167 20 0163; 4468 1162 0 0B85; 4469 1161 0 0B86; 4470 1160 0 0B86; 4464 1167 0 0160; 4464 1167 20 0163; 4469 1162 0 0B86; 4470 1161 0 0B86
4465 1167 0 0163; 4465 1167 20 0163; 4470 1162 0 0B86; 4466 1167 0 0160; 4466 1167 20 0163; 4467 1167 0 0160; 4467 1167 20 0163; 4467 1168 0 015F
4467 1168 20 0162; 4467 1169 0 015F; 4467 1169 20 0162; 4468 1168 0 0B10; 4468 1168 3 0AA3; 4467 1170 0 0162; 4467 1170 20 0162; 4468 1169 0 0B16
4468 1169 3 0AA1; 4469 1168 0 0B17; 4469 1168 3 0AA2; 4467 1171 0 015F; 4467 1171 20 0162; 4468 1170 0 0B16; 4468 1170 3 0AA1; 4469 1169 0 0B0F
4469 1169 3 104B; 4470 1168 0 0B13; 4470 1168 3 0AA4; 4467 1172 0 015F; 4467 1172 20 0162; 4468 1171 0 0B16; 4468 1171 3 0AA1; 4469 1170 0 0B0F
4469 1170 3 1057; 4469 1170 3 104D; 4470 1169 0 0B14; 4470 1169 3 0A9F; 4467 1173 0 0162; 4467 1173 20 0162; 4468 1172 0 0B16; 4468 1172 3 0AA1
4469 1171 0 0B0F; 4469 1171 3 1051; 4469 1171 3 104D; 4470 1170 0 0B14; 4470 1170 3 0A9F; 4467 1174 0 015F; 4467 1174 20 0162; 4468 1173 0 0B12
4468 1173 3 0AA5; 4469 1172 0 0B0F; 4469 1172 3 104B; 4470 1171 0 0B14; 4470 1171 3 0A9F; 4467 1175 0 015F; 4467 1175 20 0162; 4469 1173 0 0B15
4469 1173 3 0AA0; 4470 1172 0 0B14; 4470 1172 3 0A9F; 4468 1175 0 0160; 4468 1175 20 0163; 4470 1173 0 0B11; 4470 1173 3 0B18; 4469 1175 0 0160
4469 1175 20 0163; 4469 1176 0 0B9E; 4470 1175 0 0170; 4470 1175 20 0163
";

        private const string Lamps = @"
4436 1160 20 05C7; 4436 1161 23 05D0; 4437 1160 23 05C4; 4436 1162 26 05D0; 4437 1161 23 05C7; 4438 1160 26 05C4; 4436 1163 20 00C1; 4436 1163 29 05D0
4437 1162 26 05D0; 4438 1161 26 05C4; 4439 1160 29 05C4; 4436 1164 32 05CE; 4437 1163 20 00C1; 4437 1163 23 00C1; 4437 1163 29 05D0; 4438 1162 26 05C7
4439 1161 29 05C4; 4440 1160 32 05C2; 4436 1165 29 05CF; 4437 1164 32 05CE; 4438 1163 20 00C1; 4438 1163 23 00C1; 4438 1163 26 00C1; 4438 1163 29 05D0
4439 1162 29 05C4; 4440 1161 32 05C2; 4441 1160 29 05C3; 4436 1166 26 05CF; 4437 1165 29 05CF; 4438 1164 32 05CE; 4439 1163 20 00C1; 4439 1163 23 00C1
4439 1163 26 00C1; 4439 1163 29 00C1; 4439 1163 29 05C7; 4440 1162 32 05C2; 4441 1161 29 05C3; 4442 1160 26 05C3; 4436 1167 0 00A8; 4436 1167 23 05CF
4437 1166 26 05CF; 4438 1165 29 05CF; 4439 1164 32 05CE; 4440 1163 20 00C1; 4440 1163 23 00C1; 4440 1163 26 00C1; 4440 1163 32 05C2; 4441 1162 29 05C3
4442 1161 26 05C3; 4443 1160 0 0B10; 4443 1160 3 0AA3; 4443 1160 23 05C3; 4436 1168 0 0B9C; 4436 1168 20 05CF; 4437 1167 0 00BA; 4437 1167 23 05CF
4438 1166 26 05CF; 4439 1165 29 05CF; 4440 1164 32 05CA; 4441 1163 20 00C1; 4441 1163 23 00C1; 4441 1163 29 05C6; 4442 1162 26 05C6; 4443 1161 0 0B16
4443 1161 3 0AA1; 4443 1161 23 05C6; 4444 1160 0 0B17; 4444 1160 3 0AA2; 4444 1160 20 05C6; 4437 1168 20 05CF; 4438 1167 0 00AD; 4438 1167 23 05CF
4439 1166 26 05CF; 4440 1165 29 05CF; 4441 1164 32 05CE; 4442 1163 20 00C1; 4442 1163 29 05D0; 4443 1162 0 0B16; 4443 1162 3 0AA1; 4443 1162 26 05D0
4444 1161 0 0B0F; 4444 1161 3 13EF; 4444 1161 23 05D0; 4445 1160 0 0B13; 4445 1160 3 0AA4; 4445 1160 20 05D0; 4438 1168 20 05CF; 4439 1167 23 05CF
4440 1166 26 05CF; 4441 1165 29 05CF; 4442 1164 32 05CE; 4443 1163 0 0B16; 4443 1163 3 0AA1; 4443 1163 29 05D0; 4444 1162 0 0B0F; 4444 1162 3 13EC
4444 1162 26 05D0; 4445 1161 0 0B14; 4445 1161 3 0A9F; 4445 1161 23 05D0; 4446 1160 20 05D0; 4439 1168 20 05CF; 4440 1167 23 05CF; 4441 1166 26 05CF
4442 1165 29 05CF; 4443 1164 0 0B16; 4443 1164 3 0AA1; 4443 1164 32 05CE; 4444 1163 0 0B0F; 4444 1163 3 13F0; 4444 1163 29 05D0; 4445 1162 0 0B14
4445 1162 3 0A9F; 4445 1162 26 05D0; 4446 1161 23 05D0; 4447 1160 0 00A7; 4447 1160 20 05D0; 4447 1160 20 00C2; 4440 1168 20 05CF; 4441 1167 0 00AB
4441 1167 23 05CF; 4442 1166 26 05CF; 4443 1165 0 0B12; 4443 1165 3 0AA5; 4443 1165 29 05CF; 4444 1164 0 0B0F; 4444 1164 3 13EB; 4444 1164 32 05CE
4445 1163 0 0B14; 4445 1163 3 0A9F; 4445 1163 29 05D0; 4446 1162 26 05D0; 4447 1161 0 00A7; 4447 1161 20 00C2; 4447 1161 23 00C2; 4447 1161 23 05D0
4448 1160 20 05D0; 4441 1168 20 05CF; 4442 1167 0 00BA; 4442 1167 23 05CF; 4443 1166 26 05CF; 4444 1165 0 0B15; 4444 1165 3 0AA0; 4444 1165 29 05CF
4445 1164 0 0B14; 4445 1164 3 0A9F; 4445 1164 32 05CE; 4446 1163 29 05D0; 4447 1162 0 00B9; 4447 1162 20 00C2; 4447 1162 23 00C2; 4447 1162 26 05D0
4447 1162 26 00C2; 4448 1161 23 05D0; 4442 1168 20 05CF; 4443 1167 0 00A8; 4443 1167 23 05CF; 4444 1166 26 05CF; 4445 1165 0 0B11; 4445 1165 3 0B18
4445 1165 29 05CF; 4446 1164 32 05CE; 4447 1163 0 00A7; 4447 1163 20 00C2; 4447 1163 23 00C2; 4447 1163 26 00C2; 4447 1163 29 05D0; 4447 1163 29 00C2
4448 1162 26 05D0; 4436 1175 0 084B; 4443 1168 20 05CF; 4444 1167 0 00A8; 4444 1167 23 05CF; 4445 1166 26 05CF; 4446 1165 29 05CF; 4447 1164 0 00A7
4447 1164 20 00C2; 4447 1164 23 00C2; 4447 1164 26 00C2; 4447 1164 32 05CE; 4448 1163 29 05D0; 4437 1175 0 084B; 4444 1168 20 05CF; 4445 1167 0 00BA
4445 1167 23 05CF; 4446 1166 26 05CF; 4447 1165 0 00B9; 4447 1165 20 00C2; 4447 1165 23 00C2; 4447 1165 29 05CF; 4448 1164 32 05CE; 4438 1175 0 084B
4445 1168 20 05CF; 4446 1167 0 00A8; 4446 1167 23 05CF; 4447 1166 0 00A7; 4447 1166 20 00C2; 4447 1166 26 05CF; 4448 1165 29 05CF; 4439 1175 0 084B
4446 1168 20 05CF; 4447 1167 0 00A6; 4447 1167 23 05CF; 4448 1166 26 05CF; 4440 1175 0 084B; 4447 1168 20 05CF; 4448 1167 23 05CF; 4448 1168 20 05CF
4444 1175 0 084B; 4445 1175 0 084B; 4446 1175 0 084B; 4447 1175 0 084B; 4448 1175 0 084B; 4449 1175 0 084B; 4447 1178 0 177B; 4450 1175 0 084B
4451 1175 0 084B; 4452 1175 0 084B; 4444 1168 0 0B20; 4439 1174 0 0B22; 4445 1174 0 0B22
";

        private const string Fountain = @"
4467 1172 0 015F; 4467 1172 20 0162; 4467 1173 0 0162; 4467 1173 20 0162; 4468 1172 0 0B16; 4468 1172 3 0AA1; 4467 1174 0 015F; 4467 1174 20 0162
4468 1173 0 0B12; 4468 1173 3 0AA5; 4469 1172 0 0B0F; 4469 1172 3 104B; 4467 1175 0 015F; 4467 1175 20 0162; 4469 1173 0 0B15; 4469 1173 3 0AA0
4470 1172 0 0B14; 4470 1172 3 0A9F; 4468 1175 0 0160; 4468 1175 20 0163; 4470 1173 0 0B11; 4470 1173 3 0B18; 4469 1175 0 0160; 4469 1175 20 0163
4472 1172 0 0B16; 4472 1172 3 0AA1; 4469 1176 0 0B9E; 4470 1175 0 0170; 4470 1175 20 0163; 4472 1173 0 0B12; 4472 1173 3 0AA5; 4473 1172 0 0B0F
4473 1172 3 105D; 4471 1175 20 0163; 4473 1173 0 0B15; 4473 1173 3 0AA0; 4474 1172 0 0B14; 4464 1183 0 084B; 4472 1175 20 0163; 4474 1173 0 0B11
4474 1173 3 0B18; 4475 1172 0 015F; 4475 1172 20 0162; 4465 1183 0 084B; 4473 1175 0 016D; 4473 1175 20 0163; 4475 1173 0 0162; 4475 1173 20 0162
4466 1183 0 084B; 4474 1175 0 0160; 4474 1175 20 0163; 4475 1174 0 015F; 4475 1174 20 0162; 4467 1183 0 084B; 4475 1175 0 015E; 4475 1175 20 0163
4475 1175 20 0162; 4468 1183 0 084B; 4469 1183 0 084B; 4470 1183 0 084B; 4471 1183 0 084B; 4472 1183 0 084B; 4473 1183 0 084B; 4474 1183 0 084B
4475 1183 0 084B; 4476 1183 0 084B; 4477 1183 0 084B; 4478 1183 0 084B; 4479 1183 0 084B; 4480 1183 0 084B; 4464 1182 -14 0080; 4465 1182 -14 0080
4466 1180 -14 0080; 4466 1181 -14 0080; 4466 1182 -14 0080; 4467 1180 -14 0080; 4467 1181 -14 0080; 4467 1182 -14 0080; 4468 1181 -14 0080; 4468 1182 -14 0080
4469 1182 -14 0080; 4470 1182 -14 0080; 4471 1182 -14 0080; 4472 1182 -14 0080; 4473 1181 -14 0080; 4473 1182 -14 0080; 4474 1182 -14 0080; 4475 1180 -14 0080
4475 1181 -14 0080; 4475 1182 -14 0080; 4476 1182 -14 0080; 4477 1182 -14 0080; 4478 1180 -14 0080; 4478 1181 -14 0080; 4478 1182 -14 0080; 4479 1180 -14 0080
4479 1181 -14 0080; 4479 1182 -14 0080; 4480 1182 -14 0080; 4464 1179 0 084B; 4465 1179 0 084B; 4466 1179 0 084B; 4467 1179 0 084B; 4468 1179 0 084B
4469 1179 0 084B; 4470 1179 0 084B; 4471 1179 0 084B; 4472 1179 0 084B; 4473 1179 0 084B; 4474 1179 0 084B; 4475 1179 0 084B; 4476 1179 0 084B
4477 1179 0 084B; 4478 1179 0 084B; 4479 1179 0 084B; 4480 1179 0 084B; 4471 1180 0 0B20; 4465 1180 8 0C83; 4465 1181 8 0C83; 4465 1182 8 0C83
4469 1180 8 0C83; 4469 1181 8 0C83; 4469 1182 8 0C83; 4473 1180 8 0C83; 4473 1181 8 0C83; 4473 1182 8 0C83; 4477 1180 8 0C83; 4477 1181 8 0C83
4477 1182 8 0C83; 4464 1180 8 0C86; 4464 1181 8 0C86; 4464 1182 8 0C86; 4468 1180 8 0C86; 4468 1181 8 0C86; 4468 1182 8 0C86; 4476 1180 8 0C86
4476 1181 8 0C86; 4476 1182 8 0C86; 4480 1180 8 0C86; 4480 1181 8 0C86; 4480 1182 8 0C86; 4467 1180 8 0C88; 4467 1181 8 0C88; 4467 1182 8 0C88
4475 1180 8 0C88; 4475 1181 8 0C88; 4475 1182 8 0C88; 4479 1180 8 0C88; 4479 1181 8 0C88; 4479 1182 8 0C88; 4466 1180 8 0CC1; 4466 1181 8 0CC1
4466 1182 8 0CC1; 4474 1180 0 0CC1; 4474 1182 8 0CC1; 4478 1180 8 0CC1; 4478 1181 8 0CC1; 4478 1182 8 0CC1; 4470 1183 11 1731; 4471 1183 11 1732
4472 1183 11 1733; 4473 1183 11 1734; 4473 1182 11 1735; 4473 1181 11 1736; 4473 1180 11 1737; 4472 1180 11 1738; 4472 1181 11 1739; 4472 1182 11 173A
4471 1182 11 173B; 4470 1182 11 173C; 4470 1181 11 173D; 4471 1181 11 173E; 4471 1180 11 173F; 4470 1180 11 1741
";


        private static byte Facing(PackArt pack, Obj o)
        {
            var a = pack.Art(o.Graphic);

            return pack.Statics[o.Graphic].IsWall ? LightOcclusion.Facing(a.W, a.BX, a.BW) : LightOcclusion.FacingNone;
        }

        // the same wall line, by the pieces' facing from their art (LightOcclusion.Facing), not by distance: an east-facing
        // line keeps X, a south-facing one keeps Y
        private static bool SameWallLine(PackArt pack, Obj window, Obj other)
        {
            byte f = Facing(pack, window);

            return f != LightOcclusion.FacingNone && Facing(pack, other) == f && (f == LightOcclusion.FacingEast ? other.X == window.X : other.Y == window.Y);
        }

        private void AssertWindowsKeepTheirWallLine(PackArt pack, List<Obj> scene, string name)
        {
            foreach (Obj window in scene.Where(o => o.Graphic is 0x0162 or 0x0163))
            {
                var hidden = Occluders(pack, window, scene);
                _output.WriteLine($"{name} window {window}: round 3 masked {Round3Count(pack, window, scene, 10)}, now {hidden.Count}: {string.Join(", ", hidden)}");
                Assert.DoesNotContain(hidden, o => SameWallLine(pack, window, o));
            }
        }

        [TileDataPackFact]
        public void Wall_facing_from_the_art_matches_the_wall_lines()
        {
            PackArt pack = PackArt.Get();

            // lines along Y (X fixed) face east, lines along X face south: the bank's sandstone and the house's wooden walls
            foreach (ushort g in new ushort[] { 0x015F, 0x0162, 0x00A7, 0x00B9 })
            {
                Assert.Equal(LightOcclusion.FacingEast, Facing(pack, new Obj(0, 0, 0, g)));
            }

            foreach (ushort g in new ushort[] { 0x0160, 0x0163, 0x00A8, 0x00BA })
            {
                Assert.Equal(LightOcclusion.FacingSouth, Facing(pack, new Obj(0, 0, 0, g)));
            }

            // a lamp post is no wall; the iron fence's art is 66 px wide (no single tile edge): neither faces anywhere
            Assert.Equal(LightOcclusion.FacingNone, Facing(pack, new Obj(0, 0, 0, 0x0B20)));
            Assert.Equal(LightOcclusion.FacingNone, Facing(pack, new Obj(0, 0, 0, 0x084B)));
        }

        [TileDataPackFact]
        public void Inside_corner_window_glow_is_cut_by_the_perpendicular_wall_in_front()
        {
            // owner 2026-11-22 (the keep's zig-zag wall, screenshot 232455-noted): at each inside corner the glow of the window
            // next to the corner spilled onto the perpendicular wall line in front of it, which does not cover the window. That
            // line's visible face turns away from the window, so it hides the glow; the pieces of the window's own line do not
            PackArt pack = PackArt.Get();
            var zigzag = Parse(ZigZag).ToList();
            var hall = Parse(BankHall).ToList();
            var lamps = Parse(Lamps).ToList();
            var fountain = Parse(Fountain).ToList();

            var westCorner = Occluders(pack, new Obj(4462, 1166, 20, 0x0162), zigzag);
            Assert.Contains(new Obj(4463, 1167, 0, 0x0160), westCorner);
            Assert.Contains(new Obj(4463, 1167, 20, 0x0163), westCorner);
            Assert.DoesNotContain(new Obj(4462, 1167, 20, 0x0162), westCorner);

            var southCorner = Occluders(pack, new Obj(4467, 1174, 20, 0x0162), zigzag);
            Assert.Contains(new Obj(4468, 1175, 0, 0x0160), southCorner);
            Assert.Contains(new Obj(4468, 1175, 20, 0x0163), southCorner);
            Assert.DoesNotContain(new Obj(4467, 1175, 20, 0x0162), southCorner);

            Assert.Contains(new Obj(4468, 1155, 0, 0x0160), Occluders(pack, new Obj(4467, 1155, 20, 0x0162), hall));
            Assert.Contains(new Obj(4446, 1167, 0, 0x00A8), Occluders(pack, new Obj(4447, 1165, 0, 0x00B9), lamps));
            Assert.Contains(new Obj(4468, 1175, 0, 0x0160), Occluders(pack, new Obj(4467, 1175, 20, 0x0162), fountain));
            // the switch off: nothing hides
            Assert.Empty(Occluders(pack, new Obj(4462, 1166, 20, 0x0162), zigzag, on: false));
        }

        [TileDataPackFact]
        public void Bank_window_halo_is_not_masked_by_the_pieces_of_its_own_wall_line()
        {
            // owner 2026-11-22: the lit windows had no halo on their walls. The stacked window above, the wall pieces and
            // windows beside it on its line and the counter in front of the west wall are drawn after it but lit by it
            PackArt pack = PackArt.Get();
            var scene = Parse(BankHall).ToList();
            AssertWindowsKeepTheirWallLine(pack, scene, "bank");

            foreach (Obj window in scene.Where(o => o.Graphic == 0x0162 && o.X == 4462))
            {
                Assert.True(Round3Count(pack, window, scene, 10) > 0);
                Assert.Empty(Occluders(pack, window, scene));
            }
        }

        [TileDataPackFact]
        public void Bank_lit_window_behind_the_nearer_wall_line_is_hidden_by_it()
        {
            // owner's first repro (Felucca 4462 1169 0, light 05): where the bank's outer wall zig-zags, a lit window's glow
            // was drawn over the wall line in front of it. Those nearer pieces cover the window, so they keep hiding its glow
            PackArt pack = PackArt.Get();
            var zigzag = Parse(ZigZag).ToList();
            var hall = Parse(BankHall).ToList();
            AssertWindowsKeepTheirWallLine(pack, zigzag, "zigzag");

            Assert.Contains(new Obj(4463, 1167, 20, 0x0163), Occluders(pack, new Obj(4462, 1167, 20, 0x0162), zigzag));
            Assert.Contains(new Obj(4467, 1169, 20, 0x0162), Occluders(pack, new Obj(4465, 1167, 0, 0x0163), zigzag));
            Assert.Contains(new Obj(4468, 1175, 20, 0x0163), Occluders(pack, new Obj(4467, 1175, 20, 0x0162), zigzag));
            Assert.Contains(new Obj(4468, 1155, 20, 0x0163), Occluders(pack, new Obj(4467, 1155, 20, 0x0162), hall));
            // the switch off: nothing hides
            Assert.Empty(Occluders(pack, new Obj(4462, 1167, 20, 0x0162), zigzag, on: false));
        }

        [TileDataPackFact]
        public void Moonglow_street_lamps_light_the_fences_walls_and_roofs_around_them()
        {
            // owner 2026-11-22 (4444 1171 0): the lamps' halos were cut into wedges along the iron fences and the lamp by the
            // house had almost no halo (house wall and roof drawn after it). None of them stands in front of a lamp post
            PackArt pack = PackArt.Get();
            var scene = Parse(Lamps).ToList();

            foreach (Obj lamp in scene.Where(o => o.Graphic is 0x0B20 or 0x0B22))
            {
                var hidden = Occluders(pack, lamp, scene);
                _output.WriteLine($"lamp {lamp}: round 3 masked {Round3Count(pack, lamp, scene, 10)}, now {hidden.Count}: {string.Join(", ", hidden)}");
                Assert.True(Round3Count(pack, lamp, scene, 10) > 0);
                Assert.Empty(hidden);
            }

            // the house's lit windows: not masked by their own wall line
            foreach (Obj window in scene.Where(o => o.Graphic == 0x00BA))
            {
                Assert.DoesNotContain(Occluders(pack, window, scene), o => o.Y == window.Y);
            }
        }

        [TileDataPackFact]
        public void Moonglow_fountain_lamp_lights_the_fountain_planters_and_fences()
        {
            // owner 2026-11-22 (4471 1178 0): the lamp post by the fountain (ModernUO decoration: LampPost1 4471 1180 0, a
            // StoneFountainAddon centred on 4472 1182 11, planters and flowers, iron fences) and the bank's south wall windows
            PackArt pack = PackArt.Get();
            var scene = Parse(Fountain).ToList();
            Obj lamp = new Obj(4471, 1180, 0, 0x0B20);
            var hidden = Occluders(pack, lamp, scene);
            _output.WriteLine($"fountain lamp: round 3 masked {Round3Count(pack, lamp, scene, 10)}, now {hidden.Count}: {string.Join(", ", hidden)}");
            Assert.True(Round3Count(pack, lamp, scene, 10) > 10);
            // the fountain's corner piece shares the lamp's tile (z 11): drawn over the post but at its depth, it stands with
            // the lamp (bench-001032 showed it as a dark wedge on the lit fountain)
            Assert.Empty(hidden);
            AssertWindowsKeepTheirWallLine(pack, scene, "fountain");
        }

        // Moonglow, Felucca 4466..4484 x 1084..1100 (owner repro 2026-11-22 at 4470 1091 0): the house north-east of the lamp
        // post 0x0B22 at 4474 1091 0 (tiledata height 15), its walls, lit windows (0x00B9 / 0x00BA) and roof eave at z 20..32
        private const string Eave = @"
4471 1084 0 00A7; 4471 1085 0 00B9; 4471 1085 0 0C84; 4472 1084 32 05CE; 4471 1086 0 00A7; 4472 1085 29 05CF; 4473 1084 32 05CE; 4471 1087 0 00A7
4471 1087 0 0C84; 4472 1086 26 05CF; 4473 1085 29 05CF; 4474 1084 32 05CE; 4471 1088 0 0C84; 4472 1087 0 00A8; 4472 1087 23 05CF; 4473 1086 26 05CF
4474 1085 29 05CF; 4475 1084 32 05CE; 4472 1088 0 0C84; 4472 1088 20 05CF; 4473 1087 0 00BA; 4473 1087 23 05CF; 4474 1086 26 05CF; 4475 1085 29 05CF
4476 1084 0 0B3F; 4476 1084 32 05CE; 4473 1088 0 0C84; 4473 1088 20 05CF; 4474 1087 0 00A8; 4474 1087 23 05CF; 4475 1086 26 05CF; 4476 1085 29 05CF
4477 1084 32 05CE; 4474 1088 0 0C84; 4474 1088 20 05CF; 4475 1087 0 00A8; 4475 1087 20 0B3F; 4475 1087 23 05CF; 4476 1086 26 05CF; 4477 1085 29 05CF
4478 1084 32 05CE; 4475 1088 0 00A7; 4475 1088 20 05CF; 4476 1087 23 05CF; 4477 1086 26 05CF; 4478 1085 29 05CF; 4479 1084 32 05CE; 4475 1089 0 00A7
4475 1089 0 0C84; 4476 1088 20 05C5; 4477 1087 23 05C5; 4478 1086 26 05C5; 4479 1085 29 05C5; 4480 1084 32 05CB; 4474 1091 0 0B22; 4475 1090 0 00B9
4475 1090 0 0C84; 4476 1089 20 05C4; 4477 1088 23 05C4; 4478 1087 26 05C4; 4479 1086 29 05C4; 4480 1085 32 05C2; 4481 1084 32 05CE; 4475 1091 0 00A7
4475 1091 0 0C84; 4476 1090 20 05C4; 4477 1089 23 05C4; 4478 1088 26 05C4; 4479 1087 29 05C4; 4480 1086 32 05C2; 4481 1085 29 05C8; 4482 1084 32 05CE
4476 1091 0 00A8; 4476 1091 20 05C4; 4476 1091 20 00C1; 4477 1090 23 05C4; 4478 1089 26 05C4; 4479 1088 29 05C4; 4480 1087 32 05C2; 4481 1086 29 05C3
4482 1085 29 05CF; 4483 1084 32 05CE; 4476 1092 20 05C4; 4477 1091 0 00BA; 4477 1091 20 00C1; 4477 1091 23 05C4; 4477 1091 23 00C1; 4478 1090 26 05C4
4479 1089 29 05C4; 4480 1088 32 05C2; 4481 1087 29 05C3; 4482 1086 26 05C8; 4483 1085 29 05CF; 4484 1084 0 0B16; 4484 1084 3 0AA1; 4484 1084 32 05CE
4477 1092 23 05C4; 4478 1091 0 00AD; 4478 1091 20 00C1; 4478 1091 23 00C1; 4478 1091 26 05C4; 4478 1091 26 00C1; 4479 1090 29 05C4; 4480 1089 32 05C2
4481 1088 29 05C3; 4482 1087 26 05C3; 4483 1086 26 05CF; 4484 1085 0 0B12; 4484 1085 3 0AA5; 4484 1085 29 05CF; 4478 1092 0 0B9A; 4478 1092 26 05C4
4479 1091 20 00C1; 4479 1091 23 00C1; 4479 1091 26 00C1; 4479 1091 29 05C4; 4479 1091 29 00C1; 4480 1090 32 05C2; 4481 1089 29 05C3; 4482 1088 26 05C3
4483 1087 0 00A9; 4483 1087 23 05C8; 4484 1086 26 05CF; 4479 1092 29 05C4; 4480 1091 20 00C1; 4480 1091 23 00C1; 4480 1091 26 00C1; 4480 1091 32 05C2
4481 1090 29 05C3; 4482 1089 26 05C3; 4483 1088 0 00A7; 4483 1088 23 05C3; 4484 1087 0 00A8; 4484 1087 23 05CF; 4480 1092 32 05C2; 4481 1091 0 00AB
4481 1091 20 00C1; 4481 1091 23 00C1; 4481 1091 29 05C3; 4482 1090 26 05C3; 4483 1089 0 00A7; 4483 1089 23 05C3; 4484 1088 0 0C84; 4484 1088 20 05C8
4474 1099 0 0CB0; 4481 1092 29 05C3; 4482 1091 0 00BA; 4482 1091 20 00C1; 4482 1091 26 05C3; 4483 1090 0 00B9; 4483 1090 23 05C3; 4484 1089 0 0C84
4484 1089 20 05C3; 4482 1092 26 05C3; 4483 1091 0 00A6; 4483 1091 23 05C3; 4484 1090 0 0C84; 4484 1090 20 05C3; 4475 1100 0 0CB0; 4483 1092 23 05C3
4484 1091 0 0C84; 4484 1091 20 05C3; 4478 1098 0 0D11; 4484 1092 20 05C3; 4477 1100 0 0C84; 4478 1099 0 0CB4; 4478 1100 0 0CE9; 4480 1098 0 0CD6
4480 1098 0 0CD7; 4481 1098 0 0CAF; 4482 1098 0 0CB6; 4484 1097 0 0C9E; 4484 1098 0 0CE6; 4484 1098 0 0CE7
";

        [TileDataPackFact]
        public void Moonglow_lamp_post_lights_the_roof_eave_above_it()
        {
            // round 6a (owner 2026-11-22): the eave tile 4476 1092 z 20 (and 4477 1092 z 23 next to it) covers the lamp's
            // light point, so its whole sprite stayed dark while the eave tiles around it were lit. Its bottom is above the
            // lamp's top (z 0 + 15): the light shines up onto it, it does not hide it
            PackArt pack = PackArt.Get();
            var scene = Parse(Eave).ToList();
            Obj lamp = new Obj(4474, 1091, 0, 0x0B22);
            Assert.Contains(lamp, scene);
            int top = LampTop(pack, lamp);
            var all = Occluders(pack, lamp, scene);
            _output.WriteLine($"lamp {lamp}: tiledata height {pack.Statics[lamp.Graphic].Height}, top {top}; occluders {all.Count}: {string.Join(", ", all)}");
            Assert.Equal(15, top);
            // round 5 (no source top): the eave tile hid the glow
            Assert.Equal(new[] { new Obj(4476, 1092, 20, 0x05C4) }, Occluders(pack, lamp, scene, srcHeight: int.MaxValue));
            Assert.Empty(all);

            // the house's lit windows: the eave above them never hides them; only wall pieces do, the gable's upper wall pieces
            // (0x00C1, z 20..29) in front of the east-facing window 0x00B9 at 4475 1090 by facing away from it
            foreach (Obj window in scene.Where(o => o.Graphic == 0x00B9 || o.Graphic == 0x00BA))
            {
                var hidden = Occluders(pack, window, scene);
                _output.WriteLine($"window {window}: {hidden.Count}: {string.Join(", ", hidden)}");
                Assert.All(hidden, o => Assert.True(pack.Statics[o.Graphic].IsWall));
            }

            Assert.Contains(new Obj(4479, 1091, 29, 0x00C1), Occluders(pack, new Obj(4475, 1090, 0, 0x00B9), scene));
        }

        // GameScene.AddLightCore's old whole-light rejection (kept with the switch off): an object with art, not transparent
        // by tiledata, on tile (x + 1, y + 1) at least 5 Z above the source drops the light
        private static bool OldNeighbourRejects(PackArt pack, Obj src, IEnumerable<Obj> scene) =>
            scene.Any(o => o.X == src.X + 1 && o.Y == src.Y + 1 && o.Z >= src.Z + 5 && !pack.Statics[o.Graphic].IsTransparent &&
                pack.Art(o.Graphic).W != 0);

        [TileDataPackFact]
        public void Neighbour_rejection_drops_lights_the_cover_test_keeps()
        {
            // review P1 #3, kept for now (round 5): AddLightCore drops a whole light when a candle (0x0B1A) stands on tile
            // x + 1, y + 1 of a lamp post 10 Z up, although the candle does not cover the post's light point; a wall piece
            // there, 20 Z up, both drops it and covers it. The rejection stays under light_occlusion until the occluder rule
            // hides glow over intervening faces (without it the lit windows under the Moonglow house's eave glowed over
            // the roof, bench-001035): this records where the two rules part
            PackArt pack = PackArt.Get();
            Obj lamp = new Obj(800, 800, 0, 0x0B20);
            var candle = new List<Obj> { lamp, new Obj(801, 801, 10, 0x0B1A) };
            Assert.True(OldNeighbourRejects(pack, lamp, candle));
            Assert.Empty(Occluders(pack, lamp, candle));

            var wall = new List<Obj> { lamp, new Obj(801, 801, 20, 0x0162) };
            Assert.True(OldNeighbourRejects(pack, lamp, wall));
            Assert.Contains(new Obj(801, 801, 20, 0x0162), Occluders(pack, lamp, wall, srcHeight: int.MaxValue));
            // round 6a: that wall piece stands above the post's top, the light shines up onto it
            Assert.DoesNotContain(new Obj(801, 801, 20, 0x0162), Occluders(pack, lamp, wall));
        }

        [TileDataPackFact]
        public void Oversized_static_covering_the_light_is_found_by_the_chunk_search()
        {
            // review P2 #5: 0x0D42 is 44 x 421 (1,786 statics exceed the round 4 window's 300 x 256). Placed where its art covers
            // a lamp post's light point from far down the diagonals, it hides the glow (taken without round 6a's rule: at
            // z 25 it stands above the post's top) and the chunk search reaches it
            PackArt pack = PackArt.Get();
            Obj lamp = new Obj(800, 800, 0, 0x0B20);
            var a = pack.Art(0x0D42);
            Assert.True(a.H > 256);
            var (sax, say) = Anchor(lamp);
            var la = pack.Art(lamp.Graphic);
            LightOcclusion.LightPoint(sax, say, la.W, la.H, la.BX, la.BY, la.BW, la.BH, out int px, out int py);
            Obj found = default;
            bool any = false;

            for (int d = 9; d <= 16 && !any; d++)
            {
                for (int z = 0; z <= 60 && !any; z += 5)
                {
                    Obj o = new Obj(800 + d, 800 + d, z, 0x0D42);
                    var (ax, ay) = Anchor(o);

                    if (LightOcclusion.CoverPoint(ax, ay, a.W, a.H, px, py, out int lx, out int ly) && pack.Pixel(o.Graphic, lx, ly))
                    {
                        found = o;
                        any = true;
                    }
                }
            }

            Assert.True(any, "no placement of 0x0D42 covers the lamp's point");
            _output.WriteLine($"0x0D42 covering the lamp: {found} (S = {found.X - lamp.X + found.Y - lamp.Y}, round 4 window ended at S 17)");
            Assert.Contains(found, Occluders(pack, lamp, new[] { lamp, found }, srcHeight: int.MaxValue));
            // Anchor() here is ViewMargin.Anchor without the draw offset
            Assert.True(LightOcclusion.ChunkReaches(found.X >> 3, found.Y >> 3, (a.W >> 1) - 22, a.W - (a.W >> 1) - 22, a.H - 44, found.Z, found.Z,
                0, 0, px, py, px + 1, py + 1));
        }

        // the pack's map statics of facet f in x0..x1, y0..y1 (all Z)
        private static List<Obj> MapStatics(int facet, int x0, int y0, int x1, int y1)
        {
            string dir = Path.Combine(TileDrawOracleTests.PackDir(), "map", facet.ToString());
            var regions = new Dictionary<(int, int), (RegionHeader Header, BlockData[] Blocks)>();
            var list = new List<Obj>();

            for (int bx = x0 >> 3; bx <= x1 >> 3; bx++)
            {
                for (int by = y0 >> 3; by <= y1 >> 3; by++)
                {
                    var key = (bx / 64, by / 64);

                    if (!regions.TryGetValue(key, out var region))
                    {
                        regions[key] = region = RegionFile.Read(Path.Combine(dir, $"r_{key.Item1}_{key.Item2}.bin"));
                    }

                    BlockData block = region.Blocks[(by % 64) * region.Header.BlocksW + bx % 64];

                    if (block.IsEmpty)
                    {
                        continue;
                    }

                    foreach (StaticCell s in block.Statics)
                    {
                        int x = (bx << 3) + s.X, y = (by << 3) + s.Y;

                        if (x >= x0 && x <= x1 && y >= y0 && y <= y1)
                        {
                            list.Add(new Obj(x, y, s.Z, s.Graphic));
                        }
                    }
                }
            }

            return list;
        }

        // light_occlusion_surface_only: src's light is a surface glow (LightAlphaFade.IsSurface: its shape's class in table,
        // the pack's lightclass.bin, else the emitter's tiledata Wall/Window flags; GameScene.SurfaceGlow)
        private static bool Surface(PackArt pack, Obj src, byte[] table)
        {
            ref StaticTiles d = ref pack.Statics[src.Graphic];
            LightClass cls = d.Layer < table.Length ? (LightClass)table[d.Layer] : LightClass.Unlisted;

            return LightAlphaFade.IsSurface(cls, d.IsWall || d.IsWindow);
        }

        // what the light pass masks src's glow with (GameScene.CollectLightOccluders): its occluders when LightOcclusion.Masks
        // lets the light have any
        private static List<Obj> Masked(PackArt pack, Obj src, IEnumerable<Obj> scene, bool surfaceOnly, byte[] table) =>
            LightOcclusion.Masks(surfaceOnly, Surface(pack, src, table)) ? Occluders(pack, src, scene) : new List<Obj>();

        public static TheoryData<bool, bool> SurfaceOnlyArms => new TheoryData<bool, bool>
        {
            { false, false }, { false, true }, { true, false }, { true, true }
        };

        [TileDataPackTheory]
        [MemberData(nameof(SurfaceOnlyArms))]
        public void Luna_candelabra_lights_the_wall_in_front_of_it_with_surface_only(bool surfaceOnly, bool withTable)
        {
            // round 7 (owner 2026-11-22, Luna Malas 1005 513 -30): the candelabra 0x0B26 at 1003 511 -30 stands against the
            // sandstone wall line x 1004; the wall pieces in front cover its light point, so with light_occlusion their own
            // pixels were masked dark inside its halo. The light map multiplies the frame: a radial light lights what stands
            // in front of it. Surface only: no occluders for it; off: masked as before
            PackArt pack = PackArt.Get();
            byte[] table = withTable ? LightClassFile.Seed() : Array.Empty<byte>();
            var scene = MapStatics(3, 998, 506, 1010, 518);
            Obj candelabra = new Obj(1003, 511, -30, 0x0B26);
            Assert.Contains(candelabra, scene);
            Assert.False(Surface(pack, candelabra, table));
            var all = Occluders(pack, candelabra, scene);
            _output.WriteLine($"candelabra {candelabra}: occluders {all.Count}: {string.Join(", ", all)}");
            Assert.Contains(all, o => o.X == 1004 && pack.Statics[o.Graphic].IsWall);

            var masked = Masked(pack, candelabra, scene, surfaceOnly, table);

            if (surfaceOnly)
            {
                Assert.Empty(masked);
            }
            else
            {
                Assert.Equal(all, masked);
            }
        }

        [TileDataPackTheory]
        [MemberData(nameof(SurfaceOnlyArms))]
        public void Bank_window_behind_a_nearer_window_stays_hidden_with_surface_only(bool surfaceOnly, bool withTable)
        {
            // round 7 acceptance (Moonglow bank 4462 1169 0, light 05): the ground floor window behind the upper wall line's
            // window, and the zig-zag's windows, keep their occluders in both states: a surface glow must not light the
            // nearer walls
            PackArt pack = PackArt.Get();
            byte[] table = withTable ? LightClassFile.Seed() : Array.Empty<byte>();
            var zigzag = Parse(ZigZag).ToList();

            Assert.Contains(new Obj(4467, 1169, 20, 0x0162), Masked(pack, new Obj(4465, 1167, 0, 0x0163), zigzag, surfaceOnly, table));
            Assert.Contains(new Obj(4463, 1167, 20, 0x0163), Masked(pack, new Obj(4462, 1167, 20, 0x0162), zigzag, surfaceOnly, table));
            Assert.Contains(new Obj(4468, 1175, 20, 0x0163), Masked(pack, new Obj(4467, 1175, 20, 0x0162), zigzag, surfaceOnly, table));

            foreach (Obj window in zigzag.Where(o => pack.Statics[o.Graphic].IsLight && pack.Statics[o.Graphic].IsWindow))
            {
                Assert.True(Surface(pack, window, table), $"{window}");
                Assert.Equal(Occluders(pack, window, zigzag), Masked(pack, window, zigzag, surfaceOnly, table));
            }
        }

        [TileDataPackTheory]
        [MemberData(nameof(SurfaceOnlyArms))]
        public void Lamps_eave_lamp_and_Deceit_torches_are_radial_and_unmasked_with_surface_only(bool surfaceOnly, bool withTable)
        {
            // round 7 acceptance: the Moonglow street lamps (4444 1171), the lamp post under the eave (4470 1091) and Deceit's
            // wall torches (5188 638) are radial: with the switch on they get no occluders; off they keep today's (none at
            // the lamps since rounds 4 and 6a)
            PackArt pack = PackArt.Get();
            byte[] table = withTable ? LightClassFile.Seed() : Array.Empty<byte>();
            var lamps = Parse(Lamps).ToList();
            var eave = Parse(Eave).ToList();
            var deceit = MapStatics(0, 5178, 626, 5198, 648);
            var sources = lamps.Where(o => o.Graphic is 0x0B20 or 0x0B22).Select(o => (Src: o, Scene: lamps))
                .Append((Src: new Obj(4474, 1091, 0, 0x0B22), Scene: eave))
                .Concat(deceit.Where(o => o.Graphic == 0x0A07).Select(o => (Src: o, Scene: deceit)))
                .ToList();
            Assert.Equal(2, sources.Count(s => s.Src.Graphic == 0x0A07));

            foreach (var (src, scene) in sources)
            {
                Assert.False(Surface(pack, src, table), $"{src}");
                var masked = Masked(pack, src, scene, surfaceOnly, table);
                _output.WriteLine($"{src}: occluders {Occluders(pack, src, scene).Count}, masked {masked.Count}");

                if (surfaceOnly)
                {
                    Assert.Empty(masked);
                }
                else
                {
                    Assert.Equal(Occluders(pack, src, scene), masked);
                }
            }
        }

        [TileDataPackTheory]
        [MemberData(nameof(SurfaceOnlyArms))]
        public void Malas_fire_fields_are_not_masked_by_the_fire_fields_in_front_with_surface_only(bool surfaceOnly, bool withTable)
        {
            // round 7 (owner 2026-11-22, Malas 1030 520 -53, light_occlusion on): dark patches flickered inside the fire fields'
            // glow. Each fire field 0x3996 (map static, z -70, height 12, light 2, Animation | LightSource | Damaging: an occluder
            // by tiledata) is its own radial light; the fire fields on the next rows in front cover its light point with their
            // flame art, so their pixels were masked out of its glow, and the animated frame (GameObject.StaticArt) changes the
            // covered pixels every frame. Surface only: fire fields are radial, no masks
            PackArt pack = PackArt.Get();
            byte[] table = withTable ? LightClassFile.Seed() : Array.Empty<byte>();
            var scene = MapStatics(3, 1024, 510, 1040, 530);
            var fields = scene.Where(o => o.Graphic == 0x3996).ToList();
            Assert.True(fields.Count >= 6, $"fire fields {fields.Count}");
            // the animation frames' art ids (anim/animdata.bin): the light point is taken from the source's drawn frame and the
            // cover from the occluder's (GameScene.CollectOccluders)
            AnimDataEntry anim = AnimDataFile.Read(Path.Combine(TileDrawOracleTests.PackDir(), "anim", "animdata.bin")).First(e => e.Graphic == 0x3996);
            ReadOnlySpan<sbyte> offsets = anim.Frames;
            var frames = new List<ushort>();

            for (int i = 0; i < anim.FrameCount; i++)
            {
                frames.Add((ushort)(0x3996 + offsets[i]));
            }

            Assert.True(frames.Distinct().Count() > 1, "fire field not animated");
            int coveringPairs = 0, pairs = 0;

            foreach (Obj field in fields)
            {
                Assert.False(Surface(pack, field, table), $"{field}");
                // surface only: no occluders whatever the frames; off: the occluders of the base frame, as before
                var masked = Masked(pack, field, scene, surfaceOnly, table);

                if (surfaceOnly)
                {
                    Assert.Empty(masked);
                }
                else
                {
                    Assert.Equal(Occluders(pack, field, scene), masked);
                }

                foreach (Obj front in fields.Where(o => o.X - field.X + o.Y - field.Y > 0 && Math.Abs(o.X - field.X) <= 2 && Math.Abs(o.Y - field.Y) <= 2))
                {
                    foreach (ushort fs in frames)
                    {
                        foreach (ushort fo in frames)
                        {
                            pairs++;

                            if (LightOcclusion.Masks(surfaceOnly, Surface(pack, field, table)) && FrameHides(pack, field, fs, front, fo))
                            {
                                coveringPairs++;
                            }
                        }
                    }
                }
            }

            _output.WriteLine($"fire fields {fields.Count}, frames {string.Join(",", frames.Select(f => f.ToString("X4")))}: " +
                              $"{coveringPairs} of {pairs} (source frame, front field frame) pairs mask the glow");

            if (surfaceOnly)
            {
                Assert.Equal(0, coveringPairs);
            }
            else
            {
                // some frame pairs cover the light point, others do not: the mask flickers with the animation
                Assert.InRange(coveringPairs, 1, pairs - 1);
            }
        }

        // the front fire field drawn at frame fo hides the glow of src drawn at frame fs (LightOcclusion.Hides with the cover
        // test on the frames' art, as GameScene.StaticOccluder after GameObject.StaticArt)
        private static bool FrameHides(PackArt pack, Obj src, ushort fs, Obj front, ushort fo)
        {
            var (sax, say) = Anchor(src);
            var a = pack.Art(fs);
            LightOcclusion.LightPoint(sax, say, a.W, a.H, a.BX, a.BY, a.BW, a.BH, out int px, out int py);
            int srcTop = LightOcclusion.SourceTop(pack.Statics[src.Graphic].Height, say, py);
            var oa = pack.Art(fo);
            var (ax, ay) = Anchor(front);
            bool covers = oa.W != 0 && LightOcclusion.CoverPoint(ax, ay, oa.W, oa.H, px, py, out int lx, out int ly) && pack.Pixel(fo, lx, ly);
            ref StaticTiles data = ref pack.Statics[front.Graphic];

            return LightOcclusion.Hides(true, false, front.X - src.X, front.Y - src.Y, front.Z - src.Z, srcTop, ref data, covers);
        }
    }
}
