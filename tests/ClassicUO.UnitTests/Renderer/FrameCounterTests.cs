using System.Linq;
using System.Reflection;
using Xunit;

namespace ClassicUO.UnitTests.Renderer;

public class FrameCounterTests
{
    [Fact]
    public void EveryFrameCounterHasOneStableJsonKey()
    {
        var keys = (string[])typeof(FrameStats).GetField("_counterKeys", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.Equal((int)FrameCounter.Count, keys.Length);
        Assert.All(keys, key => Assert.False(string.IsNullOrEmpty(key)));
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Equal("glyph_quads", keys[(int)FrameCounter.GlyphQuads]);
        Assert.Equal("glyph_quads_built", keys[(int)FrameCounter.GlyphQuadsBuilt]);
        Assert.Equal("glyph_quads_reused", keys[(int)FrameCounter.GlyphQuadsReused]);
    }
}
