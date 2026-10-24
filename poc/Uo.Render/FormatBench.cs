using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Uo.Render;

/// <summary><c>--bench-formats packA packB ...</c>: runs the identical scripted bench once per pack in a child process
/// (offscreen 3840×2160 by default, fence-throttled, vsync irrelevant), records system CPU load during each run, and
/// prints a comparison table built from each child's RESULT line.</summary>
public static partial class FormatBench
{
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    public static int Run(List<string> packs, int width, int height, int layers)
    {
        var exe = Environment.ProcessPath!;
        var results = new List<(string Pack, Dictionary<string, string> R, double Load)>();
        foreach (var pack in packs)
        {
            if (!File.Exists(Path.Combine(pack, "manifest.json")))
            {
                Console.WriteLine($"== {pack}: skipped (no manifest.json yet)");
                continue;
            }
            Console.WriteLine($"== {pack}");
            GetSystemTimes(out var i0, out var k0, out var u0);
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "--pack", pack, "--bench", "--offscreen", "--size", $"{width}x{height}", "--layers", layers.ToString() })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            Dictionary<string, string> result = null;
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("   ! " + e.Data); };
            p.BeginErrorReadLine();
            string line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                if (line.StartsWith("RESULT "))
                {
                    result = new Dictionary<string, string>();
                    foreach (var kv in line[7..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = kv.IndexOf('=');
                        if (eq > 0) result[kv[..eq]] = kv[(eq + 1)..];
                    }
                }
                else Console.WriteLine("   " + line);
            }
            p.WaitForExit();
            GetSystemTimes(out var i1, out var k1, out var u1);
            double total = (k1 - k0) + (u1 - u0); // kernel time includes idle
            double load = total > 0 ? 100.0 * (1 - (i1 - i0) / total) : 0;
            Console.WriteLine($"   system CPU busy during run: {load:F0}% (all processes; this bench itself uses ~1 core + workers)");
            if (result == null) { Console.WriteLine($"   no RESULT (exit code {p.ExitCode})"); continue; }
            results.Add((pack, result, load));
        }

        if (results.Count == 0) return 1;
        Console.WriteLine();
        Console.WriteLine($"format comparison ({width}x{height} offscreen, identical camera script incl. zoom-0.5 segments, {layers} layers):");
        string[] cols = ["format", "fps", "cpu_med", "cpu_p95", "cpu_p99", "int_med", "int_p99", "gpu_med", "gpu_net", "gpu_p95", "uploads", "up_ms", "read_ms", "page_file_mib", "page_read_ms", "page_dec_ms", "page_brotli", "region_ms", "vram_mib", "ws_mib", "alloc_bpf", "gcs"];
        Console.WriteLine(string.Join(" | ", cols) + " | sys_load");
        foreach (var (_, r, load) in results)
            Console.WriteLine(string.Join(" | ", cols.Select(c => r.TryGetValue(c, out var v) ? v : "-")) + $" | {load:F0}%");
        Console.WriteLine("cpu = CPU work per frame (excl. waits); int = frame interval with 2 frames in flight = max(CPU, GPU);");
        Console.WriteLine("gpu = one frame alone on an idle GPU, submit->fence (SDL_GPU has no timestamp queries); gpu_net = gpu_med minus empty submit->fence overhead; up_ms = main-thread map+unmap+copy-record per page;");
        Console.WriteLine("read_ms = worker page file read into the mapped staging buffer; region_ms = worker region read (+Brotli decode); vram_mib = layers x page chain bytes.");
        return 0;
    }
}
