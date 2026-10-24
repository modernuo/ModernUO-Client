using System.Diagnostics;
using static SDL3.SDL;

namespace Uo.Render;

public static unsafe class Program
{
    sealed class Options
    {
        public string Pack;
        public bool Bench;
        public string Screenshot;
        public double ViaX = double.NaN, ViaY;  // --via x y: settle there first, then jump (different chunk-table order)
        public string ShotsDir;      // --shots <dir>: offscreen scripted fly-in, saving frames mid-streaming
        public int ShotEvery = 15;
        public float Density = 1;    // offscreen pixel density override (HiDPI simulation)
        public bool Point;           // debug: old plain point sampling at scale >= 1 (shimmers at fractional scales)
        public bool Magenta;
        public bool PanTest;
        public string HueSheet;      // --hue-sheet <png> [hue]: PartialHue statics, unhued | hued, through the real shader path
        public int SheetHue = 0x21;
        public float GrayTol = -1;
        public int FormatFlags = -1;  // debug override of PageFormats.ShaderFlags   // --gray-tol N (in 1/255): override the BC7 partial-hue gray tolerance
        public bool Offscreen;       // --offscreen: render to an offscreen target (no swapchain), fence-throttled
        public bool SizeGiven;
        public List<string> BenchFormats; // --bench-formats packA packB ...: one child process per pack         // offscreen: pan at keyboard speed (60 Hz steps) and count visible chunk pop-in         // debug: clear to magenta so unpainted pixels stand out
        public bool ForceExpand;     // --force-expand: 16-bit packs go through the worker RGBA8 expansion even if the GPU samples 16-bit
        public bool SelfTest;        // --self-test: exhaustive PixelExpand check against ClassicUO's table, then exit
        public string Driver = "direct3d12"; // --driver vulkan|direct3d12
        public float StartX = 1495, StartY = 1629, StartZoom = 1;
        public int Width = 1600, Height = 900;
        public int Layers = 24, PageBudget = 2, ChunkBudget = 64;
        public int InstanceCapacity = 2 << 20;
        public int Workers = Math.Clamp(Environment.ProcessorCount / 2 - 1, 2, 8);
        public float BenchSeconds = 20;
    }

