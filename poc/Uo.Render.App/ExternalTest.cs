using static SDL3.SDL;

namespace Uo.Render;

/// <summary><c>--external-test</c>: proves external device mode without ClassicUO. The harness plays FNA: it owns the
/// device, the window/swapchain and a separate render target; the world renders into that target through
/// <see cref="WorldRenderer.CreateExternal"/>, and the harness blits the target to its swapchain on its own command
/// buffer, acquired before the world renders and submitted after (FNA's frame order). Phases are compared pixel for pixel
/// with offscreen (normal) mode at the same camera: plain CLEAR, a target recreated at a new size mid-run, LOAD over a
/// host-drawn layer with a viewport, a BGRA8 target, and overrides plus dynamic sprites (diffs must stay inside the
/// affected sprites' rectangles).</summary>
static unsafe class ExternalTest
{
    static readonly SDL_FColor Magenta = new() { r = 1, g = 0, b = 1, a = 1 };
    const SDL_GPUTextureFormat Rgba = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM;
    const SDL_GPUTextureFormat Bgra = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM;

    public static int Run(Program.Options o)
    {
        string dir = o.ShotsDir ?? Path.Combine(Path.GetTempPath(), "uo-external-test");
        Directory.CreateDirectory(dir);
        (int W, int H) a = (o.Width, o.Height), b = (1280, 720), c = (1920, 1080), v = (960, 540);
        var refA = Reference(o, a, dir);
        var refB = Reference(o, b, dir);
        var refV = Reference(o, v, dir);
        if (refA == null || refB == null || refV == null) { Console.WriteLine("external-test: reference screenshot failed"); return 1; }

        SDL_SetHint("SDL_GPU_DRIVER", o.Driver);
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO)) { Console.Error.WriteLine(SDL_GetError()); return 1; }
        var window = SDL_CreateWindow("Uo.Render external-test", 1280, 720, SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
        var device = SDL_CreateGPUDevice(SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV | SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL, false, o.Driver);
        if (window == IntPtr.Zero || device == IntPtr.Zero || !SDL_ClaimWindowForGPUDevice(device, window))
        {
            Console.Error.WriteLine("external-test: host device/window: " + SDL_GetError());
            return 1;
        }

        int failures = 0;
        var pack = new PackSet(o.Pack, 0);
        var host = new Host(device, window);
        using (var world = WorldRenderer.CreateExternal(device, Rgba, pack, Program.WorldOptions(o)))
        {
            Console.WriteLine($"external-test: host device {SDL_GetGPUDeviceDriver(device)}, shader formats {SDL_GetGPUShaderFormats(device)}; world picked {world.Renderer.ShaderFormat}, mode {world.Renderer.Mode}");
            var cam = new WorldCamera { CenterX = (o.StartX - o.StartY) * 22, CenterY = (o.StartX + o.StartY) * 22, Zoom = o.StartZoom * o.Density };

            var px = RunPhase(world, host, cam, a, Rgba, false, null, default, null, Path.Combine(dir, "ext_clear.png"));
            failures += Check("clear (CLEAR, RGBA8)", Diff(px, refA, a.W, a.H, null));

            int depth0 = world.Renderer.DepthRecreations, rt0 = host.Recreations;
            px = RunPhase(world, host, cam, b, Rgba, false, c, default, null, Path.Combine(dir, "ext_resize.png"));
            failures += Check($"resize ({c.W}x{c.H} -> {b.W}x{b.H} mid-run; target recreated {host.Recreations - rt0}x, depth {world.Renderer.DepthRecreations - depth0}x)", Diff(px, refB, b.W, b.H, null));

            var vp = (X: 160, Y: 90, v.W, v.H);
            px = RunPhase(world, host, cam, b, Rgba, true, null, vp, null, Path.Combine(dir, "ext_load.png"));
            var dl = DiffViewport(px, b.W, b.H, vp, refV);
            failures += Check($"load (LOAD over a host-cleared magenta layer: magenta kept outside the viewport {vp.X},{vp.Y} {v.W}x{v.H})", dl.Outside, dl.Pixels - (long)v.W * v.H);
            Console.WriteLine($"       inside the offset viewport vs a {v.W}x{v.H} reference: {dl.Inside} pixels differ, max delta {dl.MaxDelta} " +
                $"({(dl.MaxDelta <= 1 ? "ok: interpolation precision of the shifted origin" : "FAIL")})");
            if (dl.MaxDelta > 1) failures++;

            px = RunPhase(world, host, cam, a, Bgra, false, null, default, null, Path.Combine(dir, "ext_bgra.png"));
            failures += Check("bgra (CLEAR, B8G8R8A8 target, second pipeline set)", Diff(px, refA, a.W, a.H, null));

            var scene = new OverrideScene();
            px = RunPhase(world, host, cam, a, Rgba, false, null, default, scene.Apply, Path.Combine(dir, "ext_overrides.png"));
            Console.WriteLine($"  overrides scene: {scene.Statics.Count} statics + {scene.Lands.Count} land overridden, {scene.Dynamic} dynamic; " +
                $"last frame: {world.OverridesApplied} applied, {world.OverridesMissed} missed, {world.OverridesDropped} dropped, {world.TranslucentCount} translucent, {world.Renderer.DrawCalls} draws");
            var d = Diff(px, refA, a.W, a.H, scene.Rects);
            failures += Check("overrides (diffs only inside overridden/dynamic sprite rects)", d, expectInside: true);
            if (scene.Probe.Width > 0)
                Console.WriteLine($"  debug hook: static 0x{scene.ProbeGraphic:X4} at {scene.ProbeX},{scene.ProbeY} -> instance {scene.Probe.InstanceIndex}, page {scene.Probe.Page} uv {scene.Probe.U},{scene.Probe.V} " +
                    $"{scene.Probe.Width}x{scene.Probe.Height}, screen {scene.Probe.ScreenX},{scene.Probe.ScreenY} {scene.Probe.ScreenWidth}x{scene.Probe.ScreenHeight}");
        }
        host.Dispose();
        SDL_ReleaseWindowFromGPUDevice(device, window);
        SDL_DestroyGPUDevice(device);
        SDL_DestroyWindow(window);
        Console.WriteLine(failures == 0 ? $"external-test [{o.Driver}]: PASS (shots in {dir})" : $"external-test [{o.Driver}]: {failures} FAILED (shots in {dir})");
        return failures == 0 ? 0 : 1;
    }

    static byte[] Reference(Program.Options o, (int W, int H) size, string dir)
    {
        var r = o.Clone();
        r.Width = size.W; r.Height = size.H;
        r.Screenshot = Path.Combine(dir, $"normal_{size.W}x{size.H}.png");
        r.Bench = false; r.Offscreen = false; r.ShotsDir = null;
        Program.LastShot = null;
        return Program.Run(r) == 0 ? Program.LastShot : null;
    }

    /// <summary>Runs frames until the world is settled for 3 frames (the same rule as --screenshot), then saves the target.
    /// <paramref name="via"/>: the target starts at this size for 10 frames, then is recreated at <paramref name="size"/>.</summary>
    static byte[] RunPhase(WorldRenderer world, Host host, WorldCamera cam, (int W, int H) size, SDL_GPUTextureFormat format, bool load,
        (int W, int H)? via, (int X, int Y, int W, int H) viewport, Action<WorldRenderer> perFrame, string path)
    {
        long deadline = Environment.TickCount64 + 60_000;
        int settled = 0;
        for (int frame = 1; ; frame++)
        {
            while (SDL_PollEvent(out _)) { }
            var s = via != null && frame <= 10 ? via.Value : size;
            host.EnsureTarget((uint)s.W, (uint)s.H, format);
            var c = cam;
            if (viewport.W > 0) { c.ViewportX = viewport.X; c.ViewportY = viewport.Y; c.ViewportWidth = viewport.W; c.ViewportHeight = viewport.H; }
            else { c.ViewportWidth = s.W; c.ViewportHeight = s.H; }

            var frameCmd = SDL_AcquireGPUCommandBuffer(host.Device); // the host's frame (FNA: acquired at frame start)
            if (load) host.Fill(Magenta);                              // a host layer under the world: submitted first
            world.Prepare(in c);
            settled = world.Settled && frame > 3 && (via == null || frame > 12) ? settled + 1 : 0;
            bool shot = settled >= 3 || Environment.TickCount64 > deadline;
            perFrame?.Invoke(world);
            world.Render(host.Target, host.W, host.H, host.Format, load);
            byte[] px = null;
            if (shot)
            {
                if (!world.Settled) Console.WriteLine("  phase did not settle before the deadline");
                world.Renderer.SaveTexture(host.Target, host.W, host.H, host.Format, path);
                px = world.Renderer.LastScreenshot;
            }
            host.Present(frameCmd);
            if (shot) return px;
        }
    }

    struct DiffResult
    {
        public long Pixels, Differing, Inside, Outside;
        public int MaxDelta;
    }

    static DiffResult Diff(byte[] a, byte[] b, int w, int h, List<(int X0, int Y0, int X1, int Y1)> rects)
    {
        var r = new DiffResult { Pixels = (long)w * h };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4, md = 0;
                for (int k = 0; k < 4; k++) md = Math.Max(md, Math.Abs(a[i + k] - b[i + k]));
                if (md == 0) continue;
                r.Differing++;
                r.MaxDelta = Math.Max(r.MaxDelta, md);
                if (rects == null) continue;
                bool inside = false;
                foreach (var q in rects) if (x >= q.X0 && x < q.X1 && y >= q.Y0 && y < q.Y1) { inside = true; break; }
                if (inside) r.Inside++; else r.Outside++;
            }
        return r;
    }

    /// <summary>Outside the viewport the host's magenta must survive (LOAD): <see cref="DiffResult.Outside"/>. Inside, the
    /// world must match a reference rendered at the viewport's size: <see cref="DiffResult.Inside"/> and MaxDelta.</summary>
    static DiffResult DiffViewport(byte[] px, int w, int h, (int X, int Y, int W, int H) vp, byte[] reference)
    {
        var r = new DiffResult { Pixels = (long)w * h };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool inVp = x >= vp.X && x < vp.X + vp.W && y >= vp.Y && y < vp.Y + vp.H;
                int md = 0;
                if (inVp)
                {
                    int j = ((y - vp.Y) * vp.W + (x - vp.X)) * 4;
                    for (int k = 0; k < 4; k++) md = Math.Max(md, Math.Abs(px[i + k] - reference[j + k]));
                }
                else
                {
                    md = Math.Max(Math.Max(Math.Abs(px[i] - 255), px[i + 1]), Math.Max(Math.Abs(px[i + 2] - 255), Math.Abs(px[i + 3] - 255)));
                }
                if (md == 0) continue;
                r.Differing++;
                if (inVp) { r.Inside++; r.MaxDelta = Math.Max(r.MaxDelta, md); } else r.Outside++;
            }
        return r;
    }

    static int Check(string name, long bad, long total)
    {
        Console.WriteLine($"  {(bad == 0 ? "ok  " : "FAIL")} {name}: {bad} of {total} pixels differ");
        return bad == 0 ? 0 : 1;
    }

    static int Check(string name, DiffResult d, bool expectInside = false)
    {
        bool ok = expectInside ? d.Outside == 0 && d.Inside > 0 : d.Differing == 0;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}: {d.Differing} of {d.Pixels} pixels differ (max channel delta {d.MaxDelta})" +
            (expectInside ? $", {d.Inside} inside the expected rects, {d.Outside} outside" : ""));
        return ok ? 0 : 1;
    }

    /// <summary>Hides, fades and highlights statics and land tiles near the camera, and adds dynamic sprites in every mode.
    /// Collects the screen rectangles where pixels may change.</summary>
    sealed class OverrideScene
    {
        public readonly List<(int X, int Y, int Z, ushort G)> Statics = new();
        public readonly List<(int X, int Y)> Lands = new();
        public readonly List<(int X0, int Y0, int X1, int Y1)> Rects = new();
        public int Dynamic;
        public SpriteDebugInfo Probe;
        public int ProbeX, ProbeY, ProbeGraphic;
        ushort _dynGraphic;
        int _dynArea, _cx, _cy;

        public void Apply(WorldRenderer w)
        {
            if (Statics.Count == 0 && !Collect(w)) return;
            Rects.Clear();
            for (int i = 0; i < Statics.Count; i++)
            {
                var (x, y, z, g) = Statics[i];
                switch (i % 3)
                {
                    case 0: w.SetStaticOverride(x, y, z, g, hidden: true); break;
                    case 1: w.SetStaticOverride(x, y, z, g, alpha: 110); break;
                    default: w.SetStaticOverride(x, y, z, g, highlightHue: 0x35); break;
                }
                AddRects(w, x, y, true, z, g);
            }
            for (int i = 0; i < Lands.Count; i++)
            {
                var (x, y) = Lands[i];
                if (i % 2 == 0) w.SetLandOverride(x, y, hidden: true); else w.SetLandOverride(x, y, highlightHue: 0x21);
                AddRects(w, x, y, false, 0, 0);
            }

            // dynamic sprites in a row south-east of the camera: opaque, alpha, spectral, shadow, mirrored + hued, effect-hued
            var sp = w.Pack.Sprites[Uo.Pack.PackConst.StaticSpriteBase + _dynGraphic];
            for (int k = 0; k < 6; k++)
            {
                int tx = _cx + 1 + 2 * k, ty = _cy + 3 - k;
                int ax = (tx - ty) * 22 - 22, ay = (tx + ty) * 22 - 22;
                ref var d = ref w.AddDynamic();
                d.X = ax - ((sp.OrigW >> 1) - 22) + sp.TrimX;
                d.Y = ay - (sp.OrigH - 44) + sp.TrimY;
                d.Whf = SpriteInstance.PackWhf(sp.W, sp.H, 0);
                d.Uv = sp.X | ((uint)sp.Y << 16);
                d.PageHue = sp.Page;
                d.Depth = BakeJob.DepthKey(tx, ty, 20, 7);
                switch (k)
                {
                    case 1: d.Alpha = 120; break;
                    case 2: d.Mode = HueMode.Spectral; break;
                    case 3: d.Mode = HueMode.Shadow; break;
                    case 4: d.SetMirror(true); d.SetHighlight(0x26); break;
                    case 5: d.Mode = HueMode.EffectHued; d.SetHighlight(0x5A); break;
                }
                var r = w.Describe(d);
                Rects.Add((r.ScreenX - 1, r.ScreenY - 1, r.ScreenX + r.ScreenWidth + 1, r.ScreenY + r.ScreenHeight + 1));
            }
            Dynamic = 6;
        }

        void AddRects(WorldRenderer w, int x, int y, bool isStatic, int z, int g)
        {
            Span<int> locals = stackalloc int[8];
            int n = w.Chunks.FindObject(x, y, isStatic, z, g, out int slot, locals);
            for (int k = 0; k < n; k++)
            {
                var r = w.Describe(w.Chunks.CpuInstances(slot)[locals[k]]);
                Rects.Add((r.ScreenX - 1, r.ScreenY - 1, r.ScreenX + r.ScreenWidth + 1, r.ScreenY + r.ScreenHeight + 1));
            }
        }

        bool Collect(WorldRenderer w)
        {
            // camera tile from the view centre
            var v = w.View;
            double wx = (v.X0 + v.X1) * 0.5, wy = (v.Y0 + v.Y1) * 0.5;
            _cx = (int)Math.Round((wx + wy) / 44); _cy = (int)Math.Round((wy - wx) / 44);
            for (int by = (_cy >> 3) - 1; by <= (_cy >> 3) + 1; by++)
                for (int bx = (_cx >> 3) - 1; bx <= (_cx >> 3) + 1; bx++)
                    if (!w.Chunks.TryGetReadySlot(bx, by, out _)) return false;
            for (int by = (_cy >> 3) - 1; by <= (_cy >> 3) + 1; by++)
                for (int bx = (_cx >> 3) - 1; bx <= (_cx >> 3) + 1; bx++)
                {
                    w.Chunks.TryGetReadySlot(bx, by, out int slot);
                    var keys = w.Chunks.ObjectKeys(slot);
                    for (int i = 0; i < keys.Length; i++)
                    {
                        uint k = keys[i];
                        int cell = (int)(k >> 25), x = bx * 8 + (cell & 7), y = by * 8 + (cell >> 3);
                        if ((k >> 24 & 1) == 0)
                        {
                            if (Lands.Count < 6 && (x + y) % 5 == 0) Lands.Add((x, y));
                            continue;
                        }
                        ushort g = (ushort)k;
                        if (Statics.Count < 30 && i % 4 == 0) Statics.Add((x, y, (sbyte)(k >> 16), g));
                        var sp = w.Pack.Sprites[Uo.Pack.PackConst.StaticSpriteBase + g];
                        if (sp.Present && sp.H <= 160 && sp.W * sp.H > _dynArea) { _dynGraphic = g; _dynArea = sp.W * sp.H; }
                    }
                }
            if (Statics.Count > 0 && w.TryGetObjectInfo(Statics[0].X, Statics[0].Y, true, Statics[0].Z, Statics[0].G, out Probe))
            {
                ProbeX = Statics[0].X; ProbeY = Statics[0].Y; ProbeGraphic = Statics[0].G;
            }
            return Statics.Count > 0;
        }
    }

    /// <summary>The host (FNA's part): owns the render target, recreated on resize like ClassicUO's RenderTargets.EnsureSize,
    /// and presents it through its own command buffer.</summary>
    sealed class Host : IDisposable
    {
        public readonly IntPtr Device, Window;
        public IntPtr Target;
        public uint W, H;
        public SDL_GPUTextureFormat Format;
        public int Recreations;

        public Host(IntPtr device, IntPtr window) { Device = device; Window = window; }

        public void EnsureTarget(uint w, uint h, SDL_GPUTextureFormat format)
        {
            if (Target != IntPtr.Zero && W == w && H == h && Format == format) return;
            if (Target != IntPtr.Zero) { SDL_ReleaseGPUTexture(Device, Target); Recreations++; }
            var ci = new SDL_GPUTextureCreateInfo
            {
                type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D, format = format,
                usage = SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER,
                width = w, height = h, layer_count_or_depth = 1, num_levels = 1,
            };
            Target = SDL_CreateGPUTexture(Device, in ci);
            if (Target == IntPtr.Zero) throw new Exception("host target: " + SDL_GetError());
            W = w; H = h; Format = format;
        }

        public void Fill(SDL_FColor color)
        {
            var cmd = SDL_AcquireGPUCommandBuffer(Device);
            var ct = new SDL_GPUColorTargetInfo { texture = Target, clear_color = color, load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR, store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE };
            var pass = Native.SDL_BeginGPURenderPass(cmd, &ct, 1, null);
            SDL_EndGPURenderPass(pass);
            SDL_SubmitGPUCommandBuffer(cmd);
        }

        public void Present(IntPtr cmd)
        {
            if (SDL_WaitAndAcquireGPUSwapchainTexture(cmd, Window, out var sw, out uint sw_w, out uint sw_h) && sw != IntPtr.Zero)
            {
                var blit = new SDL_GPUBlitInfo
                {
                    source = new SDL_GPUBlitRegion { texture = Target, w = W, h = H },
                    destination = new SDL_GPUBlitRegion { texture = sw, w = sw_w, h = sw_h },
                    load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_DONT_CARE,
                    filter = SDL_GPUFilter.SDL_GPU_FILTER_LINEAR,
                };
                SDL_BlitGPUTexture(cmd, in blit);
            }
            SDL_SubmitGPUCommandBuffer(cmd);
        }

        public void Dispose()
        {
            SDL_WaitForGPUIdle(Device);
            if (Target != IntPtr.Zero) SDL_ReleaseGPUTexture(Device, Target);
        }
    }
}
