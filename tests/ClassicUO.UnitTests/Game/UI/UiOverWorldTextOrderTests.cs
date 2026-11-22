using System.IO;
using System.Text.RegularExpressions;
using ClassicUO.UnitTests.Ecs;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.UI
{
    // Upstream fb0112d23 moved UIManager.Draw after the scene's UI so gumps cover world text. Ours already has that order,
    // for both renderers: world text (and health lines) are drawn only in GameScene.DrawUI, in the UI pass that
    // GameController runs after the world pass whichever renderer drew the world, and that pass draws the gumps after it.
    public class UiOverWorldTextOrderTests
    {
        private static string Read(string relative) => File.ReadAllText(Path.Combine(WriteSiteScanner.FindRepoRoot(), relative));

        [Fact]
        public void The_Ui_Pass_Draws_Gumps_After_The_Scenes_World_Text()
        {
            string ui = Read("src/ClassicUO.Client/GameControllerUiCache.cs");
            Match body = Regex.Match(ui, @"private void DrawSceneUiAndGumps\(\)\s*\{(?<b>.*?)\n        \}", RegexOptions.Singleline);
            body.Success.Should().BeTrue();

            int scene = body.Groups["b"].Value.IndexOf("Scene.DrawUI(");
            int gumps = body.Groups["b"].Value.IndexOf("UIManager.Draw(");
            scene.Should().BeGreaterThan(0);
            gumps.Should().BeGreaterThan(scene, "the gumps draw over the scene's UI (world text)");
        }

        [Fact]
        public void World_Text_Is_Drawn_Only_In_The_Scenes_Ui()
        {
            string root = WriteSiteScanner.FindRepoRoot();
            int sites = 0;

            foreach (string dir in new[] { "src/ClassicUO.Client", "src/Uo.Render" })
            {
                foreach (string file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
                {
                    sites += Regex.Matches(File.ReadAllText(file), @"WorldTextManager\.Draw\(").Count;
                }
            }

            sites.Should().Be(1);
            Regex.IsMatch(Read("src/ClassicUO.Client/Game/Scenes/GameScene.cs"),
                    @"public override void DrawUI\(UltimaBatcher2D batcher\)(?:(?!\n        public ).)*WorldTextManager\.Draw\(", RegexOptions.Singleline)
                .Should().BeTrue("the one site is GameScene.DrawUI");
        }
    }
}
