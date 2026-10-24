using System.Diagnostics;
using Uo.Net;
using Uo.Render;
using static SDL3.SDL;

namespace Uo.Client;

/// <summary>The vertical slice frame loop. Order per frame, all on the main thread:
/// input → <see cref="IGameClient.Poll"/> (sink writes the ECS) → <see cref="Walker.Update"/> (prediction, move requests)
/// → camera follows the predicted player → map streaming (worker results, chunk bake/evict) → static draw list →
/// <see cref="RenderExtract"/> (ECS → dynamic instances) → one depth-tested draw.</summary>
public sealed unsafe class ClientApp
{
    public const int MaxDynamic = 32768;

    readonly Options _o;
    readonly PackSet _pack;
    readonly IntPtr _window;
    readonly Renderer _r;
    readonly PageResidency _pages;
    readonly ChunkStore _chunks;
    readonly WorkerPool _workers;
    readonly IGameClient _client;
    readonly EcsWorld _world;
    readonly Walker _walker;
    readonly RenderExtract _extract;
    readonly Clock _clock;
    readonly WalkMap _map;
    readonly ChunkDraw[] _table = new ChunkDraw[8192];
    readonly uint _dynFirst;
    readonly WalkScript _script;
    readonly byte[] _title = new byte[512];

    double _camX, _camY;
    float _zoom;
    bool _mouseHeld;
    float _mouseX, _mouseY;
    bool _goSent;
    long _goSentAt;

    public ClientApp(Options o, PackSet pack, IntPtr window, Renderer r, PageResidency pages, ChunkStore chunks, WorkerPool workers,
        IGameClient client, EcsWorld world, Walker walker, WalkMap map, Clock clock)
    {
        _map = map;
        _o = o; _pack = pack; _window = window; _r = r; _pages = pages; _chunks = chunks; _workers = workers;
        _client = client; _world = world; _walker = walker; _clock = clock;
        _zoom = o.Zoom;
        _dynFirst = (uint)chunks.Alloc.Alloc(MaxDynamic); // reserved before any chunk: never fails
        _extract = new RenderExtract(world.Store, pack, new PlaceholderMobileSprites(pack), MaxDynamic);
        _script = o.WalkPattern != null ? new WalkScript(o.WalkPattern, o.Run, o.Loop) : null;
        SetCameraTile(o.StartX, o.StartY);
    }

    void SetCameraTile(double tx, double ty)
    {
        _camX = (tx - ty) * 22;
        _camY = (tx + ty) * 22;
    }

    enum ShotPhase { Login, Before, Walking, Settle, Done }

    public int Loop()
    {
        double freq = Stopwatch.Frequency;
        long frame = 0;
        long last = Stopwatch.GetTimestamp(), titleAt = last;
        int framesSinceTitle = 0;
        double cpuAccum = 0;
        bool running = true;

        // bench
        const int maxSamples = 1 << 22;
        var cpuMs = _o.Bench ? new float[maxSamples] : null;
        var extractMs = _o.Bench ? new float[maxSamples] : null;
        var ecsMs = _o.Bench ? new float[maxSamples] : null;
        bool benchRunning = false;
        long benchStart = 0, allocStart = 0, benchWarm = 0;
        int gc0 = 0, gc1 = 0, gc2 = 0, benchFrames = 0;
        long dynAccum = 0;

        // screenshots
        var phase = ShotPhase.Login;
        int settled = 0;
        long deadline = last + (long)(freq * 120);
        bool midTaken = false;

        while (running)
        {
            long t0 = Stopwatch.GetTimestamp();
            double dt = Math.Min(0.1, (t0 - last) / freq);
            last = t0;
            frame++;
            _clock.Tick();

            // ---- input
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
                        _zoom = Math.Clamp(_zoom * MathF.Pow(1.15f, ev.wheel.y), 0.5f, 4f);
                        break;
                    case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN: _mouseHeld = true; _mouseX = ev.button.x; _mouseY = ev.button.y; break;
                    case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP: _mouseHeld = false; break;
                    case SDL_EventType.SDL_EVENT_MOUSE_MOTION: _mouseX = ev.motion.x; _mouseY = ev.motion.y; break;
                }
            }

