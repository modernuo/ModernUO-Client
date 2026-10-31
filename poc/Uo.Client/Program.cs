using System.Globalization;
using Uo.Net;
using Uo.Render;
using static SDL3.SDL;

namespace Uo.Client;

public sealed class Options
{
    public string Pack = @"C:\Repositories\uo-packs\classic-v1-16";
    public bool Live;
    public string Host = "127.0.0.1";
    public int Port = 2593;
    public string Character;
    public string Go;                   // live: "[go x y" after entering the world
    public string WalkPattern;          // scripted input, e.g. "E5 S5 N5 W5"
    public bool Run, Loop;
    public int Mobiles = 12, Items = 24, Radius = 12;
    public double LatencyMs = 60;
    public string ServerBlock;          // offline: "x y" tile the fake server treats as impassable (forces a reject)
    public float StartX = 1495, StartY = 1629, Zoom = 1;
    public int Width = 1600, Height = 900;
    public string ShotsDir;
    public bool Bench;
    public float BenchSeconds = 15;
    public bool Test;
    public int Layers = 24, PageBudget = 2, ChunkBudget = 64;
    public int InstanceCapacity = 2 << 20;
    public int Workers = Math.Clamp(Environment.ProcessorCount / 2 - 1, 2, 8);
    public string Driver = "direct3d12";
    public bool ForceExpand;
    public string AnimPack;             // TEMPORARY: anims from another pack until classic-1.0 carries both
    public string TileDataMul;          // TEMPORARY: AnimID source until the pack's tiledata has it
    public int AnimLayers = 64;
    public string AnimSheet;            // offline: bodies x 8 directions, standing and walking, screenshots into DIR
    public string BodyMix = "mixed";    // offline NPC bodies: humans | mixed

    public const string Usage =
        "Uo.Client [--pack DIR] [--live [--host H] [--port P] [--char NAME] [--go \"x y\"]]  (live credentials: env UO_USER / UO_PASS)\n" +
        "          [--mobiles N] [--items N] [--radius R] [--latency MS] [--server-block \"x y\"]   (offline fake server)\n" +
        "          [--at x y] [--zoom Z] [--size WxH] [--walk \"E5 S5\"] [--run] [--loop]\n" +
        "          [--screenshots DIR] | [--bench [--bench-seconds S]] | [--test]\n" +
        "          [--driver direct3d12|vulkan] [--force-expand] [--anim-pack DIR] [--anim-layers N] [--tiledata-mul PATH]\n" +
        "          [--npc-bodies humans|mixed] | [--anim-sheet DIR]";

    public static Options Parse(string[] args)
    {
        var o = new Options();
        var ci = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value for " + args[i]);
            switch (args[i])
            {
                case "--pack": o.Pack = Next(); break;
                case "--live": o.Live = true; break;
                case "--host": o.Host = Next(); break;
                case "--port": o.Port = int.Parse(Next(), ci); break;
                case "--char": o.Character = Next(); break;
                case "--go": o.Go = Next(); break;
                case "--walk": o.WalkPattern = Next(); break;
                case "--run": o.Run = true; break;
                case "--loop": o.Loop = true; break;
                case "--mobiles": o.Mobiles = int.Parse(Next(), ci); break;
                case "--items": o.Items = int.Parse(Next(), ci); break;
                case "--radius": o.Radius = int.Parse(Next(), ci); break;
                case "--latency": o.LatencyMs = double.Parse(Next(), ci); break;
                case "--server-block": o.ServerBlock = Next(); break;
                case "--at": o.StartX = float.Parse(Next(), ci); o.StartY = float.Parse(Next(), ci); break;
                case "--zoom": o.Zoom = float.Parse(Next(), ci); break;
                case "--size":
                    var wh = Next().Split('x');
                    o.Width = int.Parse(wh[0], ci); o.Height = int.Parse(wh[1], ci);
                    break;
                case "--screenshots": o.ShotsDir = Next(); break;
                case "--bench": o.Bench = true; break;
                case "--bench-seconds": o.BenchSeconds = float.Parse(Next(), ci); break;
                case "--test": o.Test = true; break;
                case "--workers": o.Workers = int.Parse(Next(), ci); break;
                case "--layers": o.Layers = int.Parse(Next(), ci); break;
                case "--driver": o.Driver = Next(); break;
                case "--force-expand": o.ForceExpand = true; break;
                case "--anim-pack": o.AnimPack = Next(); break;
                case "--anim-layers": o.AnimLayers = int.Parse(Next(), ci); break;
                case "--tiledata-mul": o.TileDataMul = Next(); break;
                case "--anim-sheet": o.AnimSheet = Next(); break;
                case "--npc-bodies": o.BodyMix = Next(); break;
                default: return null;
            }
        }
        return o;
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        Options o;
        try { o = Options.Parse(args); }
        catch (Exception e) { Console.Error.WriteLine(e.Message); o = null; }
        if (o == null) { Console.Error.WriteLine(Options.Usage); return 2; }

        Console.WriteLine($"pack: {o.Pack}");
        var pack = new PackSet(o.Pack, 0, o.AnimPack);
        Console.WriteLine($"  {pack.PageCount} pages {PageFormats.Name(pack.Format)}, {pack.Sprites.Length} sprites, {pack.HueCount} hues, map {pack.MapWidth}x{pack.MapHeight}");
        Console.WriteLine(pack.HasAnims
            ? $"  anims {pack.AnimDir}: {pack.AnimBodies.Length} bodies, {pack.AnimFrames.Length} frames, {(pack.AnimIndexed ? pack.AnimPagePaths.Length : pack.PageCount - pack.AnimPageBase)} pages {PageFormats.Name(pack.AnimFormat)} {pack.AnimPageSize}px" +
              (pack.AnimIndexed ? $" (own R8_UINT array, {pack.PaletteCount} palettes)" : " (in the art array)")
            : "  no anims: mobiles are not drawn");
        EcsSchema.Register();

