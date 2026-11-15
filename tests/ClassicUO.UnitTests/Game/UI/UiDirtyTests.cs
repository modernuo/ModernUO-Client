using ClassicUO.Configuration;
using ClassicUO.Game;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Game.UI;

public class UiDirtyTests
{
    private sealed class Probe : Control { }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeometryAndStyleMutationsInvalidateAncestors(bool enabled)
    {
        var settings = new Settings { UiGumpDirty = enabled };
        settings.UiGumpDirty.Should().Be(enabled);
        var root = new Probe();
        var child = new Probe();
        root.Add(child);
        long version = root.DrawVersion;
        child.X++;
        root.DrawVersion.Should().BeGreaterThan(version);
        version = root.DrawVersion;
        child.Bounds = new Rectangle(2, 3, 40, 50);
        root.DrawVersion.Should().BeGreaterThan(version);
        version = root.DrawVersion;
        child.Alpha = .5f;
        child.IsVisible = false;
        child.ActivePage++;
        root.DrawVersion.Should().BeGreaterThan(version);
        version = root.DrawVersion;
        child.X = child.X;
        child.Alpha = child.Alpha;
        root.DrawVersion.Should().Be(version);
    }

    [Fact]
    public void DirectChildCollectionChangesInvalidateAndReparentTracksBothTrees()
    {
        var a = new Probe();
        var b = new Probe();
        var child = new Probe();
        a.Add(child);
        long av = a.DrawVersion, bv = b.DrawVersion;
        b.Add(child);
        a.DrawVersion.Should().BeGreaterThan(av);
        b.DrawVersion.Should().BeGreaterThan(bv);
        bv = b.DrawVersion;
        b.Children.Clear();
        b.DrawVersion.Should().BeGreaterThan(bv);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchSelectsRetainedTreeCommandsOnlyInsideAnOuterRecording(bool enabled)
    {
        var root = new Gump(null, 0, 0);
        var commands = new RenderLists();
        float depth = -100;
        UiGumpRecording.TryQueue(root, commands, ref depth, enabled, true).Should().Be(enabled);
        commands.GumpTextsCount.Should().Be(enabled ? 1 : 0);
        UiGumpRecording.TryQueue(root, commands, ref depth, enabled, false).Should().BeFalse();
    }

    [Fact]
    public void AssigningSameLabelTextStillRestoresItsLayoutDimensions()
    {
        var label = (Label)RuntimeHelpers.GetUninitializedObject(typeof(Label));
        var text = (RenderedText)RuntimeHelpers.GetUninitializedObject(typeof(RenderedText));
        typeof(RenderedText).GetField("_text", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(text, "same");
        typeof(Label).GetField("_gText", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(label, text);
        label.Width = 40;
        label.Height = 50;
        label.Text = "same";
        label.Width.Should().Be(text.Width);
        label.Height.Should().Be(text.Height);
    }

    [Fact]
    public void RepeatedChildEnumerationAllocatesNothing()
    {
        var root = new Probe();
        root.Add(new Probe());
        int total = 0;
        for (int i = 0; i < 100; i++) foreach (var child in root.Children) total += child.Width;
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) foreach (var child in root.Children) total += child.Width;
        (System.GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        total.Should().Be(0);
    }

    [Fact]
    public void UnrecognizedControlsAreAlwaysDirty()
    {
        var root = new Gump(null, 0, 0);
        UiGumpRecording.IsTreeSupported(root).Should().BeTrue();
        root.Add(new Probe());
        UiGumpRecording.IsTreeSupported(root).Should().BeFalse();
    }
}
