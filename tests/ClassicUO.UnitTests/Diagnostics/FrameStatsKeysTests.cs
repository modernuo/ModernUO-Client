using System.Reflection;
using Xunit;

namespace ClassicUO.UnitTests.Diagnostics;

public class FrameStatsKeysTests
{
    // The FRAMESTATS JSON names phases by index into _phaseKeys; a key left behind when a phase is removed shifts every
    // later key (the plugin host removal did, so bench tools read GameScene.Update as "plugin" and frame as 0).
    [Theory]
    [InlineData("_phaseNames", (int)FramePhase.Count)]
    [InlineData("_phaseKeys", (int)FramePhase.Count)]
    [InlineData("_counterKeys", (int)FrameCounter.Count)]
    public void Name_Tables_Match_Their_Enum(string field, int count)
    {
        var names = (string[])typeof(FrameStats).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        Assert.Equal(count, names.Length);
    }

    [Fact]
    public void Phase_Keys_Line_Up_With_Their_Phases()
    {
        var keys = (string[])typeof(FrameStats).GetField("_phaseKeys", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        Assert.Equal("scene_update", keys[(int)FramePhase.SceneUpdate]);
        Assert.Equal("update", keys[(int)FramePhase.Update]);
        Assert.Equal("draw", keys[(int)FramePhase.Draw]);
        Assert.Equal("frame", keys[(int)FramePhase.Frame]);
        Assert.Equal("fill_loop", keys[(int)FramePhase.FillLoop]);
        Assert.Equal("prep_band_masks", keys[(int)FramePhase.PrepBandMasks]);
        Assert.Equal("prep_zband", keys[(int)FramePhase.PrepZBand]);
        Assert.Equal("chunk_lights", keys[(int)FramePhase.ChunkLights]);
        Assert.Equal("gpu_settle", keys[(int)FramePhase.GpuSettle]);
        Assert.Equal("light_search", keys[(int)FramePhase.LightSearch]);
        Assert.Equal("mwo_resolve", keys[(int)FramePhase.MwoResolve]);
        Assert.Equal("mwo_scan", keys[(int)FramePhase.MwoScan]);
    }

    [Fact]
    public void Counter_Keys_Line_Up_With_Their_Walk_Consumers()
    {
        var keys = (string[])typeof(FrameStats).GetField("_counterKeys", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        Assert.Equal("band_chunks", keys[(int)FrameCounter.BandChunks]);
        Assert.Equal("lo_chunks", keys[(int)FrameCounter.LoChunks]);
        Assert.Equal("lo_tiles", keys[(int)FrameCounter.LoTiles]);
        Assert.Equal("lo_objects", keys[(int)FrameCounter.LoObjects]);
        Assert.Equal("lo_candidates", keys[(int)FrameCounter.LoCandidates]);
        Assert.Equal("light_search_passes", keys[(int)FrameCounter.LightSearchPasses]);
        Assert.Equal("light_candidate_reads", keys[(int)FrameCounter.LightCandidateReads]);
        Assert.Equal("light_unique_candidates", keys[(int)FrameCounter.LightUniqueCandidates]);
        Assert.Equal("light_result_queries", keys[(int)FrameCounter.LightResultQueries]);
        Assert.Equal("light_result_hits", keys[(int)FrameCounter.LightResultHits]);
        Assert.Equal("light_measure_overflow", keys[(int)FrameCounter.LightMeasureOverflow]);
        Assert.Equal("emitter_rebuilds", keys[(int)FrameCounter.EmitterRebuilds]);
        Assert.Equal("emitter_overflows", keys[(int)FrameCounter.EmitterOverflows]);
        Assert.Equal("emitter_visited_bytes", keys[(int)FrameCounter.EmitterVisitedBytes]);
        Assert.Equal("chunk_light_chunks", keys[(int)FrameCounter.ChunkLightChunks]);
        Assert.Equal("chunk_light_tiles", keys[(int)FrameCounter.ChunkLightTiles]);
        Assert.Equal("chunk_light_objects", keys[(int)FrameCounter.ChunkLightObjects]);
        Assert.Equal("gpu_settle_chunks", keys[(int)FrameCounter.GpuSettleChunks]);
        Assert.Equal("gpu_settle_tiles", keys[(int)FrameCounter.GpuSettleTiles]);
        Assert.Equal("gpu_settle_objects", keys[(int)FrameCounter.GpuSettleObjects]);
        Assert.Equal("gpu_mask_tiles", keys[(int)FrameCounter.GpuMaskTiles]);
        Assert.Equal("gpu_mask_objects", keys[(int)FrameCounter.GpuMaskObjects]);
    }

    [Fact]
    public void Prep_Rest_Excludes_Settle_And_Preserves_Inclusive_Subsets()
    {
        var acc = (long[])typeof(FrameStats).GetField("_acc", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var saved = (long[])acc.Clone();
        try
        {
            System.Array.Clear(acc);
            acc[(int)FramePhase.WorldPrepare] = 1000;
            acc[(int)FramePhase.PrepWalk] = 200;
            acc[(int)FramePhase.PrepFoliageFinish] = 100;
            acc[(int)FramePhase.GpuSettle] = 300;
            acc[(int)FramePhase.PrepZBand] = 40;
            acc[(int)FramePhase.PrepBandMasks] = 60;
            // These are subsets of existing parents, not additional exclusive prepare phases.
            acc[(int)FramePhase.FillLoop] = 400;
            acc[(int)FramePhase.LightSearch] = 50;
            acc[(int)FramePhase.ChunkLights] = 60;
            acc[(int)FramePhase.MwoResolve] = 70;
            acc[(int)FramePhase.MwoScan] = 30;

            typeof(FrameStats).GetMethod("CompletePhases", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);

            Assert.Equal(300, acc[(int)FramePhase.PrepRest]);
        }
        finally
        {
            saved.CopyTo(acc, 0);
        }
    }
}