            int want = -1;
            bool run = _o.Run;
            if (_script != null && (phase == ShotPhase.Walking || _o.ShotsDir == null))
                want = _script.Next(_walker, _clock.Now, out run);
            else if (_o.ShotsDir == null && !_o.Bench)
                want = ReadInput(ref run);

            // ---- network → ECS, then prediction
            long te = Stopwatch.GetTimestamp();
            _client.Poll(0);
            if (_o.Go != null && !_goSent && _client.State == ClientState.InWorld && _world.InWorld)
            {
                _client.Say("[go " + _o.Go);
                _goSent = true;
                _goSentAt = t0;
            }
            _walker.Update(want, run);
            long ecsTicks = Stopwatch.GetTimestamp() - te;
            if (_client.State is ClientState.Failed) { Console.WriteLine("connection failed: " + _world.LastState); return 1; }

            // ---- camera follows the predicted, interpolated player
            if (!_world.Player.IsNull && _walker.HasPosition)
            {
                RenderExtract.Interpolate(_world.Player.GetComponent<Position>(), _world.Player.GetComponent<Motion>(), _clock.Now,
                    out float fx, out float fy, out float fz, out _, out _, out _, out _);
                RenderExtract.TileToWorld(fx, fy, fz, out int ax, out int ay);
                _camX = ax + 22;
                _camY = ay + 22;
            }

            // ---- view
            SDL_GetWindowSizeInPixels(_window, out int pw, out int ph);
            float density = SDL_GetWindowPixelDensity(_window);
            if (density <= 0) density = 1;
            if (_r.Offscreen) { pw = _o.Width; ph = _o.Height; density = 1; }
            float scale = _zoom * density;
            double cx = Math.Round(_camX * scale) / scale, cy = Math.Round(_camY * scale) / scale;
            float halfW = pw * 0.5f / scale, halfH = ph * 0.5f / scale;
            var view = new RectI((int)(cx - halfW) - 1, (int)(cy - halfH) - 1, (int)(cx + halfW) + 1, (int)(cy + halfH) + 1);
            float pre = 0.35f;
            var need = new RectI((int)(cx - halfW * (1 + pre)) - 128, (int)(cy - halfH * (1 + pre)) - 128, (int)(cx + halfW * (1 + pre)) + 128, (int)(cy + halfH * (1 + pre)) + 128);

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
            _chunks.Update(frame, in view, in need);
            _pages.BeginFrame();
            int chunkCount = _chunks.BuildDrawList(in view, _table, _pages, frame, out int instances);

            // ---- render extract: ECS → dynamic instances (one more chunk-table entry)
            _extract.Run(in view, _pages, frame, _clock.Now);
            int dyn = _extract.Count;
            if (dyn > 0 && chunkCount < _table.Length)
            {
                _table[chunkCount].First = _dynFirst;
                _table[chunkCount].Prefix = (uint)instances;
                chunkCount++;
                instances += dyn;
            }
            _pages.StartLoads(frame);

            int camPxX = (int)Math.Floor(cx), camPxY = (int)Math.Floor(cy);
            var fi = new Renderer.FrameInput
            {
                Chunks = _chunks, Pages = _pages, Table = _table, ChunkCount = chunkCount, Instances = instances,
                Frame = frame, LinearFilter = scale < 0.999f, ChunkUploadBudget = _o.ChunkBudget,
                Dynamic = _extract.Instances, DynamicCount = dyn, DynamicFirst = _dynFirst,
                Uniforms = new FrameUniforms
                {
                    CamPxX = camPxX, CamPxY = camPxY,
                    CamFracX = (float)(cx - camPxX), CamFracY = (float)(cy - camPxY),
                    ScaleX = scale, ScaleY = scale,
                    CamDepth = BakeJob.CameraDepthKey((float)(cy / 22.0)),
                    ChunkCount = (uint)chunkCount,
                    InvAtlas = 1f / _pack.PageSize,
                    FormatFlags = PageFormats.ShaderFlags(_pack.Format),
                    GrayTolerance = PageFormats.Bc7GrayTolerance / 255f,
                    DepthScale = Renderer.DepthScale,
                },
            };

