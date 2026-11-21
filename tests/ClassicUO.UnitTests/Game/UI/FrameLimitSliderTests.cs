using ClassicUO.Game;
using ClassicUO.Game.UI.Gumps;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.UI
{
    /// <summary>The options' single frame-limit slider: its top step "Off" is Settings.FPS = 0 (uncapped), as before.</summary>
    public sealed class FrameLimitSliderTests
    {
        [Fact]
        public void OldSettingsWithFpsZero_ShowOff_AndSaveZero()
        {
            int slider = FrameLimitSlider.FromSetting(0);
            slider.Should().Be(FrameLimitSlider.Off);
            FrameLimitSlider.ToSetting(slider).Should().Be(0);
        }

        [Theory]
        [InlineData(Constants.MIN_FPS)]
        [InlineData(60)]
        [InlineData(240)]
        [InlineData(Constants.MAX_FPS)]
        public void ACap_RoundTrips(int fps)
        {
            FrameLimitSlider.ToSetting(FrameLimitSlider.FromSetting(fps)).Should().Be(fps);
        }

        [Fact]
        public void TheOffStep_IsAboveEveryCap()
        {
            FrameLimitSlider.Off.Should().BeGreaterThan(Constants.MAX_FPS);
            FrameLimitSlider.FromSetting(-1).Should().Be(FrameLimitSlider.Off);
            FrameLimitSlider.FromSetting(Constants.MAX_FPS + 500).Should().Be(Constants.MAX_FPS);
        }
    }
}
