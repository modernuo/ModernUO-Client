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
        // Gpu world = error and exit) or "compatibility" ("legacy" also accepted; forced). Saved; the Options menu writes it. -worldrenderer overrides it for one run without saving
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

        // pa-gpusig, with gpu_prepare_item_cache: a served tile's cached items are trusted while their chunk's render generation
        // (GpuItemGen.Touch: the WorldWriter commits, text, boat offsets, object handles) is unchanged and the tile was served in
        // every pass since it was checked; otherwise their signatures are compared as before. Off = every served item's
        // signature is rebuilt and compared every frame. CUO_GPU_SIG_CHECK=1 compares both decisions at every use.
        [JsonPropertyName("gpu_prepare_item_cache_gen")] public bool GpuPrepareItemCacheGen { get; set; }

        // B8: the UI pass records its draws and leaves the UI targets untouched when they match the last frame's exactly; the
        // cursor goes over a kept copy of the UI (GameController.DrawUiCached). Off = the UI drawn every frame.
        [JsonPropertyName("ui_gump_dirty")] public bool UiGumpDirty { get; set; }
        [JsonPropertyName("text_quad_cache")] public bool TextQuadCache { get; set; }
        [JsonPropertyName("ui_render_cache")] public bool UiRenderCache { get; set; } = true;

        // A minimized window skips world prepare, draw and present (GameController.Update); packets, update and audio keep running.
        // Not an ECS path, so there is no CUO_*_CHECK checker: nothing old to compare against, the draw is simply not run.
        // Default on (owner decision): a minimized window shows nothing, and several clients run at once.
        [JsonPropertyName("skip_draw_when_minimized")] public bool SkipDrawWhenMinimized { get; set; } = true;

        // An inactive (ReduceFPSWhenInactive) or minimized client sleeps 12 ms instead of 1 ms on frames that skip draw, so its
        // Update, input poll and packet drain run about 80 times a second instead of up to 1,000. A packet waits at most one
        // sleep. Not measured yet, so default off.
        [JsonPropertyName("throttle_update_when_inactive")] public bool ThrottleUpdateWhenInactive { get; set; }

        // evloop-1 arm A (Windows): between frames the main loop waits once, with MsgWaitForMultipleObjectsEx, for the socket
        // (WSAEventSelect), window messages, a wake, or a high-resolution timer at the next deadline (draw, queued action,
        // player step, or event_loop_max_wait_ms), instead of skipping the draw and sleeping 1 ms. Falls back to the legacy loop
        // off Windows, when the wait probe fails, and while late wakes back it off. Not an ECS path: no CUO_*_CHECK checker;
        // CUO_LOOP_STATS=1 measures both loops. Read every frame (CUO_EVENT_LOOP=1|0 overrides it in developer builds).
        // Default off.
        [JsonPropertyName("event_loop")] public bool EventLoop { get; set; }

        // M1: uncapped, vsync off, without inactive FPS reduction: skip Draw and Present until the display's next deadline.
        // Explicit FPS caps and inactive/minimized policies keep precedence. Unknown refresh uses the original path.
        [JsonPropertyName("present_at_display_rate")] public bool PresentAtDisplayRate { get; set; }

        // event_loop: the longest wait without an Update, for timers the loop does not track (plugin ticks, macros, audio
        // streaming). 16 ms keeps Update at 60 Hz or more; a larger value saves wake-ups on idle clients
        // (CUO_EVENT_LOOP_MAX_WAIT_MS overrides it in developer builds).
        [JsonPropertyName("event_loop_max_wait_ms")] public int EventLoopMaxWaitMs { get; set; } = 16;

        // perf-4: the static art of a container's contents and of the paperdoll's equipment slots decodes on the art workers
        // before the gump builds its item controls (ContentsArt: a request when an item goes into an open container, a
        // bounded batch wait in ContainerGump/PaperDollGump.UpdateContents) instead of one main-thread decode per sprite.
        // Read at every use (CUO_ART_CONTAINER_PREFETCH=1|0 overrides it in developer builds). Default off.
        [JsonPropertyName("art_container_prefetch")] public bool ArtContainerPrefetch { get; set; }

        // qw-renderlist: no world pick tests in a frame whose mouse is not over the world (over a gump, outside the viewport):
        // GameScene.DrawUI clears that frame's selection anyway (PickSkip). Drops the world draw's highlight of the object
        // under a gump. CUO_PICK_SKIP_OFF_WORLD=1|0 overrides it in developer builds, CUO_PICK_SKIP_CHECK=1 checks it.
        // Default off.
        [JsonPropertyName("pick_skip_off_world")] public bool PickSkipOffWorld { get; set; }

        // pa-itemprep: the world fill's item branch reads the graphic and the flags from the item's packed ItemDrawRow (one
        // record lookup) instead of the Graphic and Flags getters (ItemPrep). CUO_ITEM_PREP_ROWS=1|0 overrides it in developer
        // builds, CUO_ITEM_PREP_CHECK=1 checks it. Default off.
        [JsonPropertyName("item_prep_rows")] public bool ItemPrepRows { get; set; }

        // pa-itemprep: an art item's pick test rejects a mouse outside every box its pack sprite can be tested in before it
        // looks up the stored draw offset (ItemPick). CUO_ITEM_PICK_EARLY_REJECT=1|0 overrides it in developer builds,
        // CUO_ITEM_PICK_CHECK=1 checks it. Default off.
        [JsonPropertyName("item_pick_early_reject")] public bool ItemPickEarlyReject { get; set; }


        // W5: the per-frame overhead text pass skips the text layout of mobiles and corpses whose texts have all expired
        // (expired texts stay in the entity's container; the layout would place nothing). Off = laid out every frame.
        [JsonPropertyName("text_skip_expired")] public bool TextSkipExpired { get; set; } = true;

        // item-pool: the items World drops (out of range, deleted, swept) wait 2 s in a bounded pool (8,192 items, ~2.3 MB)
        // and are reset and reused for the items that enter range next (ItemPool), instead of a new Item each. Read once at
        // startup (CUO_ITEM_POOL=1|0 overrides it in developer builds; CUO_ITEM_POOL_CHECK=1 compares every reused item with a
        // new one). Default on since 2026-11-11 (owner; gates with the checker: 0 mismatches over ~15k reuses, ls_walk
        // B/frame -24..34 %); off = a new Item for every item entering range.
        [JsonPropertyName("item_pool")] public bool ItemPool { get; set; } = true;

        // anim-count: while the Gpu world renderer draws, the animation advance reads a mobile's frame count from the pack's
        // animation tables (AnimFrameCount) instead of the Legacy frame cache, which decodes and uploads the direction into the
        // RGBA8 anim atlas that the Gpu world never samples (Legacy keeps the cache read). Read once at startup
        // (CUO_ANIM_FRAME_COUNT_TABLES=1|0 overrides it in developer builds; CUO_ANIM_FRAME_COUNT_CHECK=1 compares both counts
        // at every call). Default off.
        [JsonPropertyName("anim_frame_count_tables")] public bool AnimFrameCountTables { get; set; }

        // cache-lru: the Legacy animation atlas (RGBA8 pages, 64 MiB each) frees its least recently used pages above a byte
        // budget that grows a page on thrash while memory allows and shrinks after minutes without (sooner when unfocused);
        // evicted directions load again on use (AnimAtlasLru). Read once at startup (CUO_ANIM_LRU=1|0 and the
        // CUO_ANIM_LRU_* sizes override it in developer builds; CUO_ANIM_LRU_CHECK=1 checks reloads and freed pages).
        // Default off: the atlas only grows.
        [JsonPropertyName("anim_atlas_lru")] public bool AnimAtlasLru { get; set; }

        // perf-audit #2: the world text list keeps its tail (where the draw pass starts) while its links change, validated by a
        // link-version counter, instead of walking every DRight link in each of the three passes per frame. Read at every use
        // (CUO_TEXT_TAIL=1|0 overrides it and CUO_TEXT_ORDER_CHECK=1 compares the kept tail and the link order with the full
        // walk, in developer builds). Default off; off = the walk in every pass.
        [JsonPropertyName("text_tail_cache")] public bool TextTailCache { get; set; }

        // pa-meshhue: a Legacy chunk-mesh sprite recomputes its vertex hue only when an input changes (the object's hue, the
        // out-of-range tint setting with the range centre and view range, the dead player's gray, a mesh rebuild) instead of
        // every frame it is visible (GameScene.ApplyMeshHue, MeshHueEpoch). Read once per frame (CUO_MESH_HUE=1|0 overrides
        // it in developer builds; CUO_MESH_HUE_CHECK=1 compares every kept hue with the per-frame one). Default off.
        [JsonPropertyName("mesh_hue_on_change")] public bool MeshHueOnChange { get; set; }

        // pa-meshhue: Legacy chunk-mesh layers keep the frame's sprite visibility as bits (MeshLayer.ResetVisibility clears a
        // word per 64 sprites instead of a byte each; BuildVisibleIndices compares words and walks set bits). Read once per
        // frame (CUO_MESH_VIS=1|0 overrides it in developer builds; CUO_MESH_VIS_CHECK=1 compares every layer's indices and
        // runs with the bool path's). Default off.
        [JsonPropertyName("mesh_vis_bits")] public bool MeshVisBits { get; set; }

        // opl-spread: property-list requests (0xD6) leave at this many serials per second from a queue ordered hovered/gump
        // first, then by distance from the player (OplRequestQueue), and requests for removed objects are dropped. 0 = off:
        // one list, 15 per frame. Read once at startup (CUO_OPL_REQUEST_BUDGET=<n> overrides it in developer builds;
        // CUO_OPL_SPREAD_CHECK=1 checks every send against the requests). Default off.
        [JsonPropertyName("opl_request_budget")] public int OplRequestBudget { get; set; }

        // clip-1: the world walk skips only an object over its max Z instead of ending the tile's walk there (the tile list is
        // in PriorityZ order, not PriorityZ + height, so the old rule dropped lower objects after a tall one). Read at every
        // cut (CUO_WALK_MAXZ=1|0 overrides it and CUO_WALK_MAXZ_CHECK=1 compares the drawn set with the old walk's, in
        // developer builds; WalkMaxZ). Default off.
        [JsonPropertyName("walk_maxz_continue")] public bool WalkMaxZContinue { get; set; }

        // clip-2: per-pixel geometric depth by shape class instead of one depth key per sprite (feet and large bodies
        // through walls; Game/Scenes/DepthModel). Gpu renderer only so far. CUO_DEPTH_MODEL=1|0 overrides it in developer
        // builds. Default off.
        [JsonPropertyName("depth_model")] public bool DepthModel { get; set; }

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
