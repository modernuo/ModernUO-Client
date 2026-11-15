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
    }
}
