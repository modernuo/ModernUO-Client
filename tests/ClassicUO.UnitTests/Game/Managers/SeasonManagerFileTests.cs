using System;
using System.IO;
using ClassicUO.Game.Managers;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers
{
    public class SeasonManagerFileTests
    {
        // upstream d4a188976: whitespace is trimmed before a hex parse. A space before a hex field read it as decimal and threw
        // (in SeasonManager's static constructor)
        [Theory]
        [InlineData("spring,static,0x0CA7,0x0C84", "spring", true, 0x0CA7, 0x0C84)]
        [InlineData(" Winter , static , 0x0CA7 , 0x0C84 ", "winter", true, 0x0CA7, 0x0C84)]
        [InlineData("fall,land,\t196,0x00C5", "fall", false, 196, 0xC5)]
        public void Season_Lines_Parse_With_Whitespace(string line, string season, bool isStatic, int orig, int replace)
        {
            Assert.True(SeasonManager.TryParseSeasonLine(line, out string s, out bool st, out ushort o, out ushort r));
            Assert.Equal(season, s);
            Assert.Equal(isStatic, st);
            Assert.Equal(orig, o);
            Assert.Equal(replace, r);
        }

        [Theory]
        [InlineData("")]
        [InlineData("# spring,static,1,2")]
        [InlineData("  // spring,static,1,2")]
        [InlineData("spring,static,1")]
        [InlineData("spring,static,0xZZ,2")]
        [InlineData("spring,static,70000,2")]
        public void Comment_Short_Or_Malformed_Season_Lines_Are_Skipped(string line) =>
            Assert.False(SeasonManager.TryParseSeasonLine(line, out _, out _, out _, out _));

        [Fact]
        public void CreateDefaultSeasonsFile_Creates_Missing_Folder()
        {
            string root = Path.Combine(Path.GetTempPath(), "cuo-seasons-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "Data", "Client", "seasons.txt");

            try
            {
                Assert.False(Directory.Exists(root));

                SeasonManager.CreateDefaultSeasonsFile(file);

                Assert.True(File.Exists(file));
                Assert.StartsWith("spring,static,", File.ReadAllLines(file)[0]);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }
    }
}
