using System;
using System.IO;
using System.Text.Json.Nodes;
using ClassicUO.Configuration;
using Xunit;

namespace ClassicUO.UnitTests;

public class PresentDefaultSettingTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"present_at_display_rate\":false}")]
    [InlineData("{\"present_at_display_rate\":true,\"settings_version\":0}")]
    public void OldSettingsMigrateAndPersistOnce(string json)
    {
        WithFile(json, path =>
        {
            Assert.True(Load(path).PresentAtDisplayRate);
            var saved = JsonNode.Parse(File.ReadAllText(path));
            Assert.Equal(1, saved["settings_version"].GetValue<int>());
            saved["present_at_display_rate"] = false;
            File.WriteAllText(path, saved.ToJsonString());
            Assert.False(Load(path).PresentAtDisplayRate);
            Assert.False(Load(path).PresentAtDisplayRate);
        });
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void VersionedChoiceIsKeptWithoutRewriting(int version, bool on)
    {
        string json = $"{{\"settings_version\":{version},\"present_at_display_rate\":{on.ToString().ToLowerInvariant()}}}";
        WithFile(json, path =>
        {
            Assert.Equal(on, Load(path).PresentAtDisplayRate);
            Assert.Equal(json, File.ReadAllText(path));
        });
    }

    [Fact]
    public void NewSettingsSaveVersionAndKeepExplicitFalse()
    {
        WithFile("{}", path =>
        {
            ConfigurationResolver.Save(new Settings { PresentAtDisplayRate = false }, path, SettingsJsonContext.RealDefault.Settings);
            Assert.False(Load(path).PresentAtDisplayRate);
        });
    }

    private static Settings Load(string path) => ConfigurationResolver.Load(path, SettingsJsonContext.RealDefault.Settings);

    private static void WithFile(string json, Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cuo-present-{Guid.NewGuid():N}.json");
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