    public static int Main(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--make-synthetic":
                    Synthetic.Make(i + 1 < args.Length ? args[i + 1] : @"C:\Repositories\uo-packs\synthetic");
                    return 0;
                case "--probe":
                    Probe(o.Pack ?? PackSet.FindDefault(), int.Parse(args[i + 1]), int.Parse(args[i + 2]), i + 3 < args.Length ? int.Parse(args[i + 3]) : 2);
                    return 0;
                case "--dump-sprite": // --dump-sprite art|tex <id hex> <out.png>
                    DumpSprite(o.Pack ?? PackSet.FindDefault(), args[i + 1], Convert.ToInt32(args[i + 2], 16), args[i + 3]);
                    return 0;
                case "--collisions": // --collisions x y [radiusChunks]: overlapping sprites with equal depth keys
                    Collisions(o.Pack ?? PackSet.FindDefault(), int.Parse(args[i + 1]), int.Parse(args[i + 2]), i + 3 < args.Length ? int.Parse(args[i + 3]) : 6);
                    return 0;
                case "--debug-compress-pages": // <srcPackDir> <dstPackDir>: Brotli (Kind=1) every other page, for reader tests
                    DebugCompressPages(args[i + 1], args[i + 2]);
                    return 0;
                case "--pack": o.Pack = args[++i]; break;
                case "--bench": o.Bench = true; break;
                case "--bench-seconds": o.BenchSeconds = float.Parse(args[++i]); break;
                case "--shots": o.ShotsDir = args[++i]; break;
                case "--magenta": o.Magenta = true; break;
                case "--pan-test": o.PanTest = true; break;
                case "--offscreen": o.Offscreen = true; break;
                case "--gray-tol": o.GrayTol = float.Parse(args[++i]); break;
                case "--format-flags": o.FormatFlags = int.Parse(args[++i]); break;
                case "--force-expand": o.ForceExpand = true; break;
                case "--self-test": o.SelfTest = true; break;
                case "--driver": o.Driver = args[++i]; break;
                case "--bench-formats":
                    o.BenchFormats = new List<string>();
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--")) o.BenchFormats.Add(args[++i]);
                    break;
                case "--point": o.Point = true; break;
                case "--via": o.ViaX = double.Parse(args[++i]); o.ViaY = double.Parse(args[++i]); break;
                case "--shot-every": o.ShotEvery = int.Parse(args[++i]); break;
                case "--density": o.Density = float.Parse(args[++i]); break;
                case "--layers": o.Layers = int.Parse(args[++i]); break;
                case "--page-budget": o.PageBudget = int.Parse(args[++i]); break;
                case "--chunk-budget": o.ChunkBudget = int.Parse(args[++i]); break;
                case "--workers": o.Workers = int.Parse(args[++i]); break;
                case "--size":
                    var wh = args[++i].Split('x');
                    o.Width = int.Parse(wh[0]); o.Height = int.Parse(wh[1]); o.SizeGiven = true;
                    break;
                case "--at":
                    o.StartX = float.Parse(args[++i]); o.StartY = float.Parse(args[++i]); o.StartZoom = float.Parse(args[++i]);
                    break;
                case "--hue-sheet":
                    o.HueSheet = args[++i];
                    o.Screenshot = o.HueSheet;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) o.SheetHue = Convert.ToInt32(args[++i], 16);
                    o.StartX = 0; o.StartY = 0; o.StartZoom = 1;
                    break;
                case "--screenshot":
                    o.Screenshot = args[++i];
                    if (i + 3 < args.Length && float.TryParse(args[i + 1], out var sx) && float.TryParse(args[i + 2], out var sy) && float.TryParse(args[i + 3], out var sz))
                    {
                        o.StartX = sx; o.StartY = sy; o.StartZoom = sz; i += 3;
                    }
                    break;
                default:
                    Console.Error.WriteLine($"unknown argument {args[i]}");
                    return 2;
            }
        }
        if (o.SelfTest)
        {
            long ts = Stopwatch.GetTimestamp();
            int bad = PixelExpand.SelfTest();
            Console.WriteLine($"self-test: PixelExpand vs ClassicUO 5->8 table, all 65536 texels (scalar + span at 8 offsets): {bad} mismatches, {Stopwatch.GetElapsedTime(ts).TotalMilliseconds:F0} ms (Vector256 {System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated}, Vector128 {System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated})");
            return bad != 0 ? 1 : 0;
        }
        if (o.BenchFormats != null) return FormatBench.Run(o.BenchFormats, o.SizeGiven ? o.Width : 3840, o.SizeGiven ? o.Height : 2160, o.Layers, o.Driver);
        o.Pack ??= PackSet.FindDefault();
        return Run(o);
    }

    /// <summary>Debug: dumps land and statics around a tile (tile id, z, tiledata, sprite entry).</summary>
    static void Probe(string packDir, int x0, int y0, int radius)
    {
        var pack = new PackSet(packDir, 0);
        int rb = pack.RegionBlocks;
        var cache = new Dictionary<int, Region>();
        Region Reg(int tx, int ty)
        {
            int rx = (tx >> 3) / rb, ry = (ty >> 3) / rb, key = ry * 4096 + rx;
            if (!cache.TryGetValue(key, out var r)) { r = new Region { Rx = rx, Ry = ry }; r.Parse(Uo.Pack.PackConst.RegionPath(pack.FacetDir, rx, ry)); cache[key] = r; }
            return r;
        }
        for (int ty = y0 - radius; ty <= y0 + radius; ty++)
            for (int tx = x0 - radius; tx <= x0 + radius; tx++)
            {
                var r = Reg(tx, ty);
                uint off = r.BlockOffset((tx >> 3) - r.Rx * rb, (ty >> 3) - r.Ry * rb);
                if (off == 0) { Console.WriteLine($"({tx},{ty}) empty"); continue; }
                var l = r.Land(off)[(ty & 7) * 8 + (tx & 7)];
                int id = l.TileId & 0x3FFF;
                var ld = pack.Land[id];
                var sp = pack.Sprites[id];
                string tex = ld.TexId != 0 && ld.TexId < pack.Texmaps.Length && pack.Texmaps[ld.TexId].Present ? $"tex {ld.TexId:X4} p{pack.Texmaps[ld.TexId].Page} {pack.Texmaps[ld.TexId].W}x{pack.Texmaps[ld.TexId].H}" : $"tex {ld.TexId:X4} (none)";
                Console.Write($"({tx},{ty}) land {id:X4} z{l.Z} flags {ld.Flags:X} {tex} art p{sp.Page} {sp.W}x{sp.H}+{sp.TrimX},{sp.TrimY}/{sp.OrigW}x{sp.OrigH}");
                foreach (var s in r.Statics(off))
                    if (s.X == (tx & 7) && s.Y == (ty & 7))
                    {
                        var e = pack.Sprites[Uo.Pack.PackConst.StaticSpriteBase + s.Graphic];
                        Console.Write($" | st {s.Graphic:X4} z{s.Z} h{pack.Statics[s.Graphic].Height} hue {s.Hue:X} flags {pack.Statics[s.Graphic].Flags:X} p{e.Page} {e.W}x{e.H}");
                    }
                Console.WriteLine();
            }
    }

    /// <summary>Debug: bakes the chunks around a tile synchronously and counts pairs of sprites from different tiles
    /// that overlap on screen and share a depth key. Such pairs resolve by draw order, which changes as chunks stream.</summary>
    static void Collisions(string packDir, int x0, int y0, int r)
    {
        var pack = new PackSet(packDir, 0);
        BakeJob.Pack = pack;
        int rb = pack.RegionBlocks;
        var regions = new Dictionary<int, Region>();
        Region Reg(int rx, int ry)
        {
            int key = ry * 4096 + rx;
            if (!regions.TryGetValue(key, out var reg)) { reg = new Region { Rx = rx, Ry = ry, Loaded = true }; reg.Parse(Uo.Pack.PackConst.RegionPath(pack.FacetDir, rx, ry)); regions[key] = reg; }
            return reg;
        }
        var all = new List<(SpriteInstance I, int Chunk)>();
        int cbx = x0 >> 3, cby = y0 >> 3, chunk = 0;
        for (int by = cby - r; by <= cby + r; by++)
            for (int bx = cbx - r; bx <= cbx + r; bx++, chunk++)
            {
                var job = new BakeJob { Bx = bx, By = by };
                int rx = bx / rb, ry = by / rb;
                job.Rx0 = rx - 1; job.Ry0 = ry - 1;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (rx + dx >= 0 && ry + dy >= 0) job.Regions[(dy + 1) * 3 + dx + 1] = Reg(rx + dx, ry + dy);
                job.Run();
                for (int k = 0; k < job.Count; k++) all.Add((job.Instances[k], chunk));
            }
        all.Sort((a, b) => a.I.Depth.CompareTo(b.I.Depth));
        int pairs = 0, crossChunk = 0, groups = 0;
        for (int i = 0; i < all.Count;)
        {
            int j = i + 1;
            while (j < all.Count && all[j].I.Depth == all[i].I.Depth) j++;
            if (j - i > 1) groups++;
            for (int a = i; a < j; a++)
                for (int b = a + 1; b < j; b++)
                {
                    var p = all[a].I; var q = all[b].I;
                    int pw = (int)(p.Whf & 0xFFF), ph = (int)((p.Whf >> 12) & 0xFFF), qw = (int)(q.Whf & 0xFFF), qh = (int)((q.Whf >> 12) & 0xFFF);
                    if ((p.Whf >> 24 & SpriteInstance.FlagStretch) != 0) { pw = 44; ph = 44; }
                    if ((q.Whf >> 24 & SpriteInstance.FlagStretch) != 0) { qw = 44; qh = 44; }
                    bool overlap = p.X < q.X + qw && q.X < p.X + pw && p.Y < q.Y + qh && q.Y < p.Y + ph;
                    if (!overlap) continue;
                    pairs++;
                    if (all[a].Chunk != all[b].Chunk) crossChunk++;
                }
            i = j;
        }
        Console.WriteLine($"{all.Count} instances in {(2 * r + 1) * (2 * r + 1)} chunks: {groups} equal-key groups, {pairs} overlapping equal-key pairs ({crossChunk} across chunks)");
    }

    static void DumpSprite(string packDir, string kind, int id, string outPath)
    {
        var pack = new PackSet(packDir, 0);
        var e = kind == "tex" ? pack.Texmaps[id] : pack.Sprites[id];
        int page = kind == "tex" ? pack.TexPageBase + e.Page : e.Page;
        int size = Uo.Pack.PackConst.PageSize;
        var chain = new byte[Uo.Pack.AtlasPage.ChainBytes(size, Uo.Pack.PackConst.PageMipCount)];
        Uo.Pack.AtlasPage.Read(pack.PagePaths[page], chain);
        var px = new byte[e.W * e.H * 4];
        for (int y = 0; y < e.H; y++)
            Array.Copy(chain, ((e.Y + y) * size + e.X) * 4, px, y * e.W * 4, e.W * 4);
        long sum = 0; int n = 0;
        for (int i = 0; i < px.Length; i += 4) if (px[i + 3] != 0) { sum += px[i] + px[i + 1] + px[i + 2]; n++; }
        Console.WriteLine($"{kind} {id:X4}: page {page} at {e.X},{e.Y} {e.W}x{e.H}, mean rgb {(n > 0 ? sum / (3.0 * n) : 0):F1} over {n} opaque px");
        Png.Write(outPath, e.W, e.H, px);
    }

    /// <summary>Lays out PartialHue statics in a grid around world (0,0): each cell shows the sprite unhued, then with
    /// the hue applied in partial mode. Shows how a page format preserves the exact-gray pixels partial hue keys on.</summary>
    static int BuildHueSheet(PackSet pack, Options o, out SpriteInstance[] inst)
    {
        var ids = new List<int>();
        for (int g = 0; g < pack.Statics.Length; g++)
        {
            if ((pack.Statics[g].Flags & TileFlags.PartialHue) == 0) continue;
            var e = pack.Sprites[Uo.Pack.PackConst.StaticSpriteBase + g];
            if (!e.Present || e.W < 16 || e.H < 16 || e.W > 100 || e.H > 190) continue;
            ids.Add(g);
        }
        // spread the sample across the id range (clothes, weapons, furniture ...)
        const int cols = 7, rows = 4, cellW = 228, cellH = 220;
        int want = cols * rows;
        var pick = new List<int>();
        for (int k = 0; k < want && ids.Count > 0; k++) pick.Add(ids[(int)((long)k * ids.Count / want)]);
        Console.WriteLine($"hue sheet: {ids.Count} PartialHue statics in range, showing {pick.Count}, hue 0x{o.SheetHue:X}");
        inst = new SpriteInstance[pick.Count * 2];
        uint baseDepth = BakeJob.CameraDepthKey(0);
        int n = 0;
        for (int k = 0; k < pick.Count; k++)
        {
            var e = pack.Sprites[Uo.Pack.PackConst.StaticSpriteBase + pick[k]];
            int cx = -cols * cellW / 2 + (k % cols) * cellW, cy = -rows * cellH / 2 + (k / cols) * cellH;
            for (int h = 0; h < 2; h++)
            {
                ref var i = ref inst[n++];
                i.X = cx + 8 + h * (cellW / 2) + (cellW / 2 - 16 - e.W) / 2;
                i.Y = cy + 8 + (cellH - 16 - e.H);
                uint flags = h == 1 ? SpriteInstance.FlagHued | SpriteInstance.FlagPartial : 0;
                i.Whf = SpriteInstance.PackWhf(e.W, e.H, flags);
                i.Uv = e.X | ((uint)e.Y << 16);
                i.PageHue = e.Page | (h == 1 ? (uint)(o.SheetHue - 1) << 16 : 0);
                i.Depth = baseDepth + (uint)n;
            }
        }
        return n;
    }

    static void DebugCompressPages(string src, string dst)
    {
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var outPath = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var name = Path.GetFileName(f);
            bool page = name.StartsWith("page_") && int.Parse(name[5..9]) % 2 == 1;
            if (!page) { File.Copy(f, outPath, true); continue; }
            var bytes = File.ReadAllBytes(f);
            using var fs = File.Create(outPath);
            fs.Write(bytes, 0, 6);
            fs.Write(BitConverter.GetBytes(Uo.Pack.FileKind.Brotli));
            fs.Write(bytes, 8, 8);
            using (var br = new System.IO.Compression.BrotliStream(fs, new System.IO.Compression.BrotliCompressionOptions { Quality = 1 }, leaveOpen: true))
                br.Write(bytes, 16, bytes.Length - 16);
            Console.WriteLine($"{rel}: {bytes.Length >> 10} KiB -> {fs.Length >> 10} KiB");
        }
    }

    static int Run(Options o)
    {
        Console.WriteLine($"pack: {o.Pack}");
        var pack = new PackSet(o.Pack, 0);
        Console.WriteLine($"  {pack.PageCount} pages ({pack.TexPageBase} art + {pack.PageCount - pack.TexPageBase} tex) {PageFormats.Name(pack.Format)} {pack.PageSize}px/{pack.PageMips} mips, {pack.Sprites.Length} sprites, {pack.HueCount} hues, map {pack.MapWidth}x{pack.MapHeight}");

        SDL_SetHint("SDL_GPU_DRIVER", o.Driver);
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO)) { Console.Error.WriteLine(SDL_GetError()); return 1; }
        bool offscreen = o.Screenshot != null || o.ShotsDir != null || o.PanTest || o.Offscreen;
        if (o.ShotsDir != null) Directory.CreateDirectory(o.ShotsDir);
        var flags = SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY;
        if (offscreen) flags |= SDL_WindowFlags.SDL_WINDOW_HIDDEN;
        var window = SDL_CreateWindow("Uo.Render", o.Width, o.Height, flags);
        if (window == IntPtr.Zero) { Console.Error.WriteLine(SDL_GetError()); return 1; }

        using var workers = new WorkerPool(o.Workers);
        using var renderer = new Renderer(window, offscreen, pack, o.InstanceCapacity, 8192, o.Driver);
        Console.WriteLine($"  device: {SDL_GetGPUDeviceDriver(renderer.Device)}, swapchain format {renderer.ColorFormat}, immediate={renderer.ImmediateSupported}");
        var residency = new PageResidency(renderer.Device, pack, workers, o.Layers, o.PageBudget, Math.Max(2, o.PageBudget * 2), o.ForceExpand);
        Console.WriteLine($"  shaders {renderer.ShaderFormat}, pages {residency.FormatDescription}");
        var chunks = new ChunkStore(pack, workers, o.InstanceCapacity);

        if (o.Bench) renderer.VSync = false;
        renderer.ApplyPresentMode();
        if (offscreen) renderer.EnsureOffscreen((uint)o.Width, (uint)o.Height);
        renderer.Throttle = o.Offscreen;
        renderer.ForcePoint = o.Point;
        if (o.HueSheet != null)
        {
            renderer.ClearColor = new SDL_FColor { r = 0.30f, g = 0.30f, b = 0.32f, a = 1 };
            int n = BuildHueSheet(pack, o, out var sheet);
            chunks.InjectDebugChunk(sheet, n);
        }
        if (o.Magenta) renderer.ClearColor = new SDL_FColor { r = 1, g = 0, b = 1, a = 1 };

        var app = new App(o, pack, window, renderer, residency, chunks, workers);
        int rc = app.Loop();
        residency.Dispose();
        return rc;
    }

    sealed class App
    {
        readonly Options _o;
        readonly PackSet _pack;
        readonly IntPtr _window;
        readonly Renderer _r;
        readonly PageResidency _pages;
        readonly ChunkStore _chunks;
        readonly WorkerPool _workers;
        readonly ChunkDraw[] _table = new ChunkDraw[8192];

        // camera (world pixels: X = (x-y)*22, Y = (x+y)*22)
        double _camX, _camY;
        float _zoom;
        bool _dragging;

        readonly byte[] _title = new byte[512];
        long _prevFrameStart;
        double _submitOverheadMs;
        long _panStart = -1;
        double _velX, _velY, _prevCx, _prevCy;
        int _panFrames, _panPopFrames, _panPopChunks, _panMaxEvict;
        bool _viaPending;

        public App(Options o, PackSet pack, IntPtr window, Renderer r, PageResidency pages, ChunkStore chunks, WorkerPool workers)
        {
            _o = o; _pack = pack; _window = window; _r = r; _pages = pages; _chunks = chunks; _workers = workers;
            SetCameraTile(o.StartX, o.StartY);
            _zoom = o.StartZoom;
            if (!double.IsNaN(o.ViaX)) { SetCameraTile(o.ViaX, o.ViaY); _viaPending = true; }
        }

        void SetCameraTile(double tx, double ty)
        {
            _camX = (tx - ty) * 22;
            _camY = (tx + ty) * 22;
        }

        public int Loop()
        {
            long frame = 0;
            double freq = Stopwatch.Frequency;
            long last = Stopwatch.GetTimestamp();
            long titleAt = last;
            int framesSinceTitle = 0;
            double cpuAccum = 0;
            bool running = true;

            // bench state
            int maxSamples = 1 << 20;
            float[] cpuMs = _o.Bench ? new float[maxSamples] : null;
            float[] frameMs = _o.Bench ? new float[maxSamples] : null;
            float[] gpuMs = _o.Bench ? new float[maxSamples / 16] : null;
            int samples = 0, intervals = 0, gpuSamples = 0;
            bool skipInterval = false;
            long benchStart = 0;
            bool benchRunning = false;
            long allocStart = 0, allocTotalStart = 0;
            int gc0 = 0, gc1 = 0, gc2 = 0;
            long warmupUntil = last + (long)(freq * 1.0);
            int maxBakeQueue = 0;

            // screenshot state
            long shotDeadline = last + (long)(freq * 60);
            int settledFrames = 0;

            while (running)
            {
                long t0 = Stopwatch.GetTimestamp();
                double dt = Math.Min(0.1, (t0 - last) / freq);
                last = t0;
                frame++;

                while (SDL_PollEvent(out var ev))
                {
                    switch ((SDL_EventType)ev.type)
                    {
                        case SDL_EventType.SDL_EVENT_QUIT:
                        case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                            running = false; break;
                        case SDL_EventType.SDL_EVENT_KEY_DOWN:
                            if (ev.key.scancode == SDL_Scancode.SDL_SCANCODE_ESCAPE) running = false;
                            else if (ev.key.scancode == SDL_Scancode.SDL_SCANCODE_V && !ev.key.repeat) { _r.VSync = !_r.VSync; _r.ApplyPresentMode(); }
                            break;
                        case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                            if (!_o.Bench) _zoom = Math.Clamp(_zoom * MathF.Pow(1.15f, ev.wheel.y), 0.5f, 4f);
                            break;
                        case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN: _dragging = true; break;
                        case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP: _dragging = false; break;
                        case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                            // motion is in window points; one point = density pixels = 1/zoom world pixels
                            if (_dragging && !_o.Bench)
                            {
                                _camX -= ev.motion.xrel / _zoom;
                                _camY -= ev.motion.yrel / _zoom;
                            }
                            break;
                    }
                }

                float density = SDL_GetWindowPixelDensity(_window);
                if (density <= 0) density = 1;

                if (_o.Bench)
                {
                    if (!benchRunning && t0 >= warmupUntil && _chunks.PendingChunks == 0)
                    {
                        if (_r.Offscreen) _submitOverheadMs = _r.MeasureSubmitOverheadMs();
                        benchRunning = true;
                        benchStart = Stopwatch.GetTimestamp();
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
                        allocStart = GC.GetAllocatedBytesForCurrentThread();
                        allocTotalStart = GC.GetTotalAllocatedBytes(false);
                    }
                    double bt = benchRunning ? (t0 - benchStart) / freq : 0;
                    BenchPath(bt / _o.BenchSeconds * 20.0, out double bx, out double by, out _zoom);
                    SetCameraTile(bx, by);
                    if (benchRunning && bt >= _o.BenchSeconds) running = false;
                }
                else if (_o.PanTest)
                {
                    // Keyboard pan speed (900 device-independent px/s / zoom) at 60 Hz: 6 s east at zoom 1,
                    // 6 s back west at 0.5, 6 s south at 2. Starts after the initial view has streamed in.
                    if (_panStart < 0 && _chunks.PendingChunks == 0 && frame > 10) _panStart = frame;
                    double pt = _panStart < 0 ? 0 : (frame - _panStart) / 60.0;
                    _zoom = pt < 6 ? 1f : pt < 12 ? 0.5f : 2f;
                    double v = 900.0 / _zoom / 60.0;
                    if (_panStart >= 0)
                    {
                        if (pt < 6) _camX += v; else if (pt < 12) _camX -= v; else _camY += v;
                        _panFrames++;
                        if (_chunks.MissingVisible > 0) { _panPopFrames++; _panPopChunks += _chunks.MissingVisible; }
                        _panMaxEvict = Math.Max(_panMaxEvict, _pages.UploadsThisFrame);
                    }
                    if (pt >= 18) running = false;
                }
                else if (_o.ShotsDir != null)
                {
                    double st = frame / 60.0; // fixed 60 Hz steps, like a vsync'd live session
                    ShotsPath(st, out double sx2, out double sy2, out _zoom);
                    SetCameraTile(sx2, sy2);
                    if (st > 24) running = false;
                }
                else if (_o.Screenshot == null)
                {
                    var keys = SDL_GetKeyboardState();
                    float speed = 900f * (float)dt / _zoom;
                    if (keys[(int)SDL_Scancode.SDL_SCANCODE_W] || keys[(int)SDL_Scancode.SDL_SCANCODE_UP]) _camY -= speed;
                    if (keys[(int)SDL_Scancode.SDL_SCANCODE_S] || keys[(int)SDL_Scancode.SDL_SCANCODE_DOWN]) _camY += speed;
                    if (keys[(int)SDL_Scancode.SDL_SCANCODE_A] || keys[(int)SDL_Scancode.SDL_SCANCODE_LEFT]) _camX -= speed;
                    if (keys[(int)SDL_Scancode.SDL_SCANCODE_D] || keys[(int)SDL_Scancode.SDL_SCANCODE_RIGHT]) _camX += speed;
                }

                // ---- view
                SDL_GetWindowSizeInPixels(_window, out int pw, out int ph);
                if (_r.Offscreen) { pw = _o.Width; ph = _o.Height; density = _o.Density; }
                float scale = _zoom * density;
                double cx = Math.Round(_camX * scale) / scale, cy = Math.Round(_camY * scale) / scale;
                float halfW = pw * 0.5f / scale, halfH = ph * 0.5f / scale;
                var view = new RectI((int)(cx - halfW) - 1, (int)(cy - halfH) - 1, (int)(cx + halfW) + 1, (int)(cy + halfH) + 1);
                float pre = 0.35f;
                var need = new RectI((int)(cx - halfW * (1 + pre)) - 128, (int)(cy - halfH * (1 + pre)) - 128, (int)(cx + halfW * (1 + pre)) + 128, (int)(cy + halfH * (1 + pre)) + 128);
                // Bias the prefetch toward the direction of motion: 0.4 s of travel, at most half a view.
                // Jumps larger than half a view in one frame (teleport, or a hitch under a time-scripted camera)
                // are not motion and must not inflate the prefetch set.
                if (frame > 1)
                {
                    double ddx = cx - _prevCx, ddy = cy - _prevCy;
                    if (Math.Abs(ddx) > halfW || Math.Abs(ddy) > halfH) { ddx = 0; ddy = 0; _velX = _velY = 0; }
                    double inv = 1.0 / Math.Max(dt, 1e-4);
                    _velX = _velX * 0.8 + ddx * inv * 0.2; // world px / s
                    _velY = _velY * 0.8 + ddy * inv * 0.2;
                }
                _prevCx = cx; _prevCy = cy;
                int leadX = (int)Math.Clamp(_velX * 0.4, -halfW, halfW), leadY = (int)Math.Clamp(_velY * 0.4, -halfH, halfH);
                if (leadX > 0) need.X1 += leadX; else need.X0 += leadX;
                if (leadY > 0) need.Y1 += leadY; else need.Y0 += leadY;

                // ---- streaming
                while (_workers.TryDequeueDone(out var job))
                {
                    switch (job)
                    {
                        case BakeJob b: _chunks.OnBaked(b); break;
                        case RegionJob rj: _chunks.OnRegionLoaded(rj); break;
                        case PageJob pj: _pages.OnLoaded(pj); break;
                    }
                }
                if (_o.HueSheet == null) _chunks.Update(frame, in view, in need);
                maxBakeQueue = Math.Max(maxBakeQueue, _chunks.BakesInFlight);
                _pages.BeginFrame();
                int chunkCount = _chunks.BuildDrawList(in view, _table, _pages, frame, out int instances);
                _pages.StartLoads(frame);

                // ---- render
                int camPxX = (int)Math.Floor(cx), camPxY = (int)Math.Floor(cy);
                var fi = new Renderer.FrameInput
                {
                    Chunks = _chunks, Pages = _pages, Table = _table, ChunkCount = chunkCount, Instances = instances,
                    Frame = frame, LinearFilter = scale < 0.999f, ChunkUploadBudget = _o.ChunkBudget,
                    Uniforms = new FrameUniforms
                    {
                        CamPxX = camPxX, CamPxY = camPxY,
                        CamFracX = (float)(cx - camPxX), CamFracY = (float)(cy - camPxY),
                        ScaleX = scale, ScaleY = scale,
                        CamDepth = BakeJob.CameraDepthKey((float)(cy / 22.0)),
                        ChunkCount = (uint)chunkCount,
                        InvAtlas = 1f / _pack.PageSize,
                        FormatFlags = _o.FormatFlags >= 0 ? (uint)_o.FormatFlags : PageFormats.ShaderFlags(_pack.Format),
                        GrayTolerance = (_o.GrayTol >= 0 ? _o.GrayTol : PageFormats.Bc7GrayTolerance) / 255f,
                        DepthScale = Renderer.DepthScale,
                    },
                };

                if (_o.Screenshot != null)
                {
                    bool settled = _chunks.PendingChunks == 0 && _chunks.UploadQueueLength == 0 && !_pages.Pending && _pages.MissingVisible == 0 && frame > 3;
                    settledFrames = settled ? settledFrames + 1 : 0;
                    if (settledFrames >= 3 && _viaPending)
                    {
                        _viaPending = false;
                        settledFrames = 0;
                        SetCameraTile(_o.StartX, _o.StartY);
                        continue;
                    }
                    if (settledFrames >= 3 || t0 > shotDeadline)
                    {
                        if (!settled) Console.WriteLine("screenshot: streaming did not settle before the deadline");
                        fi.ScreenshotPath = _o.Screenshot;
                        _r.RenderFrame(ref fi);
                        Console.WriteLine($"screenshot {_o.Screenshot}: {_r.TargetW}x{_r.TargetH}, {instances} instances, {chunkCount} chunks, {_pages.ResidentCount} pages resident, frame {frame}");
                        return 0;
                    }
                }

                if (_o.ShotsDir != null && frame % _o.ShotEvery == 0)
                {
                    fi.ScreenshotPath = Path.Combine(_o.ShotsDir, $"shot_{frame:D5}.png");
                    _r.RenderFrame(ref fi);
                    Console.WriteLine($"{fi.ScreenshotPath}: zoom {_zoom:F2}, {instances} inst, {chunkCount} chunks, pending chunks {_chunks.PendingChunks}, pages {_pages.ResidentCount} missing-visible {_pages.MissingVisible}");
                    continue;
                }

                // Offscreen bench: every 30th frame runs alone on an idle GPU and is fence-timed (GPU time estimate;
                // SDL_GPU exposes no timestamp queries).
                if (benchRunning && _r.Offscreen && samples % 30 == 29)
                {
                    _r.DrainInFlight();
                    fi.GpuSample = true;
                }
                _r.RenderFrame(ref fi);
                if (_o.PanTest) // pace like a 60 Hz vsync'd session so worker latency is measured in real frames
                    while (Stopwatch.GetTimestamp() - t0 < freq / 60) Thread.Sleep(1);
                long t1 = Stopwatch.GetTimestamp();
                double cpu = (t1 - t0 - _r.AcquireWaitTicks) * 1000.0 / freq;
                cpuAccum += cpu;
                framesSinceTitle++;

                if (benchRunning && samples < maxSamples)
                {
                    if (fi.GpuSample)
                    {
                        cpuMs[samples] = -1;
                        if (gpuSamples < gpuMs.Length) gpuMs[gpuSamples++] = (float)(_r.GpuSampleTicks * 1000.0 / freq);
                    }
                    else
                    {
                        cpuMs[samples] = (float)cpu;
                        // the interval ending now is the previous frame's duration; skip it after a GPU sample frame
                        if (!skipInterval && _prevFrameStart != 0) frameMs[intervals++] = (float)((t0 - _prevFrameStart) * 1000.0 / freq);
                    }
                    samples++;
                    skipInterval = fi.GpuSample;
                }
                _prevFrameStart = t0;

                if (t1 - titleAt > freq / 2)
                {
                    double secs = (t1 - titleAt) / freq;
                    UpdateTitle(framesSinceTitle / secs, cpuAccum / framesSinceTitle, instances, _r.DrawCalls, _pages.ResidentCount, _pages.UploadsThisFrame, _chunks.ReadyChunks, _r.LastChunkUploads);
                    titleAt = t1; framesSinceTitle = 0; cpuAccum = 0;
                }
            }

            if (_o.PanTest)
                Console.WriteLine($"pan-test: {_panFrames} frames, {_panPopFrames} with visible chunks not ready ({_panPopChunks} chunk-frames), page uploads {_pages.TotalUploads}, evictions {_pages.Evictions}");
            if (_o.PanTest || _o.Bench)
                Console.WriteLine($"  bakes: {_chunks.BakeStats}\n  regions: {_chunks.RegionStats}");
            if (_o.Bench)
            {
                long allocMain = GC.GetAllocatedBytesForCurrentThread() - allocStart;
                long allocAll = GC.GetTotalAllocatedBytes(false) - allocTotalStart;
                int g0 = GC.CollectionCount(0) - gc0, g1 = GC.CollectionCount(1) - gc1, g2 = GC.CollectionCount(2) - gc2;
                double secs = (Stopwatch.GetTimestamp() - benchStart) / freq;
                ReportBench(cpuMs, frameMs, intervals, gpuMs, gpuSamples, samples, secs, allocMain, allocAll, g0, g1, g2, maxBakeQueue);
            }
            return 0;
        }

        void ReportBench(float[] cpuMs, float[] frameMs, int nInt, float[] gpuMs, int nGpu, int n, double secs, long allocMain, long allocAll, int g0, int g1, int g2, int maxBake)
        {
            if (n < 2 || nInt < 2) { Console.WriteLine("bench: no samples"); return; }
            var cl = new List<float>(n);
            for (int i = 0; i < n; i++) if (cpuMs[i] >= 0) cl.Add(cpuMs[i]);
            var c = cl.ToArray();
            var f = frameMs.AsSpan(0, nInt).ToArray();
            var g = gpuMs.AsSpan(0, nGpu).ToArray();
            Array.Sort(c); Array.Sort(f); Array.Sort(g);
            static float P(float[] a, double p) => a.Length == 0 ? 0 : a[Math.Min(a.Length - 1, (int)(a.Length * p))];
            // fps over the non-sample frames, excluding the time spent in isolated GPU samples
            double sampleSecs = 0;
            foreach (var x in g) sampleSecs += x / 1000.0;
            double fps = c.Length / Math.Max(0.001, secs - sampleSecs);
            using var proc = Process.GetCurrentProcess();
            double upMs = _pages.MainUploadCount > 0 ? _pages.MainUploadTicks * 1000.0 / Stopwatch.Frequency / _pages.MainUploadCount : 0;
            double rdMs = _pages.ReadStats.Count > 0 ? _pages.ReadStats.RunTicks * 1000.0 / Stopwatch.Frequency / _pages.ReadStats.Count : 0;
            int nr = Math.Max(1, (int)_pages.ReadStats.Count);
            double rdOnlyMs = _pages.ReadTicksSum * 1000.0 / Stopwatch.Frequency / nr, decMs = _pages.DecodeTicksSum * 1000.0 / Stopwatch.Frequency / nr;
            double fileMiB = _pages.FileBytesSum / 1048576.0 / nr;
            double expMs = _pages.ExpandTicksSum * 1000.0 / Stopwatch.Frequency / nr;
            double rgMs = _chunks.RegionStats.Count > 0 ? _chunks.RegionStats.RunTicks * 1000.0 / Stopwatch.Frequency / _chunks.RegionStats.Count : 0;
            string fmt = PageFormats.Name(_pack.Format) + (_pages.Expanding ? "->RGBA8" : "");
            string driver = SDL_GetGPUDeviceDriver(_r.Device);
            Console.WriteLine($"bench [{fmt}, {driver}, {_r.TargetW}x{_r.TargetH}{(_r.Offscreen ? " offscreen, fence-throttled (2 in flight)" : ", vsync off")}]: {n} frames in {secs:F1} s = {fps:F0} fps, {_o.Workers} workers, {_o.Layers} layers");
            Console.WriteLine($"  CPU frame ms   median {P(c, 0.5):F3}  p95 {P(c, 0.95):F3}  p99 {P(c, 0.99):F3}  max {(c.Length > 0 ? c[^1] : 0):F3}");
            Console.WriteLine($"  frame interval median {P(f, 0.5):F3}  p95 {P(f, 0.95):F3}  p99 {P(f, 0.99):F3}  max {f[^1]:F3}");
            if (nGpu > 0) Console.WriteLine($"  GPU frame ms (one frame alone on an idle GPU, fence-timed, every 30th frame, n={nGpu}) median {P(g, 0.5):F3}  p95 {P(g, 0.95):F3}  max {g[^1]:F3}; empty submit->fence overhead {_submitOverheadMs:F3} => net GPU median ~{Math.Max(0, P(g, 0.5) - _submitOverheadMs):F3}");
            Console.WriteLine($"  GC collections gen0 {g0} gen1 {g1} gen2 {g2}");
            Console.WriteLine($"  allocated: main thread {allocMain} B ({(double)allocMain / n:F1} B/frame); all threads {allocAll / 1024} KiB ({(double)allocAll / n:F0} B/frame, workers' region/page IO)");
            Console.WriteLine($"  pages: {_pages.TotalUploads} uploads, {_pages.Evictions} evictions, main-thread map+unmap+copy-record avg {upMs:F3} ms/page, worker read avg {rdMs:F2} ms/page; VRAM for pages {_pages.VramBytes >> 20} MiB ({_pages.Layers} x {_pages.ChainBytes / 1024} KiB)");
            Console.WriteLine($"  page files: {_pages.CompressedPages}/{_pages.ReadStats.Count} Brotli; avg file {fileMiB:F2} MiB; worker read {rdOnlyMs:F2} ms + decode {decMs:F2} ms + expand {expMs:F2} ms per page");
            Console.WriteLine($"  regions: worker read+decompress avg {rgMs:F2} ms; chunks resident {_chunks.ActiveChunks}; instance buffer {_chunks.Alloc.Used}/{_chunks.Alloc.Capacity}; max bakes in flight {maxBake}");
            Console.WriteLine($"  bakes: {_chunks.BakeStats}");
            Console.WriteLine($"  working set {proc.WorkingSet64 >> 20} MiB, private {proc.PrivateMemorySize64 >> 20} MiB, GC heap {GC.GetTotalMemory(false) >> 20} MiB");
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            Console.WriteLine(string.Format(ci,
                "RESULT format={0} size={1}x{2} fps={3:F0} cpu_med={4:F3} cpu_p95={5:F3} cpu_p99={6:F3} int_med={7:F3} int_p95={8:F3} int_p99={9:F3} gpu_med={10:F3} gpu_p95={11:F3} uploads={12} up_ms={13:F3} read_ms={14:F2} vram_mib={15} ws_mib={16} alloc_bpf={17:F1} gcs={18} region_ms={19:F2} gpu_net={20:F3} gpu_ovh={21:F3} page_file_mib={22:F2} page_read_ms={23:F2} page_dec_ms={24:F2} page_brotli={25} page_exp_ms={26:F2} driver={27}",
                fmt, _r.TargetW, _r.TargetH, fps, P(c, 0.5), P(c, 0.95), P(c, 0.99), P(f, 0.5), P(f, 0.95), P(f, 0.99), P(g, 0.5), P(g, 0.95),
                _pages.TotalUploads, upMs, rdMs, _pages.VramBytes >> 20, proc.WorkingSet64 >> 20, (double)allocMain / n, g0 + g1 + g2, rgMs, Math.Max(0, P(g, 0.5) - _submitOverheadMs), _submitOverheadMs, fileMiB, rdOnlyMs, decMs, _pages.CompressedPages, expMs, driver));
        }

        static void Interp(double t, ReadOnlySpan<double> kt, ReadOnlySpan<double> kx, ReadOnlySpan<double> ky, ReadOnlySpan<double> kz, out double x, out double y, out float zoom)
        {
            int i = 0;
            while (i < kt.Length - 2 && t > kt[i + 1]) i++;
            double a = Math.Clamp((t - kt[i]) / (kt[i + 1] - kt[i]), 0, 1);
            x = kx[i] + (kx[i + 1] - kx[i]) * a;
            y = ky[i] + (ky[i + 1] - ky[i]) * a;
            zoom = (float)Math.Exp(Math.Log(kz[i]) + (Math.Log(kz[i + 1]) - Math.Log(kz[i])) * a);
        }

        /// <summary>Scripted ~20 s flight: Britain, north, west over the mountains zoomed out, south, zoomed in, back.</summary>
        static void BenchPath(double t, out double x, out double y, out float zoom)
            => Interp(t, [0, 4, 8, 12, 16, 20], [1495, 1450, 1100, 1000, 1300, 1495], [1629, 1300, 1250, 1600, 1750, 1629], [1.0, 1.0, 0.5, 0.5, 2.0, 1.0], out x, out y, out zoom);

        /// <summary>--shots route: western mountains, fly into Britain at zoom 1, pan at 0.5, then zoom 2.</summary>
        static void ShotsPath(double t, out double x, out double y, out float zoom)
            => Interp(t, [0, 6, 9, 14, 18, 24], [900, 1495, 1495, 1440, 1440, 1520], [1500, 1629, 1629, 1700, 1700, 1600], [1.0, 1.0, 0.5, 0.5, 2.0, 2.0], out x, out y, out zoom);

        void UpdateTitle(double fps, double cpuMs, int instances, int draws, int pages, int uploads, int chunks, int chunkUploads)
        {
            Span<byte> b = _title;
            int p = 0;
            Append(b, ref p, "Uo.Render | "u8);
            AppendNum(b, ref p, fps, 0); Append(b, ref p, " fps | cpu "u8);
            AppendNum(b, ref p, cpuMs, 2); Append(b, ref p, " ms | inst "u8);
            AppendNum(b, ref p, instances, 0); Append(b, ref p, " | draws "u8);
            AppendNum(b, ref p, draws, 0); Append(b, ref p, " | pages "u8);
            AppendNum(b, ref p, pages, 0); Append(b, ref p, "/"u8);
            AppendNum(b, ref p, _pages.Layers, 0); Append(b, ref p, " | uploads "u8);
            AppendNum(b, ref p, uploads, 0); Append(b, ref p, "p+"u8);
            AppendNum(b, ref p, chunkUploads, 0); Append(b, ref p, "c | chunks "u8);
            AppendNum(b, ref p, chunks, 0); Append(b, ref p, " | zoom "u8);
            AppendNum(b, ref p, _zoom, 2); Append(b, ref p, _r.VSync ? " | vsync"u8 : " | no-vsync"u8);
            b[p] = 0;
            fixed (byte* t = _title) Native.SDL_SetWindowTitle(_window, t);
        }

        static void Append(Span<byte> b, ref int p, ReadOnlySpan<byte> s) { s.CopyTo(b[p..]); p += s.Length; }

        static void AppendNum(Span<byte> b, ref int p, double v, int decimals)
        {
            if (decimals == 0) { ((long)Math.Round(v)).TryFormat(b[p..], out int w); p += w; }
            else { v.TryFormat(b[p..], out int w, decimals == 2 ? "F2" : "F1"); p += w; }
        }
    }
}
