using Uo.Net;
using Uo.Pack;
using Uo.Render;
using static SDL3.SDL;

namespace Uo.Client;

/// <summary>Offline animation sheet: one row per body (equipped humans, a rider, horse, dragon, monsters, hued variants),
/// 8 directions standing then 8 walking, on a plain background (no map), through the real ECS → extract → shader path.
/// Saves zoom 1, 0.5 and 1.5 shots named by the anim page format, so a 16-bit and an Indexed8 run can be diffed.</summary>
public static class AnimSheet
{
    struct Row
    {
        public ushort Body, Hue;
        public (ushort Item, byte Layer, ushort Hue)[] Equip;
    }

    public static int Run(Options o, PackSet pack)
    {
        if (!pack.HasAnims) { Console.Error.WriteLine("anim sheet: the pack has no anims"); return 1; }
        Directory.CreateDirectory(o.AnimSheet);
        var clock = new Clock { Manual = true, Now = AnimMobileSprites.FrameTicks * 1000 + AnimMobileSprites.FrameTicks / 2 };
        var world = new EcsWorld(clock);

        (ushort, byte, ushort)[] male = [(0x1517, Layers.Shirt, 0x0035), (0x152E, Layers.Pants, 0x01BB), (0x170F, Layers.Shoes, 0), (0x203B, Layers.Hair, 0x044E), (0x13B9, Layers.OneHanded, 0), (0x1B76, Layers.TwoHanded, 0)];
        (ushort, byte, ushort)[] female = [(0x1F03, Layers.Robe, 0x0021), (0x170F, Layers.Shoes, 0x0455), (0x203C, Layers.Hair, 0x0481), (0x1F7B, Layers.Torso, 0x0059)];
        (ushort, byte, ushort)[] rider = [(0x1517, Layers.Shirt, 0), (0x152E, Layers.Pants, 0), (0x170F, Layers.Shoes, 0), (0x203B, Layers.Hair, 0x0451), (0x1515, Layers.Cloak, 0x0026), (0x3EA2, Layers.Mount, 0)];
        // partial-hue items (tiledata PartialHue) hued, on a fully hued body
        (ushort, byte, ushort)[] partial = [(0x1517, Layers.Shirt, 0x0021), (0x152E, Layers.Pants, 0x0059), (0x170F, Layers.Shoes, 0x0021), (0x1B76, Layers.TwoHanded, 0x0481), (0x13B9, Layers.OneHanded, 0x0026), (0x1408, Layers.Helmet, 0x0059)];
        Row[] rows =
        [
            new Row { Body = 0x190, Hue = 0x83EA, Equip = male },
            new Row { Body = 0x191, Hue = 0x83F0, Equip = female },
            new Row { Body = 0x190, Hue = 0x83EA, Equip = rider },
            new Row { Body = 0xCC, Equip = [] },
            new Row { Body = 0x3B, Equip = [] },
            new Row { Body = 0x11, Equip = [] },
            new Row { Body = 0x09, Hue = 0x0021, Equip = [] },
            new Row { Body = 0x190, Hue = 0x0481, Equip = partial },
        ];
        var ids = PackAnimIds.TryLoad(pack.Root) ?? TileDataMulAnimIds.TryDefault(o.TileDataMul);
        foreach (var r in rows)
            foreach (var (item, _, _) in r.Equip)
                if ((pack.Statics[item].Flags & TileFlag.PartialHue) != 0) Console.WriteLine($"  item {item:X4}: PartialHue, anim {ids?.Get(item):X4}");

        // tile (bx + 4c + 6r, by − 4c + 6r): columns 176 px apart along screen x, rows 264 px apart along screen y
        const int bx = 2000, by = 2000;
        uint serial = 0x100;
        var eq = new EquipEntry[8];
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < 16; c++)
            {
                int x = bx + 4 * c + 6 * r, y = by - 4 * c + 6 * r;
                for (int k = 0; k < rows[r].Equip.Length; k++)
                {
                    var (item, layer, hue) = rows[r].Equip[k];
                    eq[k] = new EquipEntry { Serial = 0x40000000u + serial * 16 + (uint)k, ItemId = item, Layer = layer, Hue = hue };
                }
                world.OnMobile(new MobileState { Serial = serial, Body = rows[r].Body, Hue = rows[r].Hue, X = (ushort)x, Y = (ushort)y, Z = 0, Direction = (Direction)(c & 7), Source = MobileSource.Incoming78 },
                    eq.AsSpan(0, rows[r].Equip.Length));
                if (c >= 8 && world.TryGet(serial, out var e)) // walking: mid-step, in place
                    e.GetComponent<Motion>() = new Motion { FromX = (ushort)x, FromY = (ushort)y, Start = clock.Now - EcsWorld.WalkTicks / 2, Duration = EcsWorld.WalkTicks * 1000 };
                serial++;
            }

