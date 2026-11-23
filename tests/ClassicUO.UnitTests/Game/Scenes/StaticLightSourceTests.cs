using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ClassicUO.UnitTests.Ecs;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// view_margin torch flash (owner, Deceit 2026-11-22): a light static or multi component gets one light entry per
    /// frame, from the chunk emitter list (chunk_lights, permanent since quiet-1123). The Gpu fill's CollectGpuStatic once
    /// added a walk-side entry too, so an animated wall torch was lit twice on the frames its animation offset was 0: a
    /// bright flash once per animation cycle.
    /// </summary>
    public sealed class StaticLightSourceTests
    {
        // no walk-side static/multi light add (the walk's AddLightTimed, the views' AddLight(this, this, ...)) is left, so no
        // renderer path adds a second entry next to the chunk emitter list
        [Fact]
        public void No_walk_side_static_light_add_is_left()
        {
            string root = WriteSiteScanner.FindRepoRoot();
            string client = Path.Combine(root, "src", "ClassicUO.Client", "Game");
            var files = new List<string>(Directory.GetFiles(Path.Combine(client, "Scenes"), "GameScene*.cs"))
            {
                Path.Combine(client, "GameObjects", "Views", "StaticView.cs"),
                Path.Combine(client, "GameObjects", "Views", "MultiView.cs")
            };
            var call = new Regex(@"\bAddLightTimed\(|\bAddLight\(this, this,");
            var found = new List<string>();

            foreach (string file in files)
            {
                string[] lines = File.ReadAllLines(file);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (call.IsMatch(lines[i]))
                    {
                        found.Add($"{Path.GetFileName(file)}:{i + 1}");
                    }
                }
            }

            Assert.True(found.Count == 0, "walk-side static lights: " + string.Join("; ", found));
        }
    }
}