            // ---- scripted screenshots: before, mid-walk, after
            if (_o.ShotsDir != null)
            {
                bool streamed = _chunks.PendingChunks == 0 && _chunks.UploadQueueLength == 0 && !_pages.Pending && _pages.MissingVisible == 0 && frame > 3;
                bool idle = _walker.Pending == 0 && (_world.Player.IsNull || _clock.Now >= _world.Player.GetComponent<Motion>().Start + _world.Player.GetComponent<Motion>().Duration);
                switch (phase)
                {
                    case ShotPhase.Login:
                        if (_world.InWorld && _walker.HasPosition && (_o.Go == null || _goSent && t0 - _goSentAt > freq * 2.5)) phase = ShotPhase.Before;
                        break;
                    case ShotPhase.Before:
                        settled = streamed ? settled + 1 : 0;
                        if (settled >= 3 || t0 > deadline)
                        {
                            Shot(ref fi, "client_before.png", dyn);
                            phase = _script != null ? ShotPhase.Walking : ShotPhase.Done;
                            settled = 0;
                            continue;
                        }
                        break;
                    case ShotPhase.Walking:
                        if (!midTaken && _script.Index >= _script.Count / 2 && !_world.Player.IsNull && _world.Player.GetComponent<Motion>().Duration > 0 && !idle)
                        {
                            midTaken = true;
                            Shot(ref fi, "client_mid.png", dyn);
                            continue;
                        }
                        if (_script.Done && idle) phase = ShotPhase.Settle;
                        break;
                    case ShotPhase.Settle:
                        settled = streamed ? settled + 1 : 0;
                        if (settled >= 3 || t0 > deadline)
                        {
                            Shot(ref fi, "client_after.png", dyn);
                            PrintWalkStats();
                            return 0;
                        }
                        break;
                }
            }

            _r.RenderFrame(ref fi);
            long t1 = Stopwatch.GetTimestamp();
            double cpu = (t1 - t0 - _r.AcquireWaitTicks) * 1000.0 / freq;
            cpuAccum += cpu;
            framesSinceTitle++;

            if (_o.Bench)
            {
                if (!benchRunning && _world.InWorld && _chunks.PendingChunks == 0 && !_pages.Pending)
                {
                    if (benchWarm == 0) benchWarm = t0 + (long)(freq * 1.0);
                    else if (t0 >= benchWarm)
                    {
                        benchRunning = true;
                        benchStart = Stopwatch.GetTimestamp();
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
                        allocStart = GC.GetAllocatedBytesForCurrentThread();
                    }
                }
                else if (benchRunning && benchFrames < maxSamples)
                {
                    cpuMs[benchFrames] = (float)cpu;
                    extractMs[benchFrames] = (float)(_extract.Ticks * 1000.0 / freq);
                    ecsMs[benchFrames] = (float)(ecsTicks * 1000.0 / freq);
                    dynAccum += dyn;
                    benchFrames++;
                    if ((t1 - benchStart) / freq >= _o.BenchSeconds) running = false;
                }
            }

