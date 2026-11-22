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
        private static List<Obj> Occluders(PackArt pack, Obj src, IEnumerable<Obj> scene, bool on = true)
        {
            var (sax, say) = Anchor(src);
            var a = pack.Art(src.Graphic);
            LightOcclusion.LightPoint(sax, say, a.W, a.H, a.BX, a.BY, a.BW, a.BH, out int px, out int py);
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

                if (LightOcclusion.Hides(on, false, o.X - src.X, o.Y - src.Y, o.Z - src.Z, ref data, covers, facesAway))
                {
                    hidden.Add(o);
                }
            }

            return hidden;
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
    }
}
