using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ClassicUO.Game.Scenes;
using ClassicUO.UnitTests.Ecs;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>
    /// view_margin torch flash (owner, Deceit 2026-11-22): a light static or multi component gets one light entry per
    /// frame, from the walk (or the view it draws) with the switch off and from the chunk emitter list with it on. The Gpu
    /// fill's CollectGpuStatic added the walk's entry under view_margin too, so an animated wall torch was lit twice on the
    /// frames its animation offset was 0 (the other frames go through StaticView, which skipped it): a bright flash once
    /// per animation cycle.
    /// </summary>
    public sealed class StaticLightSourceTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Walk_lights_a_light_static_only_without_view_margin(bool viewMargin)
        {
            Assert.Equal(!viewMargin, ViewMargin.WalkLightsStatic(true, viewMargin));
            Assert.False(ViewMargin.WalkLightsStatic(false, viewMargin));
        }

        // every walk-side static/multi light add (the walk's AddLightTimed, the views' AddLight(this, this, ...)) is gated by
        // ViewMargin.WalkLightsStatic, so no renderer path adds a second entry next to the chunk emitter list
        [Fact]
        public void Every_walk_side_static_light_is_gated_by_the_view_margin_rule()
        {
            string root = WriteSiteScanner.FindRepoRoot();
            string client = Path.Combine(root, "src", "ClassicUO.Client", "Game");
            var files = new List<string>(Directory.GetFiles(Path.Combine(client, "Scenes"), "GameScene*.cs"))
            {
                Path.Combine(client, "GameObjects", "Views", "StaticView.cs"),
                Path.Combine(client, "GameObjects", "Views", "MultiView.cs")
            };
            var call = new Regex(@"(?<!void )\bAddLightTimed\(|\bAddLight\(this, this,");
            var ungated = new List<string>();
            int calls = 0;

            foreach (string file in files)
            {
                string[] lines = File.ReadAllLines(file);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (!call.IsMatch(lines[i]))
                    {
                        continue;
                    }

                    calls++;
                    string gate = null;

                    for (int k = i - 1; k >= 0 && k >= i - 4; k--)
                    {
                        if (lines[k].TrimStart().StartsWith("if (") || lines[k].TrimStart().StartsWith("else if ("))
                        {
                            gate = lines[k];

                            break;
                        }
                    }

                    if (gate == null || !gate.Contains("WalkLightsStatic("))
                    {
                        ungated.Add($"{Path.GetFileName(file)}:{i + 1}: {gate?.Trim() ?? "(no if)"}");
                    }
                }
            }

            Assert.True(calls >= 5, $"found {calls} walk-side light adds");
            Assert.True(ungated.Count == 0, "walk-side static lights without the view_margin rule: " + string.Join("; ", ungated));
        }
    }
}
