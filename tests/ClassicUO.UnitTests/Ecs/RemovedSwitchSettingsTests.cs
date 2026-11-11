using System.IO;
using ClassicUO.Configuration;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Ecs
{
    // T9 deletes a switch together with its settings.json key. A settings.json written by an older client still holds the key;
    // Main loads the file through ConfigurationResolver.Load with SettingsJsonContext, which must skip the unknown key and keep
    // the other values.
    public class RemovedSwitchSettingsTests
    {
        // every key T9 removed with its switch (the slices before these removed ecs_owns_scalars, world_update_mobile_system,
        // world_update_active_items, mobile_push_inputs and others the same way)
        private static readonly string[] s_removedKeys =
        {
            "world_sweep_system",
            "world_active_items_system",
            "world_item_anim_system",
            "world_anim_system",
            "world_step_system",
            "ecs_effects",
            "ecs_step_mount_inputs",
            "ecs_item_offset",
            "ecs_boat_steps",
            "ecs_small_fields",
            "ecs_active_items_source",
            "ecs_effect_motion",
            "ecs_effect_endpoints",
            "ecs_effect_sprites",
            "world_tile_index",
            "gpu_ecs_sprites"
        };

        [Fact]
        public void A_Settings_File_With_Removed_Switch_Keys_Loads()
        {
            string path = Path.Combine(Path.GetTempPath(), $"cuo-removed-keys-{System.Guid.NewGuid():N}.json");

            try
            {
                var json = new System.Text.StringBuilder("{\n  \"username\": \"tester\",\n");

                foreach (string key in s_removedKeys)
                {
                    json.Append("  \"").Append(key).Append("\": false,\n");
                }

                json.Append("  \"fps\": 144\n}\n");
                File.WriteAllText(path, json.ToString());

                Settings settings = ConfigurationResolver.Load(path, SettingsJsonContext.RealDefault.Settings);

                settings.Should().NotBeNull();
                settings.Username.Should().Be("tester");
                settings.FPS.Should().Be(144);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