            if (!_r.Offscreen && t1 - titleAt > freq / 2)
            {
                double secs = (t1 - titleAt) / freq;
                UpdateTitle(framesSinceTitle / secs, cpuAccum / framesSinceTitle, instances, dyn);
                titleAt = t1; framesSinceTitle = 0; cpuAccum = 0;
            }
        }

        if (_o.Bench)
        {
            long alloc = GC.GetAllocatedBytesForCurrentThread() - allocStart;
            ReportBench(cpuMs, extractMs, ecsMs, benchFrames, alloc, GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2,
                benchFrames > 0 ? dynAccum / (double)benchFrames : 0, (Stopwatch.GetTimestamp() - benchStart) / freq);
        }
        PrintWalkStats();
        return 0;
    }

    int ReadInput(ref bool run)
    {
        var keys = SDL_GetKeyboardState();
        float dx = 0, dy = 0;
        if (keys[(int)SDL_Scancode.SDL_SCANCODE_W] || keys[(int)SDL_Scancode.SDL_SCANCODE_UP]) dy -= 1;
        if (keys[(int)SDL_Scancode.SDL_SCANCODE_S] || keys[(int)SDL_Scancode.SDL_SCANCODE_DOWN]) dy += 1;
        if (keys[(int)SDL_Scancode.SDL_SCANCODE_A] || keys[(int)SDL_Scancode.SDL_SCANCODE_LEFT]) dx -= 1;
        if (keys[(int)SDL_Scancode.SDL_SCANCODE_D] || keys[(int)SDL_Scancode.SDL_SCANCODE_RIGHT]) dx += 1;
        if (keys[(int)SDL_Scancode.SDL_SCANCODE_LSHIFT] || keys[(int)SDL_Scancode.SDL_SCANCODE_RSHIFT]) run = true;
        if (dx != 0 || dy != 0) return Walker.DirectionFromScreen(dx, dy);
        if (_mouseHeld)
        {
            // mouse-hold walks toward the cursor (window points, relative to the centre where the player is); far = run
            SDL_GetWindowSize(_window, out int ww, out int wh);
            float mx = _mouseX - ww * 0.5f, my = _mouseY - wh * 0.5f;
            if (mx * mx + my * my < 20 * 20) return -1;
            if (mx * mx + my * my > 200 * 200) run = true;
            return Walker.DirectionFromScreen(mx, my);
        }
        return -1;
    }

    void Shot(ref Renderer.FrameInput fi, string name, int dyn)
    {
        fi.ScreenshotPath = Path.Combine(_o.ShotsDir, name);
        _r.RenderFrame(ref fi);
        Console.WriteLine($"screenshot {fi.ScreenshotPath}: player {_walker.X},{_walker.Y},{_walker.Z} facing {_walker.Facing}, {dyn} dynamic instances " +
            $"({_extract.MobilesDrawn} mobiles, {_extract.ItemsDrawn} items), {fi.Instances} total, ecs entities {_world.EntityCount}");
    }

    void PrintWalkStats()
    {
        var r = _walker.Rtts.ToArray();
        Array.Sort(r);
        Console.WriteLine($"walk: {_walker.StepsSent} steps + {_walker.TurnsSent} turns sent, {_walker.Acks} acks, {_walker.Rejects} rejects, {_walker.Snaps} snaps, " +
            $"{_walker.Blocked} frames blocked by prediction, cap waits {_walker.CapHits}; predicted {_walker.X},{_walker.Y},{_walker.Z} server-confirmed {_walker.ServerX},{_walker.ServerY},{_walker.ServerZ}" +
            (r.Length > 0 ? $"; move RTT median {r[r.Length / 2]:F2} ms max {r[^1]:F2} ms" : ""));
        Console.WriteLine($"ecs: {_world.EntityCount} entities ({_world.Mobiles} mobiles, {_world.Items} items created, {_world.Removes} removed); walk-map region loads {_map.RegionLoads} ({_map.RegionLoadTicks * 1000.0 / Stopwatch.Frequency:F1} ms total)");
    }

    void ReportBench(float[] cpu, float[] ext, float[] ecs, int n, long alloc, int g0, int g1, int g2, double avgDyn, double secs)
    {
        if (n < 2) { Console.WriteLine("bench: no samples"); return; }
        var c = cpu.AsSpan(0, n).ToArray(); var e = ext.AsSpan(0, n).ToArray(); var s = ecs.AsSpan(0, n).ToArray();
        Array.Sort(c); Array.Sort(e); Array.Sort(s);
        static float P(float[] a, double p) => a[Math.Min(a.Length - 1, (int)(a.Length * p))];
        Console.WriteLine($"bench [{PageFormats.Name(_pack.Format)}, {_o.Width}x{_o.Height} offscreen fence-throttled, {_o.Mobiles} synthetic mobiles + {_o.Items} items]: {n} frames in {secs:F1} s = {n / secs:F0} fps, {avgDyn:F0} dynamic instances/frame drawn");
        Console.WriteLine($"  CPU frame ms     median {P(c, 0.5):F3}  p95 {P(c, 0.95):F3}  p99 {P(c, 0.99):F3}  max {c[^1]:F3}");
        Console.WriteLine($"  render extract   median {P(e, 0.5):F3}  p95 {P(e, 0.95):F3}  max {e[^1]:F3}");
        Console.WriteLine($"  poll+walker ms   median {P(s, 0.5):F3}  p95 {P(s, 0.95):F3}  max {s[^1]:F3}");
        Console.WriteLine($"  GC collections gen0 {g0} gen1 {g1} gen2 {g2}; main thread allocated {alloc} B ({alloc / (double)n:F1} B/frame)");
        Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "RESULT mobiles={0} dyn={1:F0} cpu_med={2:F3} cpu_p95={3:F3} cpu_p99={4:F3} extract_med={5:F3} extract_p95={6:F3} ecs_med={7:F3} alloc_bpf={8:F1} gcs={9}",
            _o.Mobiles, avgDyn, P(c, 0.5), P(c, 0.95), P(c, 0.99), P(e, 0.5), P(e, 0.95), P(s, 0.5), alloc / (double)n, g0 + g1 + g2));
    }

    void UpdateTitle(double fps, double cpuMs, int instances, int dyn)
    {
        Span<byte> b = _title;
        int p = 0;
        Append(b, ref p, "Uo.Client | "u8);
        AppendNum(b, ref p, fps, 0); Append(b, ref p, " fps | cpu "u8);
        AppendNum(b, ref p, cpuMs, 2); Append(b, ref p, " ms | inst "u8);
        AppendNum(b, ref p, instances, 0); Append(b, ref p, " | dyn "u8);
        AppendNum(b, ref p, dyn, 0); Append(b, ref p, " | player "u8);
        AppendNum(b, ref p, _walker.X, 0); Append(b, ref p, ","u8);
        AppendNum(b, ref p, _walker.Y, 0); Append(b, ref p, ","u8);
        AppendNum(b, ref p, _walker.Z, 0); Append(b, ref p, " | pending "u8);
        AppendNum(b, ref p, _walker.Pending, 0); Append(b, ref p, " | rejects "u8);
        AppendNum(b, ref p, _walker.Rejects, 0);
        b[p] = 0;
        fixed (byte* t = _title) SDL_SetWindowTitle(_window, t);
    }

    [System.Runtime.InteropServices.DllImport("SDL3", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    static extern byte SDL_SetWindowTitle(IntPtr window, byte* title);

    static void Append(Span<byte> b, ref int p, ReadOnlySpan<byte> s) { s.CopyTo(b[p..]); p += s.Length; }

    static void AppendNum(Span<byte> b, ref int p, double v, int decimals)
    {
        if (decimals == 0) { ((long)Math.Round(v)).TryFormat(b[p..], out int w); p += w; }
        else { v.TryFormat(b[p..], out int w, "F2"); p += w; }
    }
}

/// <summary>Scripted input: "E5 S5 N5 W5" (direction + count). Holds each direction until the walker has sent
/// that many steps in it; a blocked direction is skipped after 600 ms.</summary>
public sealed class WalkScript
{
    readonly int[] _dirs;
    readonly bool _run, _loop;
    int _i, _stepsAtStart = -1;
    long _since;

    public WalkScript(string pattern, bool run, bool loop)
    {
        _run = run; _loop = loop;
        var list = new List<int>();
        foreach (var tok in pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int letters = 0;
            while (letters < tok.Length && char.IsLetter(tok[letters])) letters++;
            int count = int.Parse(tok.AsSpan(letters));
            int d = tok[..letters].ToUpperInvariant() switch
            {
                "N" => 0, "NE" => 1, "E" => 2, "SE" => 3, "S" => 4, "SW" => 5, "W" => 6, "NW" => 7,
                _ => throw new ArgumentException("bad walk direction " + tok),
            };
            for (int k = 0; k < count; k++) list.Add(d);
        }
        _dirs = list.ToArray();
    }

    public int Index => _i;
    public int Count => _dirs.Length;
    public bool Done => _i >= _dirs.Length;

    public int Next(Walker w, long now, out bool run)
    {
        run = _run;
        if (_i >= _dirs.Length)
        {
            if (!_loop) return -1;
            _i = 0;
        }
        if (_stepsAtStart < 0) { _stepsAtStart = w.StepsSent; _since = now; }
        if (w.StepsSent > _stepsAtStart || now - _since > Clock.Ms(600) && w.Pending == 0)
        {
            _i++;
            _stepsAtStart = w.StepsSent;
            _since = now;
            if (_i >= _dirs.Length) return _loop ? _dirs[0] : -1;
        }
        return _dirs[_i];
    }
}
