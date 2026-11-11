// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassicUO.Configuration.Json;
using Microsoft.Xna.Framework;

namespace ClassicUO.Configuration
{
    [JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(Settings), GenerationMode = JsonSourceGenerationMode.Metadata)]
    sealed partial class SettingsJsonContext : JsonSerializerContext
    {
        // horrible fix: https://github.com/ClassicUO/ClassicUO/issues/1663
        public static SettingsJsonContext RealDefault { get; } = new SettingsJsonContext(
            new JsonSerializerOptions()
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
    }

    internal sealed class Settings
    {
        public const string SETTINGS_FILENAME = "settings.json";
        public static Settings GlobalSettings = new Settings();
        public static string CustomSettingsFilepath = null;


        [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;

        [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;

        [JsonPropertyName("ip")] public string IP { get; set; } = "127.0.0.1";

        [JsonPropertyName("port"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public ushort Port { get; set; } = 2593;

        /**
         * Ignores the login servers relay packet, connects back with the settings IP
         */
        [JsonPropertyName("ignore_relay_ip")] public bool IgnoreRelayIp { get; set; } = false;

        [JsonPropertyName("ultimaonlinedirectory")] public string UltimaOnlineDirectory { get; set; } = "";

        [JsonPropertyName("profilespath")] public string ProfilesPath { get; set; } = string.Empty;

        [JsonPropertyName("clientversion")] public string ClientVersion { get; set; } = string.Empty;

        [JsonPropertyName("lang")] public string Language { get; set; } = "";

        [JsonPropertyName("lastservernum")] public ushort LastServerNum { get; set; } = 1;

        [JsonPropertyName("last_server_name")] public string LastServerName { get; set; } = string.Empty;

        // 0 = uncapped
        [JsonPropertyName("fps")] public int FPS { get; set; } = 0;

        [JsonPropertyName("vsync")] public bool VSync { get; set; }

        [JsonPropertyName("screen_scale")] public float ScreenScale { get; set; } = 1f;

        [JsonConverter(typeof(NullablePoint2Converter))] [JsonPropertyName("window_position")] public Point? WindowPosition { get; set; }
        [JsonConverter(typeof(NullablePoint2Converter))] [JsonPropertyName("window_size")] public Point? WindowSize { get; set; }

        [JsonPropertyName("is_win_maximized")] public bool IsWindowMaximized { get; set; } = true;

        [JsonPropertyName("saveaccount")] public bool SaveAccount { get; set; }

        [JsonPropertyName("autologin")] public bool AutoLogin { get; set; }

        [JsonPropertyName("reconnect")] public bool Reconnect { get; set; }

        [JsonPropertyName("reconnect_time")] public int ReconnectTime { get; set; } = 1;

        [JsonPropertyName("login_music")] public bool LoginMusic { get; set; } = true;

        [JsonPropertyName("login_music_volume")] public int LoginMusicVolume { get; set; } = 70;

        [JsonPropertyName("fixed_time_step")] public bool FixedTimeStep { get; set; } = true;

        [JsonPropertyName("run_mouse_in_separate_thread")]
        public bool RunMouseInASeparateThread { get; set; } = true;

        [JsonPropertyName("force_driver")] public byte ForceDriver { get; set; }

        // Folder of the converted asset pack; empty = "pack" next to the client, else in the UO directory.
        [JsonPropertyName("pack")] public string PackDirectory { get; set; }

        // The world renderer setting: "auto" (default: Gpu when the machine supports it, else Legacy), "gpu" (forced: no
        // Gpu world = error and exit) or "legacy" (forced). Saved. -worldrenderer overrides it for one run without saving
        // (WorldRendererOverride). RendererSelection decides at startup; WorldRenderer below is the result.
        [JsonPropertyName("world_renderer")] public string WorldRendererSetting { get; set; } = "auto";

        /// <summary>-worldrenderer auto|gpu|legacy: this run only, never saved.</summary>
        [JsonIgnore] public static string WorldRendererOverride { get; set; }

        // The world renderer this session uses: "gpu" (Uo.Render on FNA3D's SDL_GPU device) or "legacy" (the FNA batcher).
        // Set once at startup by RendererSelection (never mid-session for players); dev builds (CUO_DEVTOOLS) can switch it at
        // runtime (-worldrenderer command, DevBench world:/flip:/pick). Not saved.
        [JsonIgnore] public string WorldRenderer { get; set; } = "gpu";

        [JsonIgnore] public bool UseGpuWorldRenderer => string.Equals(WorldRenderer, "gpu", StringComparison.OrdinalIgnoreCase);

        // Gpu world renderer: the per-frame world prepare skips baked land and statics that need no per-frame work
        // (GameSceneGpuPrepare). Off = the full walk, as Legacy does it.
        [JsonPropertyName("gpu_prepare_skip_baked")] public bool GpuPrepareSkipBaked { get; set; } = true;

        // Gpu world renderer, with gpu_prepare_skip_baked: settled ground items' sprites are cached per
        // chunk and their tiles left out of the walk (W4, GameSceneGpuItemCache). Off = every item walked and extracted.
        [JsonPropertyName("gpu_prepare_item_cache")] public bool GpuPrepareItemCache { get; set; } = true;

        // E7, with gpu_prepare_item_cache: a tile whose other unbaked objects are items the cache can't hold (animated,
        // corpses, fading, with text...) is still served; those items are walked alone, in tile order. Off = such tiles are
        // walked in full.
        [JsonPropertyName("gpu_prepare_item_cache_mixed")] public bool GpuPrepareItemCacheMixed { get; set; } = true;

        // E6, with gpu_prepare_item_cache: graphic effects (GameEffect) on a tile no longer keep its items out of the cache, and
        // a moving effect crossing tiles no longer re-extracts the chunk's items. A served tile's effects are walked alone,
        // in tile order. Off = a tile holding an effect is walked in full and every effect move re-extracts its chunk.
        [JsonPropertyName("gpu_prepare_item_cache_effects")] public bool GpuPrepareItemCacheEffects { get; set; } = true;

        // B8: the UI pass records its draws and leaves the UI targets untouched when they match the last frame's exactly; the
        // cursor goes over a kept copy of the UI (GameController.DrawUiCached). Off = the UI drawn every frame.
        [JsonPropertyName("ui_render_cache")] public bool UiRenderCache { get; set; } = true;

        // T7b (ecs-transition-plan.md, T7): the Legacy world draws mobiles and world items from the ECS extract (select, then
        // LegacySpriteResolve to the FNA atlas through the Art/Animations loaders) at the render list's view.Draw position,
        // instead of MobileView/ItemView.Draw. Effects stay on their views. CUO_LEGACY_ECS_PARITY=1 compares the view's
        // batcher calls with the sink's. Read every frame (dev builds: CUO_LEGACY_ECS_SPRITES=1|0 overrides). Default on since
        // legacy-flip (owner decision 2026-11-10, bench-000564); off = the views.
        [JsonPropertyName("legacy_ecs_sprites")] public bool LegacyEcsSprites { get; set; } = true;

        // legacy-sprites-fix: the item sprite extract's Gather reads the item's packed ItemDrawRow component (one row,
        // written by WorldMirror.UpsertItem with the record) instead of the Graphic, Hue, Position, Flags and ItemData
        // components. CUO_ECS_ITEM_DRAW_ROW=1|0 overrides it in developer builds; CUO_ITEM_DRAW_ROW_CHECK=1 compares the row
        // with the components at every read. Off = the components. Default on since legacy-flip (bench-000564).
        [JsonPropertyName("ecs_item_draw_row")] public bool EcsItemDrawRow { get; set; } = true;

        // perf-4: the static art of a container's contents and of the paperdoll's equipment slots decodes on the art workers
        // before the gump builds its item controls (ContentsArt: a request when an item goes into an open container, a
        // bounded batch wait in ContainerGump/PaperDollGump.UpdateContents) instead of one main-thread decode per sprite.
        // Read at every use (CUO_ART_CONTAINER_PREFETCH=1|0 overrides it in developer builds). Default off.
        [JsonPropertyName("art_container_prefetch")] public bool ArtContainerPrefetch { get; set; }


        // T9 (ecs-transition-plan.md §T9): World.Get answers from the mirror's serial index (each record keeps the Item or Mobile
        // it was last written for), not from World.Items/World.Mobiles, which stay maintained. CUO_ENTITY_INDEX_CHECK=1 runs both
        // lookups at every World.Get and logs mismatches. Read once at startup (CUO_ECS_WORLD_ENTITY_INDEX=1|0 overrides it in
        // developer builds). Default on since 2026-11-09 (T9 slice 5); the off path, the tables' dictionaries for World's own
        // bookkeeping and the checker stay for one phase.
        [JsonPropertyName("world_entity_index")] public bool WorldEntityIndex { get; set; } = true;

        // W5: the per-frame overhead text pass skips the text layout of mobiles and corpses whose texts have all expired
        // (expired texts stay in the entity's container; the layout would place nothing). Off = laid out every frame.
        [JsonPropertyName("text_skip_expired")] public bool TextSkipExpired { get; set; } = true;

        [JsonPropertyName("encryption")] public byte Encryption { get; set; }

        [JsonPropertyName("plugins")] public string[] Plugins { get; set; } = { @"./Assistant/Razor.dll" };
        
        [JsonPropertyName("files_override")] public string OverrideFile { get; set; }

        public static string GetSettingsFilepath()
        {
            if (CustomSettingsFilepath != null)
            {
                if (Path.IsPathRooted(CustomSettingsFilepath))
                {
                    return CustomSettingsFilepath;
                }

                return Path.Combine(CUOEnviroment.ExecutablePath, CustomSettingsFilepath);
            }

            return Path.Combine(CUOEnviroment.ExecutablePath, SETTINGS_FILENAME);
        }


        /// <summary>
        /// The language preference as configured (settings.json or -language; "" when unset), or null before the language
        /// is chosen. <see cref="Language"/> then holds the effective language of this run (the preference when it is
        /// available, else the OS's choice); <see cref="Save"/> writes this preference unchanged, so a language chosen
        /// from the OS is never saved and a preference that is unavailable this run is kept for the next.
        /// </summary>
        internal static string ConfiguredLanguage;

        /// <summary>
        /// Startup: chooses the effective language from <paramref name="configured"/> and the available cliloc languages
        /// (<paramref name="choose"/> is <see cref="OsLanguage.Select"/>), keeping the preference for <see cref="Save"/>.
        /// </summary>
        internal static void ApplyLanguage(Settings settings, IReadOnlyCollection<string> available, Func<string, IReadOnlyCollection<string>, string> choose)
        {
            ConfiguredLanguage = settings.Language ?? string.Empty;
            settings.Language = choose(ConfiguredLanguage, available);
        }

        public void Save()
        {
            // Make a copy of the settings object that we will use in the saving process
            var json = JsonSerializer.Serialize(this, SettingsJsonContext.RealDefault.Settings);
            var settingsToSave = JsonSerializer.Deserialize(json, SettingsJsonContext.RealDefault.Settings);

            // Make sure we don't save username and password if `saveaccount` flag is not set
            // NOTE: Even if we pass username and password via command-line arguments they won't be saved
            if (!settingsToSave.SaveAccount)
            {
                settingsToSave.Username = string.Empty;
                settingsToSave.Password = string.Empty;
            }

            settingsToSave.ProfilesPath = string.Empty;

            // The configured language, not this run's effective one: a language chosen from the OS stays unset (the next
            // run follows the OS again), and a preference that is unavailable now is kept
            if (ConfiguredLanguage != null)
            {
                settingsToSave.Language = ConfiguredLanguage;
            }

            // NOTE: We can do any other settings clean-ups here before we save them

            ConfigurationResolver.Save(settingsToSave, GetSettingsFilepath(), SettingsJsonContext.RealDefault.Settings);
        }
    }
}