using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;

namespace ClassicUO
{
    internal enum FramePhase
    {
        Pump,
        Packets,
        PluginTick,
        SceneUpdate,
        WorldMobiles,
        WorldItems,
        WorldEffectsText,
        WorldEcs,
        SceneUpdateRest,
        UiUpdate,
        UpdateOther,
        Update,
        DrawSetup,
        Lights,
        WorldPrepare,
        PrepSetup,
        PrepWalk,
        PrepGpuMasks,
        PrepHidden,
        PrepFoliageFinish,
        PrepText,
        PrepCaptureMulti,
        PrepCaptureStatic,
        PrepCaptureItem,
        PrepCaptureMobile,
        PrepCaptureOther,
        PrepItemCache,
        PrepCaptureCached,
        PrepRest,
        WalkLand,
        WalkStatic,
        WalkUnmeshed,
        WalkDynamic,
        WalkPosition,
        WalkPick,
        WalkOverride,
        WalkLight,
        WalkFoliage,
        BareWalk,
        WorldDraw,
        SceneDrawUi,
        UiDraw,
        Cursor,
        Composite,
        PluginDraw,
        DrawOther,
        Draw,
        BatcherGpuCalls,
        Present,
        Frame,
        // inside the UI pass (scene DrawUI, UIManager.Draw, cursor): the gump tree walk queueing render lists, and the
        // batcher's FNA time spent on the UI target
        UiTraverse,
        UiFna,
        UiPass,
        Count
    }

    internal enum FrameCounter
    {
        Items,
        Mobiles,
        Gumps,
        RenderListEntries,
        Flushes,
        DrawCalls,
        AllocBytes,
        GpuInstances,
        GpuDynamic,
        GpuOverrides,
        GpuDrawCalls,
        // world prepare: incremented during the frame, cleared after it
        Tiles,
        Objects,
        BakedLand,
        BakedStatics,
        Unmeshed,
        Dynamic,
        PickTests,
        OverrideCalls,
        HiddenTiles,
        Lights,
        Foliage,
        CaptureMultis,
        CaptureStatics,
        CaptureItems,
        CaptureMobiles,
        CaptureOther,
        GpuMaskRebuilds,
        GpuSkipActive,
        GpuFullTiles,
        GpuWalkTiles,
        GpuEdgeTiles,
        GpuPickTiles,
        // W4 ground-item cache: chunks re-extracted, tiles and items served from it, served tiles walked after a change
        ItemCacheBuilds,
        ItemCacheTiles,
        ItemCacheItems,
        ItemCacheFallbacks,
        // E7: items walked alone on served tiles (animated, corpses, fading...)
        ItemCacheWalked,
        // map loading (Map.UpdateLoading, MapLoader prefetch): chunks loaded and the time they took, chunks queued at the
        // end of the frame, time the main thread waited for a region decode, regions decoded on the main thread, prefetches
        ChunkLoads,
        ChunkLoadUs,
        // bytes allocated inside chunk loads, collections that started inside one and their pause (Map.LoadChunk)
        ChunkAlloc,
        ChunkGcs,
        ChunkGcUs,
        ChunkMeshUs,
        ChunkQueue,
        RegionWaitUs,
        RegionSyncLoads,
        RegionPrefetches,
        // first-sight art (Art and Texmap): decodes on the main thread (a lookup that had to decode) and their time, worker
        // decodes uploaded and the upload time, worker decodes still pending at the end of the frame
        ArtSyncLoads,
        ArtSyncUs,
        ArtAsyncUploads,
        ArtUploadUs,
        ArtPending,
        // the UI pass: batcher flushes and draw calls into the UI targets; B8: 1 when the gumps (cache) target was reused, 1 when
        // the UI target was left untouched too
        UiFlushes,
        UiDrawCalls,
        UiCached,
        UiUntouched,
        Count
    }

    // Per-phase frame timing for CUO_FRAMESTATS=1 (CUO_FRAMESTATS_PHASES=0 keeps only the frame total, to measure hook overhead).
    internal static class FrameStats
    {
        public static readonly bool On = DevBench.FrameStats && Environment.GetEnvironmentVariable("CUO_FRAMESTATS_PHASES") != "0";

