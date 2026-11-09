using System.IO;
using ClassicUO.Configuration;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T9 deleted ecs_effects with its settings.json key. A settings.json written by an older client still holds the key; Main
    // loads the file through ConfigurationResolver.Load with SettingsJsonContext, which must skip the unknown key and keep the
    // other values. (The same test as RemovedSwitchSettingsTests of the first T9 slice, on its own until both are merged.)
    public class RemovedEffectSwitchSettingsTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Settings_File_With_The_Removed_Effects_Key_Loads(bool value)
        {
            string path = Path.Combine(Path.GetTempPath(), $"cuo-removed-effects-key-{System.Guid.NewGuid():N}.json");

            try
            {
                File.WriteAllText(path, $"{{\n  \"username\": \"tester\",\n  \"ecs_effects\": {(value ? "true" : "false")},\n  \"fps\": 144\n}}\n");

                Settings settings = ConfigurationResolver.Load(path, SettingsJsonContext.RealDefault.Settings);

                settings.Should().NotBeNull();
                settings.Username.Should().Be("tester");
                settings.FPS.Should().Be(144);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
