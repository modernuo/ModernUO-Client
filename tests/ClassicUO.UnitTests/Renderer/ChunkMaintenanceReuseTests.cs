using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Renderer;

// BakeJob.Pack is process-wide; do not run these synthetic stores beside another renderer test.
[Collection("AnimationRolls")]
public class ChunkMaintenanceReuseTests
{
    static void Set(object obj, string field, object value) => obj.GetType().GetField(field,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(obj, value);
    static T Get<T>(object obj, string field) => (T)obj.GetType().GetField(field,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(obj)!;

    static PackSet Pack()
    {
        var pack = (PackSet)RuntimeHelpers.GetUninitializedObject(typeof(PackSet));
        Set(pack, "BlocksX", 1); Set(pack, "BlocksY", 1); Set(pack, "RegionBlocks", 8);
        return pack;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void ResumedMotionPreservesLastNeededAgeThenEventuallyEvicts(bool reuse)
    {
        var previous = BakeJob.Pack;
        try
        {
            var store = new ChunkStore(Pack(), null, 32);
            store.InjectDebugChunk([], 0);
            uint offset = 0;
            Assert.Equal(1, store.StageUploads(null, ref offset, 1024, 64));
            var map = Get<Dictionary<int, int>>(store, "_map");
            int slot = map[-1]; map.Remove(-1); map[0] = slot;
            Get<int[]>(store, "_key")[slot] = 0;
            var near = new RectI(0, 0, 44, 44);
            store.Update(10, near, near);
            Assert.True(store.CanReuseMaintenance);
            long oldEpoch = store.ChangeEpoch;
            long stale = Stopwatch.GetTimestamp() - 2 * Stopwatch.Frequency;
            Get<long[]>(store, "_lastNeededTicks")[slot] = stale;
            if (reuse) store.ReuseMaintenance(1000);
            else store.Update(1000, near, near);
            var far = new RectI(100000, 100000, 100100, 100100);
            store.Update(1001, far, far);
            Assert.Equal(1, store.ActiveChunks);
            Assert.Equal(1000, Get<long[]>(store, "_lastNeeded")[slot]);
            Assert.True(Get<long[]>(store, "_lastNeededTicks")[slot] > stale);
            Assert.Equal(oldEpoch, store.ChangeEpoch);
            Assert.False(store.CanReuseMaintenance);
            Get<long[]>(store, "_lastNeededTicks")[slot] = stale;
            store.Update(1030, far, far); // frame hysteresis still protects it
            Assert.Equal(1, store.ActiveChunks);
            store.Update(1031, far, far);
            Assert.Equal(0, store.ActiveChunks);
            Assert.True(store.ChangeEpoch > oldEpoch);
        }
        finally { BakeJob.Pack = previous; }
    }

    [Fact]
    public unsafe void CachedUniquePageTouchesProtectExactlyTheVisibleLayers()
    {
        var previous = BakeJob.Pack;
        try
        {
            var store = new ChunkStore(Pack(), null, 32);
            SpriteInstance[] sprites = [new() { PageHue = 0, Whf = 1 | 1 << 12 },
                new() { PageHue = 1, Whf = 1 | 1 << 12 }];
            store.InjectDebugChunk(sprites, sprites.Length);
            store.InjectDebugChunk(sprites, sprites.Length); // pages shared by two visible chunks
            byte* mapped = stackalloc byte[1024];
            uint offset = 0;
            store.StageUploads(mapped, ref offset, 1024, 64);
            var pages = (PageResidency)RuntimeHelpers.GetUninitializedObject(typeof(PageResidency));
            Set(pages, "LogicalCount", 3);
            Set(pages, "_layerOf", new[] { 0, 1, 2 });
            Set(pages, "_logicalOf", new[] { 0, 1, 2 });
            Set(pages, "_layerUsed", new long[] { 0, 0, 0 });
            Set(pages, "<Layers>k__BackingField", 3);
            var table = new ChunkDraw[8];
            int count = store.BuildDrawList(new RectI(-1, -1, 2, 2), table, pages, 10, out int instances);
            Assert.Equal(2, count); Assert.Equal(4, instances);
            store.TouchVisiblePages(pages, 1000);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) store.TouchVisiblePages(pages, 1000);
            Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
            Assert.Equal(new long[] { 1000, 1000, 0 }, Get<long[]>(pages, "_layerUsed"));
            int victim = (int)typeof(PageResidency).GetMethod("PickLayer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pages, [1000L])!;
            Assert.Equal(2, victim); // untouched layer only, same decision as a full draw-list walk
            var fresh = new ChunkDraw[8];
            Assert.Equal(count, store.BuildDrawList(new RectI(-1, -1, 2, 2), fresh, pages, 1000, out int freshInstances));
            Assert.Equal(instances, freshInstances);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(table[i].First, fresh[i].First);
                Assert.Equal(table[i].Prefix, fresh[i].Prefix);
            }
        }
        finally { BakeJob.Pack = previous; }
    }
}
