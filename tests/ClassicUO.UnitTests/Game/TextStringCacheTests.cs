using System.IO;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    // StringHelper.Cp1252Cache is global: tests that set it share one collection (not run in parallel).
    [Collection("Cp1252Cache")]
    public class TextStringCacheTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Enable_Follows_The_Switch(bool on)
        {
            Cp1252StringCache saved = StringHelper.Cp1252Cache;

            try
            {
                TextStringCache.OnOverride = on;
                TextStringCache.Enable();

                (StringHelper.Cp1252Cache != null).Should().Be(on);
                StringHelper.Cp1252ToString("Vendor: Bob"u8).Should().Be("Vendor: Bob");
                TextStringCache.Summary().Should().Contain(on ? "text_string_cache on" : "text_string_cache off");
            }
            finally
            {
                TextStringCache.OnOverride = null;
                StringHelper.Cp1252Cache = saved;
                StringHelper.Cp1252CacheCheck = null;
            }
        }

        [Fact]
        public void Default_Is_On()
        {
            new Settings().TextStringCache.Should().BeTrue();
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        public void Key_Loads_In_Both_States(string value, bool expected)
        {
            Load($"{{\n  \"username\": \"tester\",\n  \"text_string_cache\": {value}\n}}\n").TextStringCache.Should().Be(expected);
        }

        [Fact]
        public void Absent_Key_Loads_As_On()
        {
            Load("{\n  \"username\": \"tester\"\n}\n").TextStringCache.Should().BeTrue();
        }

        private static Settings Load(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"cuo-textcache-{System.Guid.NewGuid():N}.json");

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
