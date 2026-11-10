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

        // T6c follow-up: the step system's mounted pace (Mobile.IsMounted, the fast-unmount speed
        // modes, IsFlying) is read from the ECS (EquipmentSlots.Mounted, rebuilt when dirty; MobileInputs.SpeedMode and its
        // Flying bit, pushed by their operations) instead of from the Mobile. Read once at startup
        // (CUO_ECS_STEP_MOUNT_INPUTS=1|0 overrides it in developer builds). CUO_STEP_MOUNT_CHECK=1 compares the two at every
        // step decision. Off = from the Mobile. Default on since 2026-11-08 (ecs-flip-8; owner: T9 path, one full timing pass at the end of T9); the off path and the
        // checker stay for one phase.
        [JsonPropertyName("ecs_step_mount_inputs")] public bool EcsStepMountInputs { get; set; } = true;

        // An item's sub-tile draw offset while a boat carries it (the boat and the items on its deck) is ECS-owned: the boat
        // operations of WorldWriter write the ItemOffset component with the GameObject's Offset, and the item extract's
        // Gather and the item pick read the component. Read once at startup (CUO_ECS_ITEM_OFFSET=1|0 overrides it in
        // developer builds). CUO_ITEM_OFFSET_CHECK=1 compares the component with the Item's Offset at every read. Off = the
        // component is not written and the readers read the Item. Default on since 2026-11-08 (ecs-flip-8; owner: T9 path, one full timing pass at the end of T9); the off path and the
        // checker stay for one phase.
        [JsonPropertyName("ecs_item_offset")] public bool EcsItemOffset { get; set; } = true;

        // A boat's motion state (its queued smooth-movement steps, the step clock, the objects 0xF6 listed on its deck) is the
        // ECS BoatSteps component on the boat's record instead of BoatMovingManager's dictionaries: WorldWriter's boat
        // operations fill it from 0xF6, and BoatStepSystem advances the boats each frame through the same boat operations.
        // Read once at startup (CUO_ECS_BOAT_STEPS=1|0 overrides it in developer builds). CUO_BOAT_CHECK=1 keeps the old
        // dictionaries in shadow and compares their decisions, queues and passengers with the component's every frame.
        // Off = BoatMovingManager's dictionaries. Default on since 2026-11-08 (ecs-flip-8; owner: T9 path, one full timing pass at the end of T9); the off path and the
        // checker stay for one phase.
        [JsonPropertyName("ecs_boat_steps")] public bool EcsBoatSteps { get; set; } = true;

        // Wave 1C: the small cold fields (Item LightID, IsDamageable, UsedLayer; Mobile Race, SpeedMode, IsFemale, IsRenamable;
        // the player's InWarMode) are read from the ECS record (ItemTraits, MobileTraits), which WorldWriter's operations write
        // whatever the switch. Read once at startup (CUO_ECS_SMALL_FIELDS=1|0 overrides it in developer builds).
        // CUO_SMALL_FIELDS_CHECK=1 compares the record with the GameObject's field at every read. Off = the GameObject's
        // fields. Default on since 2026-11-08 (ecs-flip-8; owner: T9 path, one full timing pass at the end of T9); the off path and the
        // checker stay for one phase.
        [JsonPropertyName("ecs_small_fields")] public bool EcsSmallFields { get; set; } = true;

        // perf-4: the static art of a container's contents and of the paperdoll's equipment slots decodes on the art workers
        // before the gump builds its item controls (ContentsArt: a request when an item goes into an open container, a
        // bounded batch wait in ContainerGump/PaperDollGump.UpdateContents) instead of one main-thread decode per sprite.
        // Read at every use (CUO_ART_CONTAINER_PREFETCH=1|0 overrides it in developer builds). Default off.
        [JsonPropertyName("art_container_prefetch")] public bool ArtContainerPrefetch { get; set; }

        // T6b (ecs-transition-plan.md, T6): items and mobiles are also filed per tile in Chunk.Dynamic (DynamicTileIndex, sorted by
        // T6a's key stored at insertion), and the world walks of both renderers (AddTileToRenderList, the Gpu prepare's mobile
        // list, the item cache's served runs) read the tile list's other objects merged with it. CUO_TILE_ORDER_CHECK compares the
        // merged order with the list. Read once at startup (dev builds: CUO_WORLD_TILE_INDEX=1|0 overrides). Default on since
        // 2026-11-08 (owner, after the quiet-machine timing quiet-1108: Gpu neutral, Legacy world_prepare +4-6 % at ls_z1 accepted);
        // the off path, the shadow list and CUO_TILE_ORDER_CHECK stay for one phase.
        [JsonPropertyName("world_tile_index")] public bool WorldTileIndex { get; set; } = true;

        // T9 (ecs-transition-plan.md §T5d): the ECS is the source of the per-frame item update list: one ActiveItemEntry entity per
        // listed item holds the item and its visiting slot (append, swap-remove), and the active item stage visits the order
        // rebuilt from those entities each frame. Off: World's ActiveItemList holds membership and order. CUO_ACTIVE_ITEMS_SOURCE_CHECK=1
        // keeps the list as a shadow and compares membership and order every frame. Read once at startup
        // (CUO_ECS_ACTIVE_ITEMS_SOURCE=1|0 overrides it in developer builds). Default on since 2026-11-08 (ecs-flip-8; owner: T9 path, one full timing pass at the end of T9); the off path and the
        // checker stay for one phase.
        [JsonPropertyName("ecs_active_items_source")] public bool EcsActiveItemsSource { get; set; } = true;

        // T9 (ecs-transition-plan.md §T9): World.Get answers from the mirror's serial index (each record keeps the Item or Mobile
        // it was last written for), not from World.Items/World.Mobiles, which stay maintained. CUO_ENTITY_INDEX_CHECK=1 runs both
        // lookups at every World.Get and logs mismatches. Read once at startup (CUO_ECS_WORLD_ENTITY_INDEX=1|0 overrides it in
        // developer builds). Default on since 2026-11-09 (T9 slice 5); the off path, the tables' dictionaries for World's own
        // bookkeeping and the checker stay for one phase.
        [JsonPropertyName("world_entity_index")] public bool WorldEntityIndex { get; set; } = true;

        // T8 ownership (ecs-transition-plan.md): an effect (an ECS entity since T8) references its source and target by mirror record
        // and serial (not by GameObject) and reads their position and offset from the ECS each update; the death re-key moves the
        // reference, a removed record leaves the last position. CUO_EFFECT_ENDPOINT_CHECK=1 compares every read with the old
        // object reference. Read once at startup (CUO_ECS_EFFECT_ENDPOINTS=1|0 overrides it in developer builds).
        // Default on since 2026-11-08 (ecs-flip-8; owner: T9 path, one full timing pass at the end of T9); the off path and the
        // checker stay for one phase.
        [JsonPropertyName("ecs_effect_endpoints")] public bool EcsEffectEndpoints { get; set; } = true;

        // T8 ownership phase 2 (ecs-transition-plan.md): with ecs_effect_endpoints on, the kinds' motion (a moving
        // effect's path and explosion, a fixed effect following its source and the source's offset, the drag offset, lightning
        // following its source) is computed by EffectSystem from EffectState, each endpoint resolved once per update; the
        // effect's fields become a write-through cache for the views. CUO_EFFECT_MOTION_CHECK=1 replays the legacy per-object
        // motion beside every step and compares. Read once at startup (CUO_ECS_EFFECT_MOTION=1|0 overrides it in developer
        // builds). Default on since 2026-11-08 (ecs-flip-8; owner: T9 path, one full timing pass at the end of T9); the off path and the
        // checker stay for one phase.
        [JsonPropertyName("ecs_effect_motion")] public bool EcsEffectMotion { get; set; } = true;

        // T7b part 3 (ecs-transition-plan.md): both world renderers draw graphic effects (fixed, moving,
        // explosion, drag) from the renderer-neutral effect extract over EffectState (EffectSpriteExtract) at the effect's
        // render-list position, instead of GameEffectView.Draw; lightning stays on its view. Its own switch, not
        // legacy_ecs_sprites/gpu_ecs_sprites: it flips with the effect work. CUO_EFFECT_SPRITE_CHECK=1
        // compares the view's draw with the extract's for every effect, every frame. Read every frame (dev builds:
        // CUO_ECS_EFFECT_SPRITES=1|0, DevBench effectsprites:on|off). Default on since 2026-11-08 (ecs-flip-8; owner: T9 path, one full timing pass at the end of T9); the off path and the
        // checker stay for one phase.
        [JsonPropertyName("ecs_effect_sprites")] public bool EcsEffectSprites { get; set; } = true;

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