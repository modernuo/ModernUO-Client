using System;
using System.IO;
using ClassicUO.Game.Managers;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers
{
    public class SeasonManagerFileTests
    {
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