        // CUO_FRAMESTATS_PREP=1 (or the DevBench plan entry prepdetail:on) also splits the tile walk into exclusive buckets
        // (Walk*) and times a bare walk of the same tile lists. It takes a timestamp per bucket change, so it adds overhead:
        // compare its world prepare with a run without it.
        public static bool PrepDetail = On && Environment.GetEnvironmentVariable("CUO_FRAMESTATS_PREP") == "1";

        private const int CAPACITY = 1 << 15;
        private const int PHASES = (int)FramePhase.Count;
        private const int COUNTERS = (int)FrameCounter.Count;

        private static readonly string[] _phaseNames =
        {
            "event pump", "ParsePackets", "Plugin.Tick", "GameScene.Update", "  world: mobiles", "  world: items",
            "  world: effects+text", "  world: Ecs.Update", "  scene update rest", "UIManager.Update", "update other", "Update total",
            "draw setup", "lights", "world prepare", "  prep: setup", "  prep: tile walk", "  prep: gpu skip masks", "  prep: gpu hidden collect",
            "  prep: foliage+finish", "  prep: text entities", "  prep: capture multis", "  prep: capture statics",
            "  prep: capture items", "  prep: capture mobiles", "  prep: capture other", "  prep: item cache", "  prep: capture cached", "  prep: rest", "    walk: baked land",
            "    walk: baked statics", "    walk: unmeshed", "    walk: items/mob/fx", "    walk: screen pos", "    walk: picking",
            "    walk: override/mesh", "    walk: lights", "    walk: foliage check", "  (bare list walk)", "world draw", "scene DrawUI", "UIManager.Draw", "cursor", "RT composite",
            "plugin draw", "draw other", "Draw total", "  batcher FNA calls", "Present", "frame",
            "  UI tree walk", "  UI batcher FNA", "UI pass total"
        };

        private static readonly string[] _phaseKeys =
        {
            "pump", "packets", "plugin", "scene_update", "world_mobiles", "world_items", "world_fx_text", "world_ecs", "scene_rest",
            "ui_update", "update_other", "update", "draw_setup", "lights", "world_prepare", "prep_setup", "prep_walk", "prep_gpu_masks", "prep_hidden",
            "prep_foliage_finish", "prep_text", "prep_cap_multi", "prep_cap_static", "prep_cap_item", "prep_cap_mobile", "prep_cap_other",
            "prep_item_cache", "prep_cap_cached", "prep_rest", "walk_land", "walk_static", "walk_unmeshed", "walk_dynamic", "walk_pos", "walk_pick", "walk_override", "walk_light",
            "walk_foliage", "bare_walk", "world_draw", "scene_draw_ui", "ui_draw",
            "cursor", "composite", "plugin_draw", "draw_other", "draw", "batcher_fna", "present", "frame",
            "ui_traverse", "ui_fna", "ui_pass"
        };

        private static readonly string[] _counterKeys = { "items", "mobiles", "gumps", "render_list", "flushes", "draw_calls", "alloc_bytes", "gpu_instances", "gpu_dynamic",
            "gpu_overrides", "gpu_draw_calls", "tiles", "objects", "baked_land", "baked_statics", "unmeshed", "dynamic", "pick_tests",
            "override_calls", "hidden_tiles", "lights_added", "foliage", "cap_multis", "cap_statics", "cap_items", "cap_mobiles", "cap_other", "gpu_mask_rebuilds",
            "gpu_skip_active", "gpu_full_tiles", "gpu_walk_tiles", "gpu_edge_tiles", "gpu_pick_tiles",
            "item_cache_builds", "item_cache_tiles", "item_cache_items", "item_cache_fallbacks", "item_cache_walked",
            "chunk_loads", "chunk_load_us", "chunk_alloc", "chunk_gc", "chunk_gc_us", "chunk_mesh_us", "chunk_queue", "region_wait_us", "region_sync", "region_prefetch",
            "art_sync", "art_sync_us", "art_async", "art_upload_us", "art_pending",
            "ui_flushes", "ui_draw_calls", "ui_cached", "ui_untouched" };

