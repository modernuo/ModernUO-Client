using System.Text.Json;
using ClassicUO.Configuration;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    /// <summary>
    /// worldmap-audit W-11: the world map's "show marker icons" toggle is kept in the profile (WorldMapShowMarkerIcons).
    /// Default true (the behaviour before it was saved); a profile written before the key existed loads as true.
    /// </summary>
    public sealed class WorldMapProfileTests
    {
        [Fact]
        public void Show_Marker_Icons_Defaults_On_And_Round_Trips()
        {
            new Profile().WorldMapShowMarkerIcons.Should().BeTrue();

            var profile = new Profile { WorldMapShowMarkerIcons = false };
            string json = JsonSerializer.Serialize(profile, ProfileJsonContext.DefaultToUse.Profile);

            json.Should().Contain("\"world_map_show_marker_icons\"");
            JsonSerializer.Deserialize(json, ProfileJsonContext.DefaultToUse.Profile).WorldMapShowMarkerIcons.Should().BeFalse();

            string old = json.Replace("\"world_map_show_marker_icons\"", "\"world_map_show_marker_icons_removed\"");
            JsonSerializer.Deserialize(old, ProfileJsonContext.DefaultToUse.Profile).WorldMapShowMarkerIcons.Should().BeTrue();
        }
    }
}
