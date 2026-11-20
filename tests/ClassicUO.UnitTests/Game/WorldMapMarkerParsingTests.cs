using System.Collections.Generic;
using System.IO;
using System.Text;
using ClassicUO.Game.UI.Gumps;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game
{
    /// <summary>
    /// worldmap-audit W-2/W-3: a blank line in a CSV marker file ended LoadMarkers (no marker of any file was drawn), and a
    /// malformed line in any marker file threw out of the world map gump's constructor. Bad lines are now skipped.
    /// </summary>
    public sealed class WorldMapMarkerParsingTests
    {
        private static List<WorldMapGump.WMapMarker> ReadLines(string text, System.Func<string, WorldMapGump.WMapMarker> parse)
        {
            var markers = new List<WorldMapGump.WMapMarker>();
            WorldMapGump.ReadLineMarkers(new MemoryStream(Encoding.UTF8.GetBytes(text)), "test", parse, markers);

            return markers;
        }

        [Fact]
        public void Csv_Blank_And_Bad_Lines_Are_Skipped_And_The_Rest_Is_Read()
        {
            string csv = "100,200,0,Britain,,blue,4\n\n300,400,1,Trinsic,,red\nnot,a,number,x,y,z\n1,2\n500,600,2,Last,,green,5\n";

            List<WorldMapGump.WMapMarker> markers = ReadLines(csv, WorldMapGump.ParseCsvMarker);

            markers.Should().HaveCount(3);
            markers[0].Name.Should().Be("Britain");
            markers[0].X.Should().Be(100);
            markers[0].ZoomIndex.Should().Be(4);
            markers[1].MapId.Should().Be(1);
            markers[1].ZoomIndex.Should().Be(3);
            markers[2].Name.Should().Be("Last");
        }

        [Fact]
        public void Csv_Parse_Matches_The_Old_Fields()
        {
            WorldMapGump.WMapMarker m = WorldMapGump.ParseCsvMarker("1496,1628,0,Bank,ICON,red,6");

            m.X.Should().Be(1496);
            m.Y.Should().Be(1628);
            m.MapId.Should().Be(0);
            m.Name.Should().Be("Bank");
            m.MarkerIconName.Should().Be("icon");
            m.Color.Should().Be(WorldMapGump.GetColor("red"));
            m.ZoomIndex.Should().Be(6);
            WorldMapGump.ParseCsvMarker("single").Should().BeNull();
        }

        [Fact]
        public void Uoam_Lines_Parse_And_Bad_Lines_Are_Skipped()
        {
            string map = "3\n+Shrine: 1458 843 0 Shrine of Compassion\n-bank 2: 1430 1695 0 West Britain Bank\n+broken no colon\n+x: 1 2\n+y: a 2 0 Name\nignored line\n";

            List<WorldMapGump.WMapMarker> markers = ReadLines(map, WorldMapGump.ParseUoamMarker);

            markers.Should().HaveCount(2);
            markers[0].Name.Should().Be("Shrine of Compassion");
            markers[0].MarkerIconName.Should().Be("shrine");
            markers[0].X.Should().Be(1458);
            markers[1].MarkerIconName.Should().Be("bank");
            markers[1].Name.Should().Be("West Britain Bank");
        }

        [Fact]
        public void Xml_Markers_Without_Icon_Load_And_Bad_Ones_Are_Skipped()
        {
            string xml = "<Markers>" +
                         "<Marker X=\"10\" Y=\"20\" Name=\"NoIcon\" Facet=\"1\" />" +
                         "<Marker X=\"bad\" Y=\"20\" Name=\"Bad\" Facet=\"1\" Icon=\"a\" />" +
                         "<Marker X=\"30\" Y=\"40\" Name=\"WithIcon\" Facet=\"0\" Icon=\"Unknown\" />" +
                         "</Markers>";
            var markers = new List<WorldMapGump.WMapMarker>();

            WorldMapGump.ReadXmlMarkers(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "test", markers);

            markers.Should().HaveCount(2);
            markers[0].Name.Should().Be("NoIcon");
            markers[0].MapId.Should().Be(1);
            markers[1].Name.Should().Be("WithIcon");
            markers[1].MarkerIcon.Should().BeNull();
        }
    }
}
