// SPDX-License-Identifier: BSD-2-Clause
using System;

namespace ClassicUO
{
    // CLEANUP(release): all diagnostics storage and probes are developer-only.
    internal static partial class Diagnostics
    {
#if CUO_DEVTOOLS
        public static readonly bool On = Environment.GetEnvironmentVariable("CUO_DIAG") == "1";
#else
        public const bool On = false;
#endif
    }
}
#if CUO_DEVTOOLS
namespace ClassicUO
{
    using System.Collections;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using ClassicUO.Network;

    internal static partial class Diagnostics
    {
        private static State _state;
        private static readonly Process _process = Process.GetCurrentProcess();
        private sealed class Window
        {
            // Main thread owns Active; worker owns Ready. Publication uses volatile state.
            public int Status;
            public readonly DiagHistogram[] Phases = MakeHistograms((int)FramePhase.Count);
            public readonly DiagHistogram Frame = new(), Present = new(), Interval = new();
            public readonly long[] Counters = new long[(int)FrameCounter.Count];
            public readonly DiagHistogram[] RenderPhases = MakeHistograms(5);
            public readonly long[] RenderFrame = new long[(int)global::Uo.Render.DiagMetric.Count];
            public readonly long[] Render = new long[(int)global::Uo.Render.DiagMetric.Count];
            public readonly Ping[] Pings = new Ping[256];
            public readonly long[] DrainStops = new long[5];
            public long CpuTicks;
            public long Start, End, Frames, Draws, Updates, Presents, Alloc, ProcessAlloc, PauseTicks;
            public int Gc0, Gc1, Gc2, PingCount, PingDropped, BacklogMax;
            public long DrainBytes, DrainPackets, Drains, DrainTicks;
            public string Display, Loop;
            public void Clear()
            {
                foreach (var h in Phases) h.Clear();
                foreach (var h in RenderPhases) h.Clear();
                Array.Clear(RenderFrame);
                Frame.Clear(); Present.Clear(); Interval.Clear();
                Array.Clear(Counters); Array.Clear(Render); Array.Clear(Pings); Array.Clear(DrainStops);
                Frames = Draws = Updates = Presents = Alloc = DrainBytes = DrainPackets = Drains = DrainTicks = 0;
                PingCount = PingDropped = BacklogMax = 0;
                Display = Loop = null;
            }
        }
        private sealed class State
        {
            public readonly Window[] Windows = { new(), new(), new() };
            public readonly Ping[] ActivePings = new Ping[16];
            public readonly AutoResetEvent Wake = new(false);
            public readonly Thread Writer;
            public readonly string Header, Path;
            public Window Current;
            public readonly int ThreadId = Environment.CurrentManagedThreadId;
            public long LastUpdate, LastPresent, LastAlloc, Sequence, ForeignPingSends;
            public bool Stop;
            public Exception Error;
            public State(string header)
            {
                Header = header;
                string folder = System.IO.Path.Combine(CUOEnviroment.ExecutablePath, "Logs");
                Directory.CreateDirectory(folder);
                Path = System.IO.Path.Combine(folder, $"diag-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.txt");
                Current = Windows[0]; Current.Status = 1;
                Baseline(Current, Stopwatch.GetTimestamp());
                LastAlloc = GC.GetAllocatedBytesForCurrentThread();
                Writer = new Thread(WriteLoop) { IsBackground = true, Name = "CUO diagnostics" };
            }
        }
        private static DiagHistogram[] MakeHistograms(int count)
        {
            var result = new DiagHistogram[count];
            for (int i = 0; i < count; i++) result[i] = new();
            return result;
        }
        public static void Start(string header)
        {
            if (!On || _state != null) return;
            try
            {
                _state = new State(header);
                global::Uo.Render.RenderDiagnostics.Record = RecordRender;
                _state.Writer.Start();
            }
            catch (Exception ex)
            {
                ClassicUO.Utility.Logging.Log.Warn($"Diagnostics could not start: {ex.Message}");
            }
        }
        private static void Baseline(Window w, long now)
        {
            w.Start = now;
            w.CpuTicks = _process.TotalProcessorTime.Ticks;
            w.Gc0 = GC.CollectionCount(0); w.Gc1 = GC.CollectionCount(1); w.Gc2 = GC.CollectionCount(2);
            w.PauseTicks = GC.GetTotalPauseDuration().Ticks;
            w.ProcessAlloc = GC.GetTotalAllocatedBytes();
        }
        private static void Finish(Window w, long now)
        {
            w.End = now;
            w.CpuTicks = _process.TotalProcessorTime.Ticks - w.CpuTicks;
            w.Gc0 = GC.CollectionCount(0) - w.Gc0; w.Gc1 = GC.CollectionCount(1) - w.Gc1; w.Gc2 = GC.CollectionCount(2) - w.Gc2;
            w.PauseTicks = GC.GetTotalPauseDuration().Ticks - w.PauseTicks;
            w.ProcessAlloc = GC.GetTotalAllocatedBytes() - w.ProcessAlloc;
        }
        public static void Update()
        {
            var s = _state;
            if (s == null) return;
            long now = Stopwatch.GetTimestamp();
            s.Current.Updates++;
            if (s.LastUpdate != 0)
            {
                long interval = now - s.LastUpdate;
                s.Current.Interval.Add(ToUs(interval));
                for (int i = 0; i < s.ActivePings.Length; i++)
                {
                    ref var p = ref s.ActivePings[i];
                    if (p.Owner != null) p.MaxInterval = Math.Max(p.MaxInterval, interval);
                }
            }
            s.LastUpdate = now;
        }
        public static void Draw() { if (_state != null) _state.Current.Draws++; }
        public static void UpdateEnd()
        {
            var s = _state;
            if (s == null) return;
            long elapsed = Stopwatch.GetTimestamp() - s.LastUpdate;
            foreach (ref var p in s.ActivePings.AsSpan())
                if (p.Owner != null) p.MaxUpdate = Math.Max(p.MaxUpdate, elapsed);
        }
        public static void Frame(long now, long presentTicks, long[] phases, int[] counters)
        {
            var s = _state;
            if (s == null) return;
            var w = s.Current;
            w.Frames++; w.Presents++;
            w.Present.Add(ToUs(presentTicks));
            if (s.LastPresent != 0) w.Frame.Add(ToUs(now - s.LastPresent));
            s.LastPresent = now;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long allocDelta = allocated - s.LastAlloc;
            w.Alloc += allocDelta;
            w.Counters[(int)FrameCounter.AllocBytes] += allocDelta;
            s.LastAlloc = allocated;
            for (int i = 0; i < phases.Length; i++) w.Phases[i].Add(ToUs(phases[i]));
            for (int i = 0; i < w.RenderPhases.Length; i++) w.RenderPhases[i].Add(ToUs(w.RenderFrame[i]));
            Array.Clear(w.RenderFrame);
            for (int i = 0; i < counters.Length; i++) w.Counters[i] += counters[i];
            for (int i = 0; i < s.ActivePings.Length; i++)
            {
                ref var p = ref s.ActivePings[i];
                if (p.Owner == null) continue;
                p.MaxPresent = Math.Max(p.MaxPresent, presentTicks);
                p.MaxDraw = Math.Max(p.MaxDraw, phases[(int)FramePhase.Draw]);
            }
            if (now - w.Start < Stopwatch.Frequency * 5) return;
            foreach (var next in s.Windows)
            {
                if (Volatile.Read(ref next.Status) != 0) continue;
                next.Status = 1; Baseline(next, now); s.Current = next;
                Finish(w, now); Volatile.Write(ref w.Status, 2); s.Wake.Set();
                return;
            }
            // A slow disk extends this window, rather than blocking or allocating on the game thread.
        }
        public static bool NeedsDisplay => _state != null && _state.Current.Display == null;
        public static void Display(string value) { if (_state != null) _state.Current.Display = value; }
        public static void Loop(string value) { if (_state != null) _state.Current.Loop = value; }
        public static void RecordRender(global::Uo.Render.DiagMetric metric, long value)
        {
            if (_state != null)
            {
                _state.Current.Render[(int)metric] += value;
                _state.Current.RenderFrame[(int)metric] += value;
            }
        }
        public static void Drain(long start, int bytes, int packets, int backlog, int stop)
        {
            if (_state == null) return;
            var w = _state.Current;
            w.Drains++; w.DrainBytes += bytes; w.DrainPackets += packets;
            w.BacklogMax = Math.Max(w.BacklogMax, backlog);
            long end = Stopwatch.GetTimestamp();
            w.DrainTicks += end - start;
            for (int i = 0; i < w.PingCount; i++)
            {
                ref var p = ref w.Pings[i];
                if (p.Handler >= start && p.Handler <= end)
                { p.DrainStop = stop; p.Backlog = backlog; p.DrainBytes = bytes; }
            }
            w.DrainStops[stop]++;
        }
        public static void Stop()
        {
            var s = _state;
            if (s == null || s.Stop) return;
            foreach (ref var p in s.ActivePings.AsSpan())
                if (p.Owner != null) Complete(ref p, "shutdown");
            Finish(s.Current, Stopwatch.GetTimestamp());
            Volatile.Write(ref s.Current.Status, 2);
            Volatile.Write(ref s.Stop, true); s.Wake.Set();
            if (!s.Writer.Join(3000))
                ClassicUO.Utility.Logging.Log.Warn("Diagnostics writer did not finish within 3 seconds; report may be partial.");
            if (s.Error != null)
                ClassicUO.Utility.Logging.Log.Warn($"Diagnostics report failed: {s.Error.Message}");
        }
        private static long ToUs(long ticks) => (long)Math.Ceiling(ticks * (1_000_000.0 / Stopwatch.Frequency));
        private static void WriteLoop()
        {
            var s = _state;
            try
            {
                using var writer = new StreamWriter(new FileStream(s.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
                writer.WriteLine(s.Header); writer.Flush();
                while (true)
                {
                    Window ready = null;
                    foreach (var w in s.Windows)
                        if (Volatile.Read(ref w.Status) == 2 && (ready == null || w.Start < ready.Start)) ready = w;
                    if (ready == null)
                    {
                        if (Volatile.Read(ref s.Stop))
                        {
                            // Stop publishes the final window before the flag. Re-scan after observing the flag:
                            // it may have been published between the first scan and this read.
                            bool pending = false;
                            foreach (var w in s.Windows) pending |= Volatile.Read(ref w.Status) == 2;
                            if (!pending) break;
                            continue;
                        }
                        s.Wake.WaitOne(1000); continue;
                    }
                    WriteWindow(writer, ready); writer.Flush();
                    ready.Clear(); Volatile.Write(ref ready.Status, 0);
                }
            }
            catch (Exception ex) { s.Error = ex; }
        }
        private static void Histogram(TextWriter writer, string name, DiagHistogram h)
            => writer.WriteLine($"{name} n={h.Count} p50<={h.Percentile(.50)} p95<={h.Percentile(.95)} p99<={h.Percentile(.99)} max={h.Max} us");
        private static void WriteWindow(TextWriter writer, Window w)
        {
            double seconds = (w.End - w.Start) / (double)Stopwatch.Frequency;
            writer.WriteLine(FormattableString.Invariant($"window start={w.Start} end={w.End} sec={seconds:F3} cpu_ms_per_s={TimeSpan.FromTicks(w.CpuTicks).TotalMilliseconds / seconds:F1} fps={w.Presents / seconds:F1} loops/s={w.Updates / seconds:F1} updates={w.Updates} draws={w.Draws} presents={w.Presents} gc={w.Gc0}/{w.Gc1}/{w.Gc2} pause_ms={TimeSpan.FromTicks(w.PauseTicks).TotalMilliseconds:F3} alloc/frame={w.Alloc / (double)Math.Max(1, w.Frames):F1} process_alloc_bytes={w.ProcessAlloc} foreign_ping_sends_total={System.Threading.Interlocked.Read(ref _state.ForeignPingSends)} ping_dropped={w.PingDropped}"));
            writer.WriteLine(w.Display); writer.WriteLine(w.Loop);
            Histogram(writer, "frame", w.Frame); Histogram(writer, "present", w.Present); Histogram(writer, "update_interval", w.Interval);
            for (int i = 0; i < w.Phases.Length; i++) Histogram(writer, "phase " + (FramePhase)i, w.Phases[i]);
            for (int i = 0; i < w.Counters.Length; i++) writer.WriteLine($"counter {(FrameCounter)i} sum={w.Counters[i]}");
            for (int i = 0; i < w.RenderPhases.Length; i++) Histogram(writer, "render_phase " + (global::Uo.Render.DiagMetric)i, w.RenderPhases[i]);
            for (int i = 0; i < w.Render.Length; i++) writer.WriteLine($"render {(global::Uo.Render.DiagMetric)i} sum={w.Render[i]}");
            writer.WriteLine($"drain calls={w.Drains} bytes={w.DrainBytes} packets={w.DrainPackets} time_us={ToUs(w.DrainTicks)} backlog_max_bytes={w.BacklogMax} stops(empty/bytes/packets/time/closed)={string.Join('/', w.DrainStops)}");
            for (int i = 0; i < w.PingCount; i++)
            {
                var p = w.Pings[i];
                writer.WriteLine($"ping connection={p.Connection} seq={p.Sequence} index={p.Index} status={p.Status} created={p.Created} queued={p.Queued} socket_start={p.SendStart} socket_end={p.SendEnd} read={p.Read} dequeued={p.Dequeued} handler={p.Handler} frame_clock_ms={p.RawMs} displayed_ms={p.DisplayMs} drain_stop={p.DrainStop} backlog_bytes={p.Backlog} drain_bytes={p.DrainBytes} max_interval_us={ToUs(p.MaxInterval)} max_update_us={ToUs(p.MaxUpdate)} max_draw_us={ToUs(p.MaxDraw)} max_present_us={ToUs(p.MaxPresent)} gc={p.Gc0}/{p.Gc1}/{p.Gc2} pause_ms={p.PauseMs:F3}");
            }
        }
        public static string RuntimeHeader()
        {
            var b = new StringBuilder();
            b.AppendLine($"CUO_DIAG v1 utc={DateTime.UtcNow:O} pid={Environment.ProcessId} executable={Environment.ProcessPath}");
            b.AppendLine($"runtime dynamic_code_supported={RuntimeFeature.IsDynamicCodeSupported} kind={(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT")} version={RuntimeInformation.FrameworkDescription} arch={RuntimeInformation.ProcessArchitecture} server_gc={GCSettings.IsServerGC} latency={GCSettings.LatencyMode} concurrent_gc={ConcurrentGc()}");
            b.AppendLine($"clock=Stopwatch ticks frequency={Stopwatch.Frequency}; timestamp 0=unobserved; durations CPU wall time; percentile upper bounds: 1us bins through 8191us, overflow uses observed max");
            b.AppendLine("ping read=socket-read completion of slice containing final reply byte (not kernel arrival); phase maxima include completed draw/present while outstanding and update through handler; work after reply dispatch excluded; pause/count deltas are process-wide; socket_end=synchronous write returned, not peer receipt; ping indices repeat every 5 sends, delayed echoes after index reuse are inherently ambiguous");
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                string key = (string)e.Key;
                if (key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CUO_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("FNA", StringComparison.OrdinalIgnoreCase) || key.StartsWith("SDL", StringComparison.OrdinalIgnoreCase))
                    b.AppendLine($"env {key}={(key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) || key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) || key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ? "<redacted>" : e.Value)}");
            }
            try
            {
                using var process = Process.GetCurrentProcess();
                foreach (ProcessModule m in process.Modules)
                    if (m.ModuleName.Contains("FNA3D", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Contains("SDL3", StringComparison.OrdinalIgnoreCase))
                        b.AppendLine($"native path={m.FileName} version={m.FileVersionInfo.FileVersion ?? "<no version resource>"}");
            }
            catch (Exception ex) { b.AppendLine($"native enumeration unavailable: {ex.Message}"); }
            return b.ToString();
        }
        private static string ConcurrentGc()
        {
            foreach (var entry in GC.GetConfigurationVariables())
                if (entry.Key.Contains("Concurrent", StringComparison.OrdinalIgnoreCase)) return entry.Value.ToString();
            return "unavailable (no concurrent value exposed by runtime)";
        }
    }
}
#endif
