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
    }
}
