// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Diagnostics;

namespace Uo.Render;

public enum DiagMetric
{
    StreamingTicks, ChunkTableTicks, BuildFrameTicks, TranslucentSortTicks,
    UploadTicks, BufferUploadBytes, BufferUploadCalls, TextureUploadBytes, TextureUploadCalls,
    ChunkTableRebuilds, ChunkTableReuses, StreamingMarks, EvictionScans, PageTouches, Count
}

public static class RenderDiagnostics
{
#if CUO_DEVTOOLS
    public static readonly bool On = Environment.GetEnvironmentVariable("CUO_DIAG") == "1";
    public static Action<DiagMetric, long> Record;
    public static long Stamp() => On ? Stopwatch.GetTimestamp() : 0;
    public static void Elapsed(DiagMetric metric, long start)
    {
        if (On) Record?.Invoke(metric, Stopwatch.GetTimestamp() - start);
    }
    public static void Add(DiagMetric metric, long value)
    {
        if (On) Record?.Invoke(metric, value);
    }
#else
    public const bool On = false;
    [Conditional("CUO_DEVTOOLS")] public static void Add(DiagMetric metric, long value) { }
    [Conditional("CUO_DEVTOOLS")] public static void Elapsed(DiagMetric metric, long start) { }
    public static long Stamp() => 0;
#endif
}
