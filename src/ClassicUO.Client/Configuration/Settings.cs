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

        // Gpu world renderer, with gpu_prepare_skip_baked and gpu_ecs_sprites: settled ground items' sprites are cached per
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

        // Gpu world renderer: mobiles' dynamic sprites come from the ECS (MobileAnimationSystem + MobileSpriteExtract)
        // instead of capturing MobileView.Draw. Off = the capture, the parity yardstick (EcsParity).
        [JsonPropertyName("gpu_ecs_sprites")] public bool GpuEcsSprites { get; set; } = true;

        // W5: World.Update visits only the items that need a per-frame update (corpses, items whose object handles just
        // opened) plus a range sweep every 50 ms, instead of every item every frame. Off = the full per-frame loop.
        [JsonPropertyName("world_update_active_items")] public bool WorldUpdateActiveItems { get; set; } = true;

        // W5, with gpu_ecs_sprites: a mobile's action selection and sprite instances are reused while their inputs are
        // unchanged (MobileFrameCache: the sampled state, equipment, seat tile, hues, alpha); position and depth are
        // reapplied every frame. Off = selected and extracted every frame.
        [JsonPropertyName("gpu_mobile_frame_cache")] public bool GpuMobileFrameCache { get; set; } = true;

        // W5, with gpu_ecs_sprites: a mobile's slow-changing selection and override-hue inputs (body flags, sex, dead, war
        // mode, running, steps, notoriety, status bits, mounted) are read from the ECS, where their mutation sites push
        // them (MobileInputs, EquipmentSlots.Mounted), instead of from the legacy Mobile every frame. The pushes always
        // run. Off = sampled from the Mobile every frame.
        [JsonPropertyName("mobile_push_inputs")] public bool MobilePushInputs { get; set; } = true;

        // E4 (needs world_update_active_items): World.Update updates only the mobiles whose per-mobile clock is
        // due (queued steps, frame or idle timer expired, handles opening), picked from the ECS (MobileClock) instead of
        // calling Mobile.Update on every mobile every frame. Off = the full per-frame loop. Default on since 2026-10-31
        // (owner); the off path and CUO_MOBILE_UPDATE_CHECK stay for one phase.
        [JsonPropertyName("world_update_mobile_system")] public bool WorldUpdateMobileSystem { get; set; } = true;

        // T5b (needs world_update_mobile_system): a due mobile without queued steps or a handles opening has its idle animation
        // and frame advance computed in the ECS (MobileAnimationAdvanceSystem over AnimationTimer, MobileInputs and
        // EquipmentSlots) instead of Mobile.Update; mobiles with steps or handles still run Mobile.Update until T6. Read once
        // at startup (CUO_WORLD_ANIM_SYSTEM=1|0 overrides it in developer builds). Default off; CUO_ANIM_ADVANCE_CHECK=1
        // compares every advance with the legacy one.
        [JsonPropertyName("world_anim_system")] public bool WorldAnimSystem { get; set; } = false;

        // T2 (ecs-transition-plan.md): the container-contents readers (container, grid loot, trade and spellbook gumps, the
        // shop's lists, FindItem*, the counters, macros) take a parent's children from the ECS (WorldState.Children: the
        // mirror's attached children by child-order stamp, a spellbook's spell entries) instead of the LinkedObject child
        // list, which is still maintained. CUO_CONTENTS_CHECK=1 compares the two. Default on since 2026-11-01 (coordinator, after
        // the off/on gates bench-000104/106 and two Codex audits); the off path and the checker stay for one phase.
        [JsonPropertyName("ecs_read_contents")] public bool EcsReadContents { get; set; } = true;

        // T5c (ecs-transition-plan.md): the 50 ms range sweep and the world-map pass run as a system over the ECS (Position, with
        // the last step's tile for stepping mobiles; MobileInputs.Notoriety plus the party list), removing through the funnel in
        // table order, instead of the loops' own distance tests. CUO_SWEEP_CHECK=1 compares the two. Read once at startup. Default off.
        [JsonPropertyName("world_sweep_system")] public bool WorldSweepSystem { get; set; }

        // T6b (ecs-transition-plan.md, T6): items and mobiles are also filed per tile in Chunk.Dynamic (DynamicTileIndex, sorted by
        // T6a's key stored at insertion), and the world walks of both renderers (AddTileToRenderList, the Gpu prepare's mobile
        // list, the item cache's served runs) read the tile list's other objects merged with it. CUO_TILE_ORDER_CHECK compares the
        // merged order with the list. Read once at startup (dev builds: CUO_WORLD_TILE_INDEX=1|0 overrides). Default off.
        [JsonPropertyName("world_tile_index")] public bool WorldTileIndex { get; set; }

        // T5d (ecs-transition-plan.md): the per-frame item update list is owned by the ECS side: membership is the ItemActive tag,
        // the visiting order is the system's list (same add and swap-remove rules as before). CUO_ACTIVE_ITEMS_CHECK=1 compares
        // tags and list every frame. Read once at startup (CUO_WORLD_ACTIVE_ITEMS_SYSTEM=1|0 overrides it in developer builds).
        // Only used with world_update_active_items. Default off.
        [JsonPropertyName("world_active_items_system")] public bool WorldActiveItemsSystem { get; set; }

        // T3a (ecs-transition-plan.md, T3): the equipment readers (Mobile.FindItemByLayer and through it IsMounted, the
        // animation group's hands, the abilities, the paperdoll, PaperdollOrder.BuildInWorld and Legacy's MobileView) take a
        // mobile's worn item per layer from the ECS (WornLayers: the mirror's first attached item per layer by child-order
        // stamp) instead of walking the LinkedObject child list, which is still maintained. CUO_EQUIP_READ_CHECK=1 compares
        // the two at every call. Default on since 2026-11-01 (coordinator, after t3a-fold removed the 0x78 maintenance cost and
        // the off/on gates bench-000144/145); the off path and the checker stay for one phase.
        [JsonPropertyName("ecs_read_equipment")] public bool EcsReadEquipment { get; set; } = true;

        // T4 (ecs-transition-plan.md, "thin handles"): the cold world-state getters (Name, Hits, HitsMax, Stamina, Mana and
        // their maxima, Price) read the entity's ECS record; the GameObject field becomes a write-only cache. Read once at
        // startup (EcsScalars.Owns). Default off.
        [JsonPropertyName("ecs_owns_scalars")] public bool EcsOwnsScalars { get; set; }

        // T3b (ecs-transition-plan.md, T3 "Equipment layering as data"): the worn-layer draw order, the covering and the
        // gargoyle remaps come from the layering rules data (the pack's layerrules.bin, else the built-in default made from the
        // same source) instead of the hard-coded PaperdollOrder/IsCovered/FixGargoyleEquipments. CUO_LAYER_RULES_CHECK=1
        // compares the two at every decision. Default on since 2026-11-01 (coordinator, after the gates bench-000147/148 PASS, 120M
        // [layers] checks with 0 mismatches, and E2 bench-000146/155: cache hit -51..-59 %, mixed -42..-47 % vs the hard-coded path);
        // the hard-coded path and the checker stay for one phase.
        [JsonPropertyName("ecs_layer_rules")] public bool EcsLayerRules { get; set; } = true;

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