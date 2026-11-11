using System.IO;
using ClassicUO.Configuration;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests
{
    // throttle_update_when_inactive: default off (unmeasured); the key loads in both states. The sleep itself is in
    // GameController.Update and needs a window.
    public class ThrottleUpdateWhenInactiveSettingTests
    {
        [Fact]
        public void Default_Is_Off()
        {
            new Settings().ThrottleUpdateWhenInactive.Should().BeFalse();
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        public void Key_Loads_In_Both_States(string value, bool expected)
        {
            string path = Path.Combine(Path.GetTempPath(), $"cuo-throttle-{System.Guid.NewGuid():N}.json");

            try
            {
                File.WriteAllText(path, $"{{\n  \"username\": \"tester\",\n  \"throttle_update_when_inactive\": {value}\n}}\n");

                ConfigurationResolver.Load(path, SettingsJsonContext.RealDefault.Settings).ThrottleUpdateWhenInactive.Should().Be(expected);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
