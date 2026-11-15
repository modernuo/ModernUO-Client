using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace ClassicUO.UnitTests.Renderer;

public class SpriteFragmentTests
{
    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    // Recording/copy tests need the device's managed state only. Never construct or call a native device.
    private static UltimaBatcher2D Batcher()
    {
        var device = (GraphicsDevice)RuntimeHelpers.GetUninitializedObject(typeof(GraphicsDevice));
        GC.SuppressFinalize(device);
        Set(device, "INTERNAL_viewport", new Viewport(0, 0, 640, 480));
        Set(device, "INTERNAL_scissorRectangle", new Rectangle(0, 0, 640, 480));
        var rasterizer = new RasterizerState();
        Set(device, "cachedRasterizerState", rasterizer);
        var batcher = (UltimaBatcher2D)RuntimeHelpers.GetUninitializedObject(typeof(UltimaBatcher2D));
        GC.SuppressFinalize(batcher);
        Set(batcher, "<GraphicsDevice>k__BackingField", device);
        Set(batcher, "_rasterizerState", rasterizer);
        Set(batcher, "_recordPass", new RecordedPass());
        Set(batcher, "_recSegments", new List<RecordedSegment>());
        Set(batcher, "_vertexInfo", new UltimaBatcher2D.PositionNormalTextureColor4[16]);
        Set(batcher, "_textureInfo", new Texture2D[16]);
        Set(batcher, "_transformMatrix", Matrix.Identity);
        return batcher;
    }

    private static SpriteFragment Capture(UltimaBatcher2D batcher, float x)
    {
        var mark = batcher.BeginFragment();
        var vertices = (UltimaBatcher2D.PositionNormalTextureColor4[])typeof(UltimaBatcher2D)
            .GetField("_vertexInfo", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(batcher)!;
        vertices[mark.Sprite].Position0.X = x;
        Set(batcher, "_numSprites", mark.Sprite + 1);
        var result = new SpriteFragment();
        batcher.EndFragment(result, mark);
        return result;
    }

    [Fact]
    public void FragmentReplayPreservesGeometryAndNormalizesSpriteOffsets()
    {
        var batcher = Batcher();
        var original = Capture(batcher, 123);
        Capture(batcher, 456);
        var mark = batcher.BeginFragment();
        batcher.AppendFragment(original);
        var replayed = new SpriteFragment();
        batcher.EndFragment(replayed, mark);
        Assert.Equal(1, replayed.Count);
        Assert.True(original.SameAs(replayed));
        Assert.False(original.SameAs(Capture(batcher, 124)));
    }

    [Fact]
    public void ViewportChangeRejectsInheritedRecordingEnvironment()
    {
        var batcher = Batcher();
        var original = Capture(batcher, 0);
        Assert.True(batcher.FragmentEnvironmentMatches(original));
        Set(batcher.GraphicsDevice, "INTERNAL_viewport", new Viewport(0, 0, 800, 600));
        Assert.False(batcher.FragmentEnvironmentMatches(original));
    }

    [Fact]
    public void ShadowRewindRestoresPendingGeometryAndBeginState()
    {
        var batcher = Batcher();
        Capture(batcher, 12);
        Set(batcher, "_recBegun", true);
        var mark = batcher.BeginFragment();
        Capture(batcher, 99);
        batcher.RewindFragment(mark);
        Assert.Equal(1, batcher.PendingSprites);
        Assert.True(batcher.BeginFragment().Begun);
    }
}