        SDL_SetHint("SDL_GPU_DRIVER", o.Driver);
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO)) { Console.Error.WriteLine(SDL_GetError()); return 1; }
        int w = o.Width == 1600 ? 3000 : o.Width, h = o.Height == 900 ? 2200 : o.Height;
        var window = SDL_CreateWindow("Uo.Client anim sheet", w, h, SDL_WindowFlags.SDL_WINDOW_HIDDEN);
        string tag = pack.AnimIndexed ? "idx" : "b16";
        using (var workers = new WorkerPool(o.Workers))
        using (var renderer = new Renderer(window, true, pack, 1 << 16, 64, o.Driver))
        {
            var pages = new PageResidency(renderer.Device, pack, workers, o.Layers, 8, 8, o.ForceExpand);
            var animPages = Program.CreateAnimResidency(renderer, pack, workers, o);
            var chunks = new ChunkStore(pack, workers, 1 << 16);
            uint dynFirst = (uint)chunks.Alloc.Alloc(ClientApp.MaxDynamic);
            var extract = new RenderExtract(world.Store, pack, Program.CreateMobileSprites(pack, o), ClientApp.MaxDynamic);
            renderer.EnsureOffscreen((uint)w, (uint)h);
            renderer.ClearColor = new SDL_FColor { r = 0.30f, g = 0.30f, b = 0.32f, a = 1 };
            var table = new ChunkDraw[4];
            // camera on the middle of the sheet
            double camX = (bx - by) * 22 + 15 * 88, camY = (bx + by) * 22 + 3.5 * 264 - 60;
            long frame = 0;
            foreach (float zoom in new[] { 1f, 0.5f, 1.5f })
            {
                for (int settled = 0, guard = 0; settled < 3 && guard < 2000; guard++)
                {
                    frame++;
                    while (workers.TryDequeueDone(out var job)) if (job is PageJob pj) pj.Owner.OnLoaded(pj);
                    double cx = Math.Round(camX * zoom) / zoom, cy = Math.Round(camY * zoom) / zoom;
                    float hw = w * 0.5f / zoom, hh = h * 0.5f / zoom;
                    var view = new RectI((int)(cx - hw) - 1, (int)(cy - hh) - 1, (int)(cx + hw) + 1, (int)(cy + hh) + 1);
                    pages.BeginFrame();
                    animPages?.BeginFrame();
                    extract.Run(in view, pages, animPages, frame, clock.Now);
                    pages.StartLoads(frame);
                    animPages?.StartLoads(frame);
                    table[0] = new ChunkDraw { First = dynFirst, Prefix = 0 };
                    int camPxX = (int)Math.Floor(cx), camPxY = (int)Math.Floor(cy);
                    var fi = new Renderer.FrameInput
                    {
                        Chunks = chunks, Pages = pages, AnimPages = animPages, AnimTableBase = pack.AnimPageBase, Table = table, ChunkCount = 1, Instances = extract.Count,
                        Frame = frame, LinearFilter = zoom < 0.999f, Dynamic = extract.Instances, DynamicCount = extract.Count, DynamicFirst = dynFirst,
                        Uniforms = new FrameUniforms
                        {
                            CamPxX = camPxX, CamPxY = camPxY, CamFracX = (float)(cx - camPxX), CamFracY = (float)(cy - camPxY), ScaleX = zoom, ScaleY = zoom,
                            CamDepth = BakeJob.CameraDepthKey((float)(cy / 22.0)), ChunkCount = 1, InvAtlas = 1f / pack.PageSize,
                            FormatFlags = PageFormats.ShaderFlags(pack.Format), GrayTolerance = PageFormats.Bc7GrayTolerance / 255f, DepthScale = Renderer.DepthScale,
                        },
                    };
                    bool ready = !pages.Pending && pages.MissingVisible == 0 && (animPages == null || !animPages.Pending && animPages.MissingVisible == 0) && frame > 2;
                    settled = ready ? settled + 1 : 0;
                    if (settled >= 3)
                    {
                        fi.ScreenshotPath = Path.Combine(o.AnimSheet, $"anim_{tag}_z{(int)(zoom * 100):D3}.png");
                        renderer.RenderFrame(ref fi);
                        Console.WriteLine($"screenshot {fi.ScreenshotPath}: {extract.MobilesDrawn} mobiles, {extract.SpritesDrawn} sprites, art pages {pages.ResidentCount}" +
                            (animPages != null ? $", anim pages {animPages.ResidentCount}" : ""));
                    }
                    else renderer.RenderFrame(ref fi);
                }
            }
            renderer.DrainInFlight();
            pages.Dispose();
            animPages?.Dispose();
        }
        SDL_DestroyWindow(window);
        SDL_Quit();
        return 0;
    }
}
