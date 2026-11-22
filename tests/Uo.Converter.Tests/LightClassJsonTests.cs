using FluentAssertions;
using Uo.Converter;
using Uo.Pack;
using Xunit;

namespace Uo.Converter.Tests;

/// <summary>light_alpha_fade: the light shape classes as JSON (--light-classes, --export-light-classes).</summary>
public sealed class LightClassJsonTests
{
    [Fact]
    public void Seed_round_trips_through_json()
    {
        byte[] seed = LightClassFile.Seed();
        LightClassJson.Parse(LightClassJson.Export(seed)).Should().Equal(seed);
    }

    [Fact]
    public void A_shards_table_lists_its_own_shapes()
    {
        byte[] t = LightClassJson.Parse("{ \"lightClasses\": 1, \"surface\": [26, 27, 60], // custom window\n \"radial\": [1] }");
        t[60].Should().Be((byte)LightClass.Surface);
        t[1].Should().Be((byte)LightClass.Radial);
        t[2].Should().Be((byte)LightClass.Unlisted);
    }

    [Theory]
    [InlineData("{ \"surface\": [1] }", "missing")]
    [InlineData("{ \"lightClasses\": 2 }", "must be 1")]
    [InlineData("{ \"lightClasses\": 1, \"windows\": [1] }", "unknown key")]
    [InlineData("{ \"lightClasses\": 1, \"surface\": [1], \"radial\": [1] }", "twice")]
    [InlineData("{ \"lightClasses\": 1, \"surface\": [256] }", "outside")]
    [InlineData("{ \"lightClasses\": 1, \"surface\": [\"26\"] }", "not a light id")]
    [InlineData("[1, 2]", "object")]
    [InlineData("{", "not JSON")]
    public void Bad_input_is_refused_with_the_reason(string json, string reason)
    {
        var act = () => LightClassJson.Parse(json);
        act.Should().Throw<LightClassJsonException>().Where(e => e.Message.Contains(reason));
    }
}
