using System;
using System.IO;
using ClassicUO.Configuration;
using Xunit;

namespace ClassicUO.UnitTests;

// mount-fix (owner 2026-11-23): mobile_fade_in defaults off, fade_time_based defaults on; a saved value is kept either way.
public class FadeDefaultSettingTests
{
    [Fact]
    public void Defaults()
    {
        var s = new Settings();
        Assert.False(s.MobileFadeIn);
        Assert.True(s.FadeTimeBased);
        Assert.True(s.ItemFadeIn);
    }

    [Fact]
    public void Settings_Without_The_Keys_Load_The_New_Defaults()
    {
        WithFile("{}", path =>
        {
            Settings s = Load(path);
            Assert.False(s.MobileFadeIn);
            Assert.True(s.FadeTimeBased);
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Saved_Values_Round_Trip(bool mobileFadeIn, bool timeBased)
    {
        WithFile("{}", path =>
        {
            ConfigurationResolver.Save(new Settings { MobileFadeIn = mobileFadeIn, FadeTimeBased = timeBased }, path, SettingsJsonContext.RealDefault.Settings);
            Settings s = Load(path);
            Assert.Equal(mobileFadeIn, s.MobileFadeIn);
            Assert.Equal(timeBased, s.FadeTimeBased);
        });
    }

    [Theory]
    [InlineData("{\"mobile_fade_in\":true,\"fade_time_based\":false}", true, false)]
    [InlineData("{\"mobile_fade_in\":false,\"fade_time_based\":true}", false, true)]
    public void Old_Written_Values_Are_Kept(string json, bool mobileFadeIn, bool timeBased)
    {
        WithFile(json, path =>
        {
            Settings s = Load(path);
            Assert.Equal(mobileFadeIn, s.MobileFadeIn);
            Assert.Equal(timeBased, s.FadeTimeBased);
        });
    }

    private static Settings Load(string path) => ConfigurationResolver.Load(path, SettingsJsonContext.RealDefault.Settings);

    private static void WithFile(string json, Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cuo-fade-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            action(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
