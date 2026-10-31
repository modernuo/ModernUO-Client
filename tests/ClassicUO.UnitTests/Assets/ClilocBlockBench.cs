using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Uo.Pack;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Assets
{
    // Block-compressed cliloc, measured (poc-log "Text in the pack: audit fixes, overrides, block-compressed cliloc"):
    // CUO_CLILOC_BENCH = name=dir;name=dir... (packs with text/cliloc.bin in different layouts). For each: the file size,
    // the startup load (open the store, resolve it, read and parse enu, and deu over enu) as the client does it, the
    // bytes allocated and held, and for blocked layouts the first-lookup cost of every block. Prints a table; skipped
    // without the variable.
    public class ClilocBlockBench
    {
        private readonly ITestOutputHelper _out;

        public ClilocBlockBench(ITestOutputHelper output) => _out = output;

        private static (ClilocTable Lang, ClilocTable Enu) Load(string path, string lang)
        {
            using var store = RecordStore.Open(path, TextConst.MagicCliloc);
            var dir = ClilocStore.Resolve(store);
            var l = ClilocStore.ReadTable(store, dir.Languages[lang], out _);
            var e = lang == "enu" ? null : ClilocStore.ReadTable(store, dir.Languages["enu"], out _);

            return (l, e);
        }

        [Fact]
        public void Measure()
        {
            var spec = Environment.GetEnvironmentVariable("CUO_CLILOC_BENCH");

            if (string.IsNullOrEmpty(spec))
            {
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("| variant | cliloc.bin MiB | load enu ms (median of 15) | load deu+enu ms | allocated at load (enu) MiB | held after load (enu) MiB | blocks (enu) | first lookup per block µs (median / max) | held with every block decoded MiB |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");

            foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var (name, dir) = (part.Split('=')[0], part.Split('=', 2)[1]);
                var path = Path.Combine(dir, TextConst.ClilocFile);
                double size = new FileInfo(path).Length / 1048576.0;

                for (int w = 0; w < 3; w++)
                {
                    Load(path, "deu");
                }

                var enuMs = new List<double>();
                var deuMs = new List<double>();
                long alloc = 0;
                ClilocTable enu = null;

                for (int i = 0; i < 15; i++)
                {
                    long a0 = GC.GetAllocatedBytesForCurrentThread();
                    long t0 = Stopwatch.GetTimestamp();
                    enu = Load(path, "enu").Lang;
                    enuMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
                    t0 = Stopwatch.GetTimestamp();
                    Load(path, "deu");
                    deuMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                }

                long held = enu.PayloadBytes;
                string blockCost = "-";

                if (enu.Blocked)
                {
                    // the first lookup in each block: one id per block, timed alone (the decode dominates)
                    var firstIdOfBlock = new List<int>();
                    int loaded = 0;
                    var costs = new List<double>();

                    for (int i = 0; i < enu.Count && loaded < enu.BlockCount; i++)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        enu.TryGetString(enu.IdAt(i), out _);
                        double us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;

                        if (enu.BlocksLoaded > loaded)
                        {
                            loaded = enu.BlocksLoaded;
                            costs.Add(us);
                        }
                    }

                    costs.Sort();
                    blockCost = $"{costs[costs.Count / 2]:0} / {costs[^1]:0}";
                }
                else
                {
                    for (int i = 0; i < enu.Count; i++)
                    {
                        enu.Utf8At(i);
                    }
                }

                enuMs.Sort();
                deuMs.Sort();
                sb.AppendLine($"| {name} | {size:0.00} | {enuMs[7]:0.00} | {deuMs[7]:0.00} | {alloc / 1048576.0:0.00} | {held / 1048576.0:0.00} | {(enu.Blocked ? enu.BlockCount.ToString() : "-")} | {blockCost} | {enu.PayloadBytes / 1048576.0:0.00} |");
            }

            _out.WriteLine(sb.ToString());
            var file = Environment.GetEnvironmentVariable("CUO_CLILOC_BENCH_OUT");

            if (!string.IsNullOrEmpty(file))
            {
                File.WriteAllText(file, sb.ToString());
            }
        }
    }
}
