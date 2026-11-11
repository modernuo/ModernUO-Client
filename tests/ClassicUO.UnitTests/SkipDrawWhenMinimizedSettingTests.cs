using System.IO;
using ClassicUO.Configuration;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests
{
    // skip_draw_when_minimized: default on; the key loads in both states. The draw skip itself lives in
    // GameController.Update and needs a window, so it is exercised by the DevBench plan entries skipmin: and minimized:.
    public class SkipDrawWhenMinimizedSettingTests
    {
        [Fact]
        public void Default_Is_On()
        {
            new Settings().SkipDrawWhenMinimized.Should().BeTrue();
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        public void Key_Loads_In_Both_States(string value, bool expected)
        {
            Load($"{{\n  \"username\": \"tester\",\n  \"skip_draw_when_minimized\": {value}\n}}\n").SkipDrawWhenMinimized.Should().Be(expected);
        }

        [Fact]
        public void Absent_Key_Loads_As_On()
        {
            Load("{\n  \"username\": \"tester\"\n}\n").SkipDrawWhenMinimized.Should().BeTrue();
        }

        private static Settings Load(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"cuo-skipmin-{System.Guid.NewGuid():N}.json");

            try
            {
                File.WriteAllText(path, json);

                return ConfigurationResolver.Load(path, SettingsJsonContext.RealDefault.Settings);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
