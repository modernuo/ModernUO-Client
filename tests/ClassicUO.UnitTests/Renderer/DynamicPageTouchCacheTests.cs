using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Uo.Render;
using Xunit;

namespace ClassicUO.UnitTests.Renderer;

public class DynamicPageTouchCacheTests
{
    static SpriteInstance Sprite(int page, bool indexed = false) => new()
    {
        PageHue = (uint)page,
        Whf = indexed ? (uint)SpriteInstance.FlagIndexed << 24 : 0
    };

    [Fact]
    public void TracksExactPageSequenceAndSeparatesArtFromIndexedPages()
    {
        var cache = new DynamicPageTouchCache(8);
        SpriteInstance[] sprites = [Sprite(3), Sprite(3), Sprite(3, true), Sprite(7)];
        Assert.Equal(new uint[] { 3, 0x10003, 7 }, cache.Update(sprites).ToArray());
        sprites[0].PageHue |= 42u << 16; // hue changes don't change residency
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) cache.Update(sprites);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        sprites[1] = Sprite(9);
        Assert.Equal(new uint[] { 3, 9, 0x10003, 7 }, cache.Update(sprites).ToArray());
        Assert.Equal(new uint[] { 3, 9 }, cache.Update(sprites.AsSpan(0, 2)).ToArray());
        Assert.Empty(cache.Update([]).ToArray());
        Assert.Equal(new uint[] { 9, 3 }, cache.Update(new[] { Sprite(9), Sprite(3) }).ToArray());
    }

    static void Set(object o, string name, object value) => o.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(o, value);

    static PageResidency ResidentPages()
    {
        var pages = (PageResidency)RuntimeHelpers.GetUninitializedObject(typeof(PageResidency));
        Set(pages, "LogicalCount", 3);
        Set(pages, "_layerOf", new[] { 0, 1, -1 });
        Set(pages, "_logicalOf", new[] { 0, 1 });
        Set(pages, "_layerUsed", new long[] { 0, 0 });
        return pages;
    }

    [Fact]
    public void IndexedBaseAndEvictedLayerAreRecheckedEvenForIdenticalInput()
    {
        var art = ResidentPages();
        var anim = ResidentPages();
        var cache = new DynamicPageTouchCache(8);
        SpriteInstance[] sprites = [Sprite(0), Sprite(10, true), Sprite(10, true)];
        Assert.True(cache.TouchResident(sprites, art, anim, 10, 100, true));
        var field = typeof(PageResidency).GetField("_layerUsed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(new long[] { 100, 0 }, (long[])field.GetValue(art)!);
        Assert.Equal(new long[] { 100, 0 }, (long[])field.GetValue(anim)!);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Assert.True(cache.TouchResident(sprites, art, anim, 10, 101 + i, true));
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        // Same logical sequence, different physical residency: no stale layer pinning and no partial touch before fallback.
        Set(anim, "_layerOf", new[] { -1, 1, -1 });
        Assert.False(cache.TouchResident(sprites, art, anim, 10, 2000, true));
        Assert.Equal(1100, ((long[])field.GetValue(art)!)[0]);
        Assert.True(cache.TouchResident(sprites, art, null, 10, 2000, true)); // old indexed path is a no-op without an atlas
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefreshPreservesEvictionAndMissingRequestCounts(bool enabled)
    {
        var pages = (PageResidency)RuntimeHelpers.GetUninitializedObject(typeof(PageResidency));
        Set(pages, "LogicalCount", 3);
        Set(pages, "_layerOf", new[] { 0, 1, -1 });
        Set(pages, "_logicalOf", new[] { 0, 1 });
        long[] used = [0, 0]; Set(pages, "_layerUsed", used);
        Set(pages, "<Layers>k__BackingField", 2);
        Set(pages, "_requested", new long[3]);
        var stateType = typeof(PageResidency).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType.GetElementType()!;
        Set(pages, "_state", Array.CreateInstance(stateType, 3));
        Set(pages, "_queue", new int[3]);
        var cache = new DynamicPageTouchCache(8);
        SpriteInstance[] sprites = [Sprite(0), Sprite(0)];
        Assert.Equal(enabled, cache.TouchResident(sprites, pages, null, 0, 100, enabled));
        if (!enabled) foreach (var sprite in sprites) pages.Touch(sprite.Page, 100);
        Assert.Equal(new long[] { 100, 0 }, used);
        int victim = (int)typeof(PageResidency).GetMethod("PickLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pages, [100L])!;
        Assert.Equal(1, victim);
        // Long settled views still refresh exact timestamps before motion/eviction resumes.
        cache.TouchResident(sprites, pages, null, 0, 1000, true);
        Assert.Equal(1000, used[0]);
        sprites[0] = Sprite(2); sprites[1] = Sprite(2);
        Assert.False(cache.TouchResident(sprites, pages, null, 0, 1001, enabled));
        foreach (var sprite in sprites) pages.Touch(sprite.Page, 1001);
        Assert.Equal(2, pages.MissingVisible);
        Assert.Equal(1000, used[0]);
        Assert.Equal(1001, ((long[])typeof(PageResidency).GetField("_requested", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pages)!)[2]);
    }
}