        if (o.Test) return PredictionTests.Run(pack);
        if (o.AnimSheet != null) return AnimSheet.Run(o, pack);
        return RunClient(o, pack);
    }

    /// <summary>Indexed8 anims get their own R8_UINT page array (1 MiB per 1024² layer); 16-bit anims use the art array.</summary>
    public static PageResidency CreateAnimResidency(Renderer renderer, PackSet pack, WorkerPool workers, Options o)
        => pack.AnimIndexed
            ? new PageResidency(renderer.Device, pack.AnimPagePaths, pack.AnimFormat, pack.AnimPageSize, pack.AnimPageMips, workers, o.AnimLayers, Math.Max(4, o.PageBudget * 4), Math.Max(4, o.PageBudget * 4))
            : null;

    public static IMobileSprites CreateMobileSprites(PackSet pack, Options o)
    {
        var ids = o.TileDataMul == null ? PackAnimIds.TryLoad(pack.Root) : null;
        string src = "pack tiledata_ext.bin";
        if (ids == null) { ids = TileDataMulAnimIds.TryDefault(o.TileDataMul); src = "FALLBACK tiledata.mul read (pack has no tiledata_ext.bin)"; }
        var convPath = Path.Combine(pack.Root, Uo.Pack.TableConst.EquipConvFile);
        var conv = File.Exists(convPath) ? Uo.Pack.EquipConvFile.Read(convPath) : null;
        Console.WriteLine(ids != null ? $"  equipment AnimIDs: {src}; equipconv {(conv != null ? conv.Length + " rows" : "none")}" : "  equipment AnimIDs: none: equipment is not drawn");
        return pack.HasAnims ? new AnimMobileSprites(pack, ids, conv) : new NoMobileSprites();
    }

    static int RunClient(Options o, PackSet pack)
    {
        var clock = new Clock();
        clock.Tick();
        var map = new WalkMap(pack);
        var world = new EcsWorld(clock);
        map.Dynamic = world;

        IGameClient client;
        if (o.Live)
        {
            var user = Environment.GetEnvironmentVariable("UO_USER");
            var pass = Environment.GetEnvironmentVariable("UO_PASS");
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                Console.Error.WriteLine("--live needs UO_USER and UO_PASS in the environment");
                return 2;
            }
            var cc = new ClassicClient(world);
            cc.Connect(new LoginOptions { Host = o.Host, Port = o.Port, Username = user, Password = pass, CharacterName = o.Character });
            client = cc;
        }
        else
        {
            var fake = new FakeServer(world, map, clock, new FakeServer.Scenario { X = (int)o.StartX, Y = (int)o.StartY, Mobiles = o.Mobiles, Items = o.Items, Radius = o.Radius, MixedBodies = o.BodyMix != "humans" }, o.LatencyMs);
            if (o.ServerBlock != null)
            {
                var p = o.ServerBlock.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                fake.ServerBlocked.Add(int.Parse(p[1]) * 65536 + int.Parse(p[0]));
            }
            fake.Connect(null);
            client = fake;
            Console.WriteLine($"offline: fake server, {o.Mobiles} mobiles, {o.Items} items around {o.StartX},{o.StartY}, latency {o.LatencyMs} ms");
        }
        var walker = new Walker(client, map, world, clock);

        SDL_SetHint("SDL_GPU_DRIVER", o.Driver);
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO)) { Console.Error.WriteLine(SDL_GetError()); return 1; }
        bool offscreen = o.ShotsDir != null || o.Bench;
        if (o.ShotsDir != null) Directory.CreateDirectory(o.ShotsDir);
        var flags = SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY;
        if (offscreen) flags |= SDL_WindowFlags.SDL_WINDOW_HIDDEN;
        var window = SDL_CreateWindow("Uo.Client", o.Width, o.Height, flags);
        if (window == IntPtr.Zero) { Console.Error.WriteLine(SDL_GetError()); return 1; }

        int rc;
        using (var workers = new WorkerPool(o.Workers))
        using (var renderer = new Renderer(window, offscreen, pack, o.InstanceCapacity, 8192, o.Driver))
        {
            var residency = new PageResidency(renderer.Device, pack, workers, o.Layers, o.PageBudget, Math.Max(2, o.PageBudget * 2), o.ForceExpand);
            var animPages = CreateAnimResidency(renderer, pack, workers, o);
            Console.WriteLine($"  device: {SDL_GetGPUDeviceDriver(renderer.Device)}, shaders {renderer.ShaderFormat}, pages {residency.FormatDescription}" +
                (animPages != null ? $", anim pages {animPages.FormatDescription} x {animPages.Layers} layers ({animPages.VramBytes >> 20} MiB)" : ""));
            var chunks = new ChunkStore(pack, workers, o.InstanceCapacity);
            if (o.Bench) renderer.VSync = false;
            renderer.ApplyPresentMode();
            if (offscreen) renderer.EnsureOffscreen((uint)o.Width, (uint)o.Height);
            renderer.Throttle = offscreen;
            var app = new ClientApp(o, pack, window, renderer, residency, animPages, chunks, workers, client, world, walker, map, clock);
            rc = app.Loop();
            client.Disconnect();
            residency.Dispose();
            animPages?.Dispose();
        }
        client.Dispose();
        return rc;
    }
}