        private static readonly long[] _start = new long[PHASES];
        private static readonly long[] _acc = new long[PHASES];
        private static readonly int[] _cur = new int[COUNTERS];
        private static readonly float[][] _phase = CreateArrays<float>(PHASES);
        private static readonly int[][] _counter = CreateArrays<int>(COUNTERS);
        private static readonly float[] _sortScratch = DevBench.FrameStats ? new float[CAPACITY] : null;
        private static readonly int[] _sortScratchI = DevBench.FrameStats ? new int[CAPACITY] : null;

        private static int _count;
        private static long _lastEnd, _windowStart, _endDrawStart, _allocThread, _allocTotalStart;
        private static int _gc0, _gc1, _gc2;
        private static TimeSpan _pauseStart;
        private static bool _recording;
        private static string _tag = "";
        private static int _windowIndex;

        private static T[][] CreateArrays<T>(int n)
        {
            if (!DevBench.FrameStats)
            {
                return Array.Empty<T[]>();
            }

            var a = new T[n][];

            for (int i = 0; i < n; i++)
            {
                a[i] = new T[CAPACITY];
            }

            return a;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Begin(FramePhase phase)
        {
            if (On)
            {
                _start[(int)phase] = Stopwatch.GetTimestamp();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void End(FramePhase phase)
        {
            if (On)
            {
                _acc[(int)phase] += Stopwatch.GetTimestamp() - _start[(int)phase];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Set(FrameCounter counter, int value)
        {
            if (On)
            {
                _cur[(int)counter] = value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Stamp()
        {
            return On ? Stopwatch.GetTimestamp() : 0;
        }

        // Adds the time since `since` (a Stamp()) to phase and returns now.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long AddSince(FramePhase phase, long since)
        {
            if (On)
            {
                long now = Stopwatch.GetTimestamp();
                _acc[(int)phase] += now - since;

                return now;
            }

            return 0;
        }

        // Adds a Stopwatch tick count measured elsewhere (the batcher's FNA ticks) to phase.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddTicks(FramePhase phase, long ticks)
        {
            if (On)
            {
                _acc[(int)phase] += ticks;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Inc(FrameCounter counter)
        {
            if (On)
            {
                _cur[(int)counter]++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Add(FrameCounter counter, int value)
        {
            if (On)
            {
                _cur[(int)counter] += value;
            }
        }

        public static bool Detail
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => On && PrepDetail;
        }

        // Detail mode's exclusive walk buckets: charges the time since the last switch to the current bucket, makes `bucket`
        // current and returns the previous one (FramePhase.Count = none), so a nested duty can switch back.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FramePhase Enter(FramePhase bucket)
        {
            if (On && PrepDetail && bucket != _bucket)
            {
                return Switch(bucket);
            }

            return bucket;
        }

        public const FramePhase NoBucket = FramePhase.Count;
        private static FramePhase _bucket = NoBucket;
        private static long _bucketAt;

        private static FramePhase Switch(FramePhase bucket)
        {
            long now = Stopwatch.GetTimestamp();
            FramePhase prev = _bucket;

            if (prev != FramePhase.Count)
            {
                _acc[(int)prev] += now - _bucketAt;
            }

            _bucket = bucket;
            _bucketAt = now;
            _bucketEntries[(int)bucket]++;

            return prev;
        }

        // detail mode: switches into each bucket during the window, to subtract the timestamps' own cost
        private static readonly long[] _bucketEntries = new long[PHASES + 1];

        // Starts a measurement window; Window() ends it and logs the summary.
        public static void StartWindow(string tag)
        {
            _tag = tag ?? "";
            _recording = true;
            _windowStart = 0;
        }

        public static bool Recording => _recording;

        public static int WindowsReported => _windowIndex;

        public static void StopWindow()
        {
            if (_recording && _count > 0)
            {
                Report(Stopwatch.GetTimestamp());
            }

            _recording = false;
        }

        public static void BeforeEndDraw()
        {
            _endDrawStart = Stopwatch.GetTimestamp();
        }

        public static void AfterEndDraw()
        {
            long now = Stopwatch.GetTimestamp();

            if (!_recording)
            {
                _lastEnd = now;
                Array.Clear(_acc);
                ClearFrameCounters();
                UltimaBatcher2D.ResetFrameCounters();

                return;
            }

            if (_windowStart == 0)
            {
                _windowStart = now;
                _lastEnd = now;
                _count = 0;
                _allocThread = GC.GetAllocatedBytesForCurrentThread();
                _allocTotalStart = GC.GetTotalAllocatedBytes();
                _gc0 = GC.CollectionCount(0);
                _gc1 = GC.CollectionCount(1);
                _gc2 = GC.CollectionCount(2);
                _pauseStart = GC.GetTotalPauseDuration();
                Array.Clear(_bucketEntries);
                Array.Clear(_acc);
                ClearFrameCounters();
                UltimaBatcher2D.ResetFrameCounters();

                return;
            }

            if (_count < CAPACITY)
            {
                double toMs = 1000.0 / Stopwatch.Frequency;
                int i = _count++;

                _acc[(int)FramePhase.Present] = now - _endDrawStart;
                _acc[(int)FramePhase.Frame] = now - _lastEnd;

                if (On)
                {
                    _acc[(int)FramePhase.Pump] = _start[(int)FramePhase.Update] - _lastEnd;
                    _acc[(int)FramePhase.SceneUpdateRest] = _acc[(int)FramePhase.SceneUpdate] - _acc[(int)FramePhase.WorldMobiles] -
                                                            _acc[(int)FramePhase.WorldItems] - _acc[(int)FramePhase.WorldEffectsText] -
                                                            _acc[(int)FramePhase.WorldEcs];
                    _acc[(int)FramePhase.UpdateOther] = _acc[(int)FramePhase.Update] - _acc[(int)FramePhase.Packets] -
                                                        _acc[(int)FramePhase.PluginTick] - _acc[(int)FramePhase.SceneUpdate] -
                                                        _acc[(int)FramePhase.UiUpdate];
                    _acc[(int)FramePhase.DrawOther] = _acc[(int)FramePhase.Draw] - _acc[(int)FramePhase.DrawSetup] - _acc[(int)FramePhase.Lights] -
                                                      _acc[(int)FramePhase.WorldPrepare] - _acc[(int)FramePhase.WorldDraw] -
                                                      _acc[(int)FramePhase.SceneDrawUi] - _acc[(int)FramePhase.UiDraw] -
                                                      _acc[(int)FramePhase.Cursor] - _acc[(int)FramePhase.Composite] -
                                                      _acc[(int)FramePhase.PluginDraw];
                    _acc[(int)FramePhase.BatcherGpuCalls] = UltimaBatcher2D.FrameGpuTicks;
                    _acc[(int)FramePhase.PrepRest] = _acc[(int)FramePhase.WorldPrepare] - _acc[(int)FramePhase.PrepSetup] - _acc[(int)FramePhase.PrepWalk] - _acc[(int)FramePhase.PrepGpuMasks] -
                                                     _acc[(int)FramePhase.PrepHidden] - _acc[(int)FramePhase.PrepFoliageFinish] -
                                                     _acc[(int)FramePhase.PrepText] - _acc[(int)FramePhase.PrepCaptureMulti] -
                                                     _acc[(int)FramePhase.PrepCaptureStatic] - _acc[(int)FramePhase.PrepCaptureItem] -
                                                     _acc[(int)FramePhase.PrepCaptureMobile] - _acc[(int)FramePhase.PrepCaptureOther] -
                                                     _acc[(int)FramePhase.PrepItemCache] - _acc[(int)FramePhase.PrepCaptureCached];

                    _cur[(int)FrameCounter.Flushes] = UltimaBatcher2D.FrameFlushes;
                    _cur[(int)FrameCounter.DrawCalls] = UltimaBatcher2D.FrameDrawCalls;
                    _cur[(int)FrameCounter.Gumps] = UIManager.Gumps.Count;
                    SampleMapLoading();
                }

                long alloc = GC.GetAllocatedBytesForCurrentThread();
                _cur[(int)FrameCounter.AllocBytes] = (int)Math.Min(int.MaxValue, alloc - _allocThread);
                _allocThread = alloc;

                for (int p = 0; p < PHASES; p++)
                {
                    _phase[p][i] = (float)(_acc[p] * toMs);
                }

                for (int c = 0; c < COUNTERS; c++)
                {
                    _counter[c][i] = _cur[c];
                }
            }

            Array.Clear(_acc);
            ClearFrameCounters();
            UltimaBatcher2D.ResetFrameCounters();
            _lastEnd = now;

            if ((now - _windowStart) * 1000.0 / Stopwatch.Frequency >= 5000)
            {
                Report(now);
                _windowStart = 0;
                _lastEnd = Stopwatch.GetTimestamp();
            }
        }

        private static int _lastSync, _lastPrefetch;
        private static long _lastWaitTicks;

        private static void SampleMapLoading()
        {
            double toUs = 1e6 / Stopwatch.Frequency;
            _cur[(int)FrameCounter.ChunkLoads] = Game.Map.Map.FrameChunkLoads;
            _cur[(int)FrameCounter.ChunkLoadUs] = (int)(Game.Map.Map.FrameChunkLoadTicks * toUs);
            _cur[(int)FrameCounter.ChunkMeshUs] = (int)(Game.Map.Map.FrameMeshTicks * toUs);
            _cur[(int)FrameCounter.ChunkAlloc] = (int)Math.Min(int.MaxValue, Game.Map.Map.FrameChunkLoadBytes);
            _cur[(int)FrameCounter.ChunkGcs] = Game.Map.Map.FrameChunkLoadGcs;
            _cur[(int)FrameCounter.ChunkGcUs] = (int)(Game.Map.Map.FrameChunkLoadGcTicks / 10);
            Game.Map.Map.FrameChunkLoads = 0;
            Game.Map.Map.FrameChunkLoadTicks = 0;
            Game.Map.Map.FrameChunkLoadBytes = 0;
            Game.Map.Map.FrameChunkLoadGcs = 0;
            Game.Map.Map.FrameChunkLoadGcTicks = 0;
            Game.Map.Map.FrameMeshTicks = 0;
            _cur[(int)FrameCounter.ChunkQueue] = Client.Game?.UO?.World?.Map?.QueuedChunks ?? 0;

            var maps = Client.Game?.UO?.FileManager?.Maps;

            if (maps != null)
            {
                _cur[(int)FrameCounter.RegionWaitUs] = (int)((maps.PrefetchWaitTicks - _lastWaitTicks) * toUs);
                _cur[(int)FrameCounter.RegionSyncLoads] = maps.SyncLoads - _lastSync;
                _cur[(int)FrameCounter.RegionPrefetches] = maps.PrefetchesStarted - _lastPrefetch;
                _lastWaitTicks = maps.PrefetchWaitTicks;
                _lastSync = maps.SyncLoads;
                _lastPrefetch = maps.PrefetchesStarted;
            }

            _cur[(int)FrameCounter.ArtSyncLoads] = Renderer.Arts.Art.FrameSyncLoads;
            _cur[(int)FrameCounter.ArtSyncUs] = (int)(Renderer.Arts.Art.FrameSyncTicks * toUs);
            _cur[(int)FrameCounter.ArtAsyncUploads] = Renderer.Arts.Art.FrameAsyncUploads;
            _cur[(int)FrameCounter.ArtUploadUs] = (int)(Renderer.Arts.Art.FrameUploadTicks * toUs);
            _cur[(int)FrameCounter.ArtPending] = (Client.Game?.UO?.Arts?.PendingDecodes ?? 0) + (Client.Game?.UO?.Texmaps?.PendingDecodes ?? 0);
            Renderer.Arts.Art.FrameSyncLoads = Renderer.Arts.Art.FrameAsyncUploads = 0;
            Renderer.Arts.Art.FrameSyncTicks = Renderer.Arts.Art.FrameUploadTicks = 0;
        }

        private static void ClearFrameCounters()
        {
            if (On)
            {
                SampleMapLoading(); // so frames that are not recorded do not pile up into the next recorded one
            }

            Array.Clear(_cur, (int)FrameCounter.Tiles, COUNTERS - (int)FrameCounter.Tiles);
        }

        private static void Report(long now)
        {
            double seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
            long allocTotal = GC.GetTotalAllocatedBytes() - _allocTotalStart;
            int gc0 = GC.CollectionCount(0) - _gc0, gc1 = GC.CollectionCount(1) - _gc1, gc2 = GC.CollectionCount(2) - _gc2;
            double pauseMs = (GC.GetTotalPauseDuration() - _pauseStart).TotalMilliseconds;
            int n = _count;
            _windowIndex++;

            var sb = new StringBuilder(4096);
            var json = new StringBuilder(4096);
            var inv = CultureInfo.InvariantCulture;

            sb.Append(inv, $"[framestats] {_tag} window {_windowIndex}: {n} frames in {seconds:0.00} s = {n / seconds:0} fps; ")
              .Append(inv, $"gc gen0 {gc0} gen1 {gc1} gen2 {gc2}, pause {pauseMs:0.00} ms, alloc {allocTotal / Math.Max(1, n)} B/frame (all threads)\n")
              .Append("  phase                      avg      p50      p95      p99  (ms)\n");

            json.Append(inv, $"FRAMESTATS {{\"tag\":\"{_tag}\",\"window\":{_windowIndex},\"frames\":{n},\"seconds\":{seconds:0.000},\"fps\":{n / seconds:0.0},")
                .Append(inv, $"\"phases_on\":{(On ? "true" : "false")},\"prep_detail\":{(Detail ? "true" : "false")},\"gc0\":{gc0},\"gc1\":{gc1},\"gc2\":{gc2},\"gc_pause_ms\":{pauseMs:0.000},")
                .Append(inv, $"\"alloc_per_frame\":{allocTotal / Math.Max(1, n)},\"phases\":{{");

            bool first = true;

            for (int p = 0; p < PHASES; p++)
            {
                if (!On && p != (int)FramePhase.Frame && p != (int)FramePhase.Present)
                {
                    continue;
                }

                Stats(_phase[p], n, _sortScratch, out double avg, out double p50, out double p95, out double p99);
                sb.Append(inv, $"  {_phaseNames[p],-24} {avg,7:0.000}  {p50,7:0.000}  {p95,7:0.000}  {p99,7:0.000}\n");
                json.Append(first ? "" : ",").Append(inv, $"\"{_phaseKeys[p]}\":[{avg:0.0000},{p50:0.0000},{p95:0.0000},{p99:0.0000}]");
                first = false;
            }

            if (Detail)
            {
                json.Append("},\"bucket_entries\":{");
                first = true;

                for (int p = (int)FramePhase.WalkLand; p <= (int)FramePhase.WalkFoliage; p++)
                {
                    json.Append(first ? "" : ",").Append(inv, $"\"{_phaseKeys[p]}\":{_bucketEntries[p] / (double)Math.Max(1, n):0.0}");
                    first = false;
                }
            }

            json.Append("}");

            // frame-time tail: the worst frame, hitch counts, and the phases that dominated the worst frame
            var frames = _phase[(int)FramePhase.Frame];
            var presents = _phase[(int)FramePhase.Present];
            int worst = 0, hitch8 = 0, hitch16 = 0, cpuHitch8 = 0, cpuHitch16 = 0, worstWork = 0, workHitch8 = 0, workHitch16 = 0;
            double cpuMax = 0, workMax = 0;

            for (int i = 0; i < n; i++)
            {
                if (frames[i] > frames[worst])
                {
                    worst = i;
                }

                if (frames[i] > 8f)
                {
                    hitch8++;
                }

                if (frames[i] > 16f)
                {
                    hitch16++;
                }

                // the frame without Present: GPU/compositor waits (other windows, the driver) are not CPU hitches
                double cpu = frames[i] - presents[i];
                cpuMax = Math.Max(cpuMax, cpu);
                cpuHitch8 += cpu > 8 ? 1 : 0;
                cpuHitch16 += cpu > 16 ? 1 : 0;

                // Update + Draw only: the client's own work (loading shows up here, in world prepare or update); stalls
                // outside every phase (the OS not scheduling the window, focus changes) do not
                if (On)
                {
                    double work = _phase[(int)FramePhase.Update][i] + _phase[(int)FramePhase.Draw][i];

                    if (work > workMax)
                    {
                        workMax = work;
                        worstWork = i;
                    }

                    workHitch8 += work > 8 ? 1 : 0;
                    workHitch16 += work > 16 ? 1 : 0;
                }
            }

            double frameMax = n > 0 ? frames[worst] : 0;

            string Top(int frame)
            {
                var top = new StringBuilder();

                if (!On || n == 0)
                {
                    return "";
                }

                for (int k = 0; k < 3; k++)
                {
                    int best = -1;

                    for (int p = 0; p < (int)FramePhase.Frame; p++)
                    {
                        if (p == (int)FramePhase.Update || p == (int)FramePhase.Draw || p == (int)FramePhase.SceneUpdate || top.ToString().Contains(_phaseKeys[p] + "="))
                        {
                            continue;
                        }

                        if (best < 0 || _phase[p][frame] > _phase[best][frame])
                        {
                            best = p;
                        }
                    }

                    top.Append(inv, $"{(k > 0 ? " " : "")}{_phaseKeys[best]}={_phase[best][frame]:0.00}");
                }

                return top.ToString();
            }

            string topFrame = Top(worst), topWork = Top(worstWork);
            sb.Append(inv, $"  frame max {frameMax:0.00} ms, hitches >8 ms {hitch8}, >16 ms {hitch16}; without Present: max {cpuMax:0.00} ms, >8 ms {cpuHitch8}, >16 ms {cpuHitch16}; worst frame: {topFrame}\n");
            sb.Append(inv, $"  Update+Draw max {workMax:0.00} ms, >8 ms {workHitch8}, >16 ms {workHitch16}; worst: {topWork}\n");
            json.Append(inv, $",\"frame_max\":{frameMax:0.000},\"hitch8\":{hitch8},\"hitch16\":{hitch16},\"cpu_max\":{cpuMax:0.000},\"cpu_hitch8\":{cpuHitch8},\"cpu_hitch16\":{cpuHitch16},\"worst\":\"{topFrame}\"");
            json.Append(inv, $",\"work_max\":{workMax:0.000},\"work_hitch8\":{workHitch8},\"work_hitch16\":{workHitch16},\"worst_work\":\"{topWork}\"");

            json.Append(",\"counts\":{");
            sb.Append("  count                      avg      p50      p95      max\n");
            first = true;

            for (int c = 0; c < COUNTERS; c++)
            {
                if (!On && c != (int)FrameCounter.AllocBytes)
                {
                    continue;
                }

                StatsI(_counter[c], n, _sortScratchI, out double avg, out int p50, out int p95, out int max);
                sb.Append(inv, $"  {_counterKeys[c],-24} {avg,7:0.0}  {p50,7}  {p95,7}  {max,7}\n");
                json.Append(first ? "" : ",").Append(inv, $"\"{_counterKeys[c]}\":[{avg:0.0},{p50},{p95},{max}]");
                first = false;
            }

            if (On)
            {
                int controls = 0;

                foreach (var g in UIManager.Gumps)
                {
                    controls += CountControls(g);
                }

                sb.Append(inv, $"  controls (at report)     {controls}\n");
                json.Append(inv, $"}},\"controls\":{controls}}}");
            }
            else
            {
                json.Append("}}");
            }

            Log.Info(sb.ToString());
            Log.Info(json.ToString());
            _count = 0;
        }

        private static int CountControls(Control c)
        {
            int n = 1;

            foreach (var child in c.Children)
            {
                n += CountControls(child);
            }

            return n;
        }

        private static void Stats(float[] values, int n, float[] scratch, out double avg, out double p50, out double p95, out double p99)
        {
            avg = p50 = p95 = p99 = 0;

            if (n == 0)
            {
                return;
            }

            double sum = 0;

            for (int i = 0; i < n; i++)
            {
                sum += values[i];
            }

            Array.Copy(values, scratch, n);
            Array.Sort(scratch, 0, n);
            avg = sum / n;
            p50 = scratch[n / 2];
            p95 = scratch[(int)(n * 0.95)];
            p99 = scratch[(int)(n * 0.99)];
        }

        private static void StatsI(int[] values, int n, int[] scratch, out double avg, out int p50, out int p95, out int max)
        {
            avg = 0;
            p50 = p95 = max = 0;

            if (n == 0)
            {
                return;
            }

            long sum = 0;

            for (int i = 0; i < n; i++)
            {
                sum += values[i];
            }

            Array.Copy(values, scratch, n);
            Array.Sort(scratch, 0, n);
            avg = sum / (double)n;
            p50 = scratch[n / 2];
            p95 = scratch[(int)(n * 0.95)];
            max = scratch[n - 1];
        }
    }
}
