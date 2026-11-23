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

    internal sealed class Settings : IJsonOnDeserializing
    {
        public const string SETTINGS_FILENAME = "settings.json";
        public const int CurrentSettingsVersion = 1;

        [JsonPropertyName("settings_version")] public int SettingsVersion { get; set; } = CurrentSettingsVersion;

        // New objects carry the current version; files without a version predate all migrations.
        void IJsonOnDeserializing.OnDeserializing() => SettingsVersion = 0;

        internal bool Migrate()
        {
            if (SettingsVersion >= CurrentSettingsVersion)
                return false;

            // Before version 1 this hidden switch was saved as false by default. Apply the owner's new default once.
            PresentAtDisplayRate = true;
            SettingsVersion = CurrentSettingsVersion;
            return true;
        }

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

        // On for a new settings.json (owner, 2026-11-19); a saved "vsync": false is kept.
        [JsonPropertyName("vsync")] public bool VSync { get; set; } = true;

        [JsonPropertyName("screen_scale")] public float ScreenScale { get; set; } = 1f;

        [JsonConverter(typeof(NullablePoint2Converter))] [JsonPropertyName("window_position")] public Point? WindowPosition { get; set; }
        [JsonConverter(typeof(NullablePoint2Converter))] [JsonPropertyName("window_size")] public Point? WindowSize { get; set; }

        // The monitor the window opens on: an SDL display index (0-based) or a display name (or part of one); the -display
        // argument sets it. Empty: window_position alone decides. An index or name that matches nothing opens on the primary
        // (WindowPlacement).
        [JsonPropertyName("window_display")] public string WindowDisplay { get; set; }

        [JsonPropertyName("is_win_maximized")] public bool IsWindowMaximized { get; set; } = true;

        [JsonPropertyName("saveaccount")] public bool SaveAccount { get; set; }

        [JsonPropertyName("autologin")] public bool AutoLogin { get; set; }

        [JsonPropertyName("reconnect")] public bool Reconnect { get; set; }

        [JsonPropertyName("reconnect_time")] public int ReconnectTime { get; set; } = 1;

        [JsonPropertyName("login_music")] public bool LoginMusic { get; set; } = true;

        [JsonPropertyName("login_music_volume")] public int LoginMusicVolume { get; set; } = 70;

        // not read: GameController runs with IsFixedTimeStep = false; only the -fixed_time_step argument writes the key
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

        // gpu_force_point: the Gpu world samples with the point sampler at every scale instead of sharp-bilinear (a diagnostic
        // for tile grid lines; logged in [render-info], GameControllerRenderInfo). Default off.
        [JsonPropertyName("gpu_force_point")] public bool GpuForcePoint { get; set; }

        /// <summary>-worldrenderer auto|gpu|legacy: this run only, never saved.</summary>
        [JsonIgnore] public static string WorldRendererOverride { get; set; }

        // The world renderer this session uses: "gpu" (Uo.Render on FNA3D's SDL_GPU device) or "legacy" (the FNA batcher).
        // Set once at startup by RendererSelection (never mid-session for players); dev builds (CUO_DEVTOOLS) can switch it at
        // runtime (-worldrenderer command, DevBench world:/flip:/pick). Not saved.
        [JsonIgnore] public string WorldRenderer { get; set; } = "gpu";

        [JsonIgnore] public bool UseGpuWorldRenderer => string.Equals(WorldRenderer, "gpu", StringComparison.OrdinalIgnoreCase);


        // pa-gpusig, the Gpu prepare's ground-item cache: a served tile's cached items are trusted while their chunk's render generation
        // (GpuItemGen.Touch: the WorldWriter commits, text, boat offsets, object handles) is unchanged and the tile was served in
        // every pass since it was checked; otherwise their signatures are compared as before. Off = every served item's
        // signature is rebuilt and compared every frame. CUO_GPU_SIG_CHECK=1 compares both decisions at every use.
        [JsonPropertyName("gpu_prepare_item_cache_gen")] public bool GpuPrepareItemCacheGen { get; set; }

        // B8: the UI pass records its draws and leaves the UI targets untouched when they match the last frame's exactly; the
        // cursor goes over a kept copy of the UI (GameController.DrawUiCached). Off = the UI drawn every frame.
        [JsonPropertyName("ui_gump_dirty")] public bool UiGumpDirty { get; set; }
        // a text control keeps the glyph quads of its last draw and reuses them while its layout, atlas, width, font, style,
        // hue and cell are unchanged. CUO_TEXT_QUAD_CHECK=1 compares them with a fresh build. Default off.
        [JsonPropertyName("text_quad_cache")] public bool TextQuadCache { get; set; }

        // A minimized window skips world prepare, draw and present (GameController.Update); packets, update and audio keep running.
        // Not an ECS path, so there is no CUO_*_CHECK checker: nothing old to compare against, the draw is simply not run.
        // Default on (owner decision): a minimized window shows nothing, and several clients run at once.
        [JsonPropertyName("skip_draw_when_minimized")] public bool SkipDrawWhenMinimized { get; set; } = true;

        // An inactive (ReduceFPSWhenInactive) or minimized client sleeps 12 ms instead of 1 ms on frames that skip draw, so its
        // Update, input poll and packet drain run about 80 times a second instead of up to 1,000. A packet waits at most one
        // sleep. Not measured yet, so default off.
        [JsonPropertyName("throttle_update_when_inactive")] public bool ThrottleUpdateWhenInactive { get; set; }

        // M1: uncapped, vsync off, without inactive FPS reduction: skip Draw and Present until the display's next deadline.
        // Explicit FPS caps and inactive/minimized policies keep precedence. Unknown refresh uses the original path.
        [JsonPropertyName("present_at_display_rate")] public bool PresentAtDisplayRate { get; set; } = true;

        // vsync-poc: with vsync on (fps 0, window active, event loop) the display-rate schedule and present on change run as
        // for vsync off: unchanged frames skip Draw and Present and the loop waits for the next change instead of blocking in
        // Present every vblank. Off: vsync draws every tick. CUO_VSYNC_PRESENT_CHECK logs the presents it skips. Default on after
        // quiet-1119 (bench-000921/922: quiet idle CPU 55-90 -> 9-19 ms/s, ping RTT p50 5.9 -> 0.12 ms, both renderers).
        [JsonPropertyName("vsync_present_on_change")] public bool VsyncPresentOnChange { get; set; } = true;

        // motion_above_display_rate: with present on change and a present that does not wait for the vblank (vsync off, or
        // vsync_mailbox), frames while something moves present at three times the display rate. Presenting exactly at the
        // display rate without the vblank, the frame that lands near a refresh's scanout is shown twice or skipped depending on
        // a fraction of a millisecond of wake jitter; with the drift between the client's clock and the panel this comes and
        // goes as judder while walking (FramePacingSimulationTests). An idle screen still presents on change. Costs up to three
        // times the draws while moving. CUO_MOTION_RATE_CHECK logs the extra draws against the display-rate decision. Default off.
        [JsonPropertyName("motion_above_display_rate")] public bool MotionAboveDisplayRate { get; set; }

        // vsync-mailbox: vsync on presents in mailbox mode where FNA3D's SDL_GPU driver and the window support it (no tearing,
        // no queued frames; Present does not block), else in normal vsync. In mailbox mode the display-rate schedule and present
        // on change run as for vsync off. The Options menu shows the mode in force. Was default on after quiet-1119 (bench-000919/920:
        // idle CPU 68-90 -> 9-22 ms/s, walking 70-84 -> 25-37, ping RTT p50 5.9-7.5 -> 0.12 ms, both renderers).
        // Default off since 2026-11-22 (owner): mailbox at the display rate judders as presents drift against scanout (bg/frame-pacing
        // simulation); FIFO with present on change and the deadline fix measured similar idle CPU. Mailbox stays opt-in.
        [JsonPropertyName("vsync_mailbox")] public bool VsyncMailbox { get; set; } = false;

        // The event loop (GameController.EventLoopWait; Windows, and Linux with event_loop_linux) waits between frames for the socket, window messages, a wake
        // or the next deadline. This is its longest wait without an Update, for timers the loop does not track (macros,
        // audio streaming). 16 ms keeps Update at 60 Hz or more; a larger value saves wake-ups on idle clients.
        [JsonPropertyName("event_loop_max_wait_ms")] public int EventLoopMaxWaitMs { get; set; } = 16;

        // loop-idle: the event loop gathers the non-visual work Update polls (LoopDue: use queue, delayed click, music refill,
        // driver waits, the ping; input held, a macro, pending decodes and OPL requests keep the cadence above) and, when none
        // of it polls, waits until the next known deadline, at most event_loop_idle_max_wait_ms, instead of at most
        // event_loop_max_wait_ms. Out of game the cadence above stays. CUO_LOOP_IDLE_CHECK reports deadlines served late. Off:
        // unchanged. Default on after quiet-1119 (bench-000923/924: quiet idle loops/s 63 -> 7, CPU about halved on Gpu; ping
        // unchanged within ~20 us; busy and walking unchanged).
        [JsonPropertyName("event_loop_idle_wait")] public bool EventLoopIdleWait { get; set; } = true;

        [JsonPropertyName("event_loop_idle_max_wait_ms")] public int EventLoopIdleMaxWaitMs { get; set; } = 250;

        // gpu-popin-2: a frame presented with a missing sprite or chunk while a load is pending (Gpu: the view was not ready
        // or held; Legacy: decodes pending) is followed by a present as soon as a worker load completes (AssetWake: art,
        // texmap, gump and animation decodes, Gpu page loads and chunk bakes wake the event-loop wait and count as a visual
        // change), with a fallback deadline (AssetWake.FallbackMs). CUO_ASSET_WAKE_CHECK=1 counts completed loads still
        // missing and presents later than one display interval after a completion. CUO_ASSET_READY_WAKE=1|0 overrides it
        // in developer builds. Default off.
        [JsonPropertyName("asset_ready_wake")] public bool AssetReadyWake { get; set; }

        // gpu-popin-2: while the player moves, one chunk ring beyond the draw range on the side it moves to is requested
        // ahead: the client's chunk loads (Map.RequestRing, after the visible chunks, inside the chunk budget) and the Gpu
        // world's static art pages (WorldRenderer.PrefetchRing, only while no visible page waits, at most 2 pages a frame;
        // the page LRU keeps its layer budget). Counted by the [popin] chunk_pages/client_queued counters.
        // CUO_CHUNK_RING_PREFETCH=1|0 overrides it in developer builds. Default off.
        [JsonPropertyName("chunk_ring_prefetch")] public bool ChunkRingPrefetch { get; set; }

        // gpu-popin-2: the WorldWriter operations that place a mobile or item, change a body, graphic or hue, or equip,
        // request the new look's assets on packet arrival (LookPrefetch: the animation direction the draw will use, then
        // the action's other directions on Gpu; mount and equipment; an item's art), at most 16 objects an Update, objects
        // beyond the view range skipped. CUO_LOOK_PREFETCH_CHECK=1 measures packet-to-complete-draw latency per kind in both
        // states. CUO_ASSET_PREFETCH_ON_PACKET=1|0 overrides it in developer builds. Default off.
        [JsonPropertyName("asset_prefetch_on_packet")] public bool AssetPrefetchOnPacket { get; set; }

        // gpu-popin round 5 (owner 2026-11-20: a brief turn delay on one mobile over a whole-frame stall): a turn or new
        // action of a body already drawn decodes its direction on a worker (Animations.AsyncTurn) instead of on the main
        // thread; the mobile keeps its previous look until the direction is uploaded, at most LookPrefetch.HoldMaxMs. Moves,
        // turns and war-mode changes request the facing direction and its two neighbours early (AnimAsyncTurn).
        // CUO_ANIM_ASYNC_TURN_CHECK=1 counts the main-thread decodes left and the turn delays in both states;
        // CUO_ANIM_ASYNC_TURN=1|0 overrides the setting in developer builds. Default on (owner, 2026-11-22): without it the Gpu
        // world draws a mobile part whose direction page is not resident as nothing for a frame (the first-turn flash, bg/turn-flash).
        [JsonPropertyName("anim_async_turn")] public bool AnimAsyncTurn { get; set; } = true;

        // mount-fix (owner 2026-11-23: the horse left by a dismount fades in from invisible, a flash every cycle): on, a mobile
        // the server creates starts at alpha 0 and fades in (the upstream behaviour); off, it is drawn at its target alpha
        // from its first frame (full, or 0 above the max Z). Roof, max-Z, translucency and chunk-stream fades are unchanged;
        // items keep their fade. CUO_MOBILE_FADE_CHECK=1 counts mobiles first drawn below their target and the time until
        // they reach it; CUO_MOBILE_FADE_IN=1|0 overrides the setting in developer builds. Default on; off is the new path.
        [JsonPropertyName("mobile_fade_in")] public bool MobileFadeIn { get; set; } = true;

        // mount-fix: object fades (roof, max Z, new objects) catch up the 20 ms steps a long frame missed in every pacing
        // mode, so a fade takes the same ~220 ms at any frame rate; before, only display-paced drawing caught up and an
        // unpaced 12 fps frame took one step (~917 ms). CUO_FADE_TIME_BASED=1|0 overrides it in developer builds. Default off.
        [JsonPropertyName("fade_time_based")] public bool FadeTimeBased { get; set; }

        // light-edges (owner 2026-11-21: trees, roofs and other tall art beside the view popped in whole): the sprite margin.
        // The walk reaches the objects whose art reaches the view from beside it (the chunks' recorded overhang,
        // Chunk.StaticOver*). Its light part is chunk_lights since round 6c. CUO_VIEW_MARGIN_CHECK=1 logs sprites reaching the
        // view that the walk did not draw; CUO_VIEW_MARGIN=1|0 overrides the setting in developer builds. Default off.
        [JsonPropertyName("view_margin")] public bool ViewMargin { get; set; }

        // light-edges round 6c (split from view_margin): static and multi lights from the chunks' emitter lists instead of
        // the walk (Deceit's wall torches lit before they enter the view; the gradient circle of transparency no longer drops
        // every static light), a growable ranked light list instead of the classic first 100, and no slot for a light whose
        // halo cannot reach the view (ChunkLights). CUO_CHUNK_LIGHTS_CHECK=1 compares the band lights with a walk of those
        // chunks; CUO_CHUNK_LIGHTS=1|0 overrides the setting in developer builds. Default off.
        [JsonPropertyName("chunk_lights")] public bool ChunkLights { get; set; }

        // light-edges: a surface glow's halo (a lit window's beam: light_alpha_fade's class, lightclass.bin, else the
        // emitter's tiledata Wall/Window flags) is not drawn where another static, multi piece or ground item in front of its
        // source covers the source's light point, nor past wall pieces facing away from a lit wall source, so lit windows on a
        // far wall no longer glow through the near wall. Its own emitter, objects on its own tile, translucent tiles, floors
        // and mobiles do not hide it. Radial lights (lamps, candles, torches, fire fields, mobiles' lights) are never masked:
        // the light map multiplies the frame, so they light the objects in front of them (round 7, Luna 1005 513 -30; folded
        // in 2026-11-22). Per masked light, the occluders' drawn sprites go into the light target's stencil
        // (GameSceneLightOcclusion, LightOcclusion.Masks). Both renderers. CUO_LIGHT_OCCLUSION_CHECK=1 compares the occluders
        // found with a scan of every tile of the drawn chunks and logs any radial light drawn masked; CUO_LIGHT_OCCLUSION=1|0
        // overrides the setting in developer builds. Default off.
        [JsonPropertyName("light_occlusion")] public bool LightOcclusion { get; set; }

        // light-edges round 6b: a surface glow (a lit window's beam: the light shape's class in the pack's lightclass.bin,
        // else the emitter's tiledata Wall/Window flags) is drawn at its emitter's drawn alpha (circle of transparency, roof
        // fade), so hidden walls leave no floating glows; radial glows (lamps, torches) keep full light. With light_occlusion
        // on, occluder masks are dithered to their drawn alpha instead of cut at 128 (LightAlphaFade). Both renderers.
        // CUO_LIGHT_ALPHA_FADE_CHECK=1 compares with the unfaded path; CUO_LIGHT_ALPHA_FADE=1|0 overrides the setting in
        // developer builds. Default off.
        [JsonPropertyName("light_alpha_fade")] public bool LightAlphaFade { get; set; }

        // light-edges round 6c (owner 2026-11-22: zoomed out with every light in range drawn, the light pass lagged): with
        // chunk_lights on, a light farther from the player than light_range_tiles (0 = auto: just past the zoom-1 view plus
        // the largest halo) is dropped, fading over its last 2 tiles, and at most light_budget lights are kept (the ranked
        // list's best), the lowest-ranked 16 fading by rank (LightRange). Both renderers. CUO_LIGHT_RANGE_CHECK=1 checks the
        // kept, dropped and faded lights against the rule; CUO_LIGHT_RANGE=1|0 overrides the setting in developer builds.
        // Default off.
        [JsonPropertyName("light_range")] public bool LightRange { get; set; }

        [JsonPropertyName("light_range_tiles")] public int LightRangeTiles { get; set; }

        [JsonPropertyName("light_budget")] public int LightBudget { get; set; } = ClassicUO.Game.Scenes.LightRange.DefaultBudget;

        // light-edges round 6c: with light_occlusion on, the light pass collects every light's occluders first, draws the
        // lights with none in one batch without the stencil, then the occluded ones with their masks as before (additive
        // blending: the same light target). Both renderers. CUO_LIGHT_BATCH_CHECK=1 renders the per-light path beside it once
        // a second and compares the light targets pixel by pixel; CUO_LIGHT_BATCH=1|0 overrides the setting in developer
        // builds. Default off.
        [JsonPropertyName("light_batch")] public bool LightBatch { get; set; }

        // G11a composite fill (gump-system survey, addendum 4): the final composite copies one opaque border texture (black,
        // the background pattern and the UI outside the world viewport, baked when the UI target or the geometry changes)
        // instead of a full-window clear, a full-window pattern pass and a full-window UI blend; the UI is blended only inside
        // the viewport (RenderTargets.BorderBake, UiComposite). Costs one back-buffer-sized target (8 MB at 1080p, 33 MB at
        // 4K). Live. CUO_UI_COMPOSITE_CHECK=1|N compares both composites pixel for pixel; CUO_UI_BORDER_BAKE=1|0 overrides the
        // setting in developer builds. Default off.
        [JsonPropertyName("ui_border_bake")] public bool UiBorderBake { get; set; }

        // worldmap-audit P-1: with the world map open, the event loop polls at event_loop_max_wait_ms only while a map image
        // loads (task running or image not yet uploaded); otherwise the gump's work is deadlines (the 250 ms party/guild
        // query, the 1 s stale-entity prune) and packets and input wake it as before. Off: an open map keeps the loop at the
        // event_loop_max_wait_ms cadence (perf-opportunities "World map gump open keeps the 16 ms loop cadence").
        // CUO_WORLDMAP_IDLE_CHECK reports map state that changed after an idle wait it was not planned for, and late map work;
        // CUO_WORLDMAP_IDLE_LOOP=1|0 overrides the setting in developer builds. Default off.
        [JsonPropertyName("worldmap_idle_loop")] public bool WorldMapIdleLoop { get; set; }

        // loop-linux: the event loop on Linux, waiting in epoll (LinuxEpollLoopWaiter: socket, wake eventfd, timerfd deadline,
        // X11/Wayland display fd, SDL event watch) instead of the legacy loop's Sleep(1) and spin. Read once at startup; the
        // log names the waiter ("main loop: Event (epoll-x11)"). Off: Linux runs the legacy loop. No effect on other OSes.
        [JsonPropertyName("event_loop_linux")] public bool EventLoopLinux { get; set; }

        // loop-macos: the event loop on macOS (MacLoopWaiter: SDL's Cocoa wait, ended by a kqueue over the socket and the
        // cross-thread wake through a main run-loop source). Off: macOS runs the legacy loop, as before. CUO_EVENT_LOOP_MACOS=1|0
        // overrides it in developer builds. Windows ignores it.
        [JsonPropertyName("event_loop_macos")] public bool EventLoopMacOS { get; set; }

        // perf-4: the static art of a container's contents and of the paperdoll's equipment slots decodes on the art workers
        // before the gump builds its item controls (ContentsArt: a request when an item goes into an open container, a
        // bounded batch wait in ContainerGump/PaperDollGump.UpdateContents) instead of one main-thread decode per sprite.
        // Read at every use (CUO_ART_CONTAINER_PREFETCH=1|0 overrides it in developer builds). Default off.
        [JsonPropertyName("art_container_prefetch")] public bool ArtContainerPrefetch { get; set; }

        // buffer-pools: gump worker decodes are counted in their own 32 MiB decode budget (DecodeBudget.Gumps) and wait for
        // room there, so a burst of large gumps cannot hold an unbounded amount of pooled pixels, and UI decodes never wait
        // behind world decodes. Off: gump decodes are not counted, as before. Read at startup (CUO_GUMP_DECODE_BUDGET=1|0
        // overrides it in developer builds); CUO_GUMP_BUDGET_CHECK=1 compares held and admitted bytes. Default off.
        [JsonPropertyName("gump_decode_budget")] public bool GumpDecodeBudget { get; set; }

        // gump-system G3: server gumps (0xB0/0xDD) parse their layout in one pass over the string into token ranges (GumpLayout)
        // and build the controls by entry kind, instead of two tokenizer passes that make a string per token and a list per
        // entry. Read at every open; CUO_GUMP_LAYOUT_V2=1|0 overrides it in developer builds, CUO_GUMP_LAYOUT_CHECK=1 compares
        // each parse and built tree with the old path. Default off.
        [JsonPropertyName("gump_layout_v2")] public bool GumpLayoutV2 { get; set; }

        // gump-system G4 (needs gump_layout_v2): the parsed layouts of the last 32 server gumps by (gump id, layout), so a resend
        // of the same layout skips the parse. Read at every open; CUO_GUMP_LAYOUT_CACHE=1|0 overrides it in developer builds;
        // CUO_GUMP_LAYOUT_CHECK=1 checks every hit against the old tokenizer. Default off.
        [JsonPropertyName("gump_layout_cache")] public bool GumpLayoutCache { get; set; }

        // gump-system G5: a text control's font layout (RenderedText) goes back to the font loader's node pool when its text
        // changes or it is destroyed, so the next label reuses the nodes and their glyph lists instead of growing new ones
        // (~0.9 KB per label line). Read at every text set; CUO_FONT_LAYOUT_POOL=1|0 overrides it in developer builds,
        // CUO_FONT_LAYOUT_CHECK=1 keeps a private copy of each layout and compares it at draws and on release. Default off.
        [JsonPropertyName("font_layout_pool")] public bool FontLayoutPool { get; set; }

        // gump-system Q1 (b) (needs gump_layout_v2): a server gump resent with the open gump's layout keeps its controls and
        // only the text lines that changed are patched in; typed text, checkbox/radio states, html scroll and the page survive
        // the refresh (a text entry whose own line changed takes the server's value). Off: the gump is rebuilt, as before.
        // Read at every open; CUO_GUMP_RESEND_PATCH=1|0 overrides it in developer builds, CUO_GUMP_RESEND_CHECK=1 compares
        // each patched gump with an old-path rebuild. Default off.
        [JsonPropertyName("gump_resend_patch")] public bool GumpResendPatch { get; set; }

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

        // atlas-lru: the Art, Gump, Texmap, Light and font glyph atlases free their least recently used pages above a
        // budget per atlas, with anim_atlas_lru's policy (AtlasLruSetup); evicted sprites load again on use, and the glyph
        // caches drop the entries of a freed page (bounds the coloured-glyph cache). Read once at startup (CUO_ATLAS_LRU=1|0
        // and the CUO_ATLAS_LRU_* sizes override it in developer builds; CUO_ATLAS_LRU_CHECK=1 checks reloads and draws).
        // Default off: the atlases only grow.
        [JsonPropertyName("atlas_lru")] public bool AtlasLru { get; set; }

        // shared-tables: cliloc tables are read in place from read-only memory every instance shares (SharedTableCache):
        // a raw record straight from the pack file, a compressed one from a per-user cache file it is decompressed into
        // once (keyed by the pack's manifest hash), instead of a heap copy per process. Read once at startup
        // (CUO_SHARED_CLILOC=1|0 overrides it in developer builds; CUO_SHARED_CLILOC_CHECK=1 compares every lookup with the
        // heap path's). Default off.
        [JsonPropertyName("shared_cliloc")] public bool SharedCliloc { get; set; }

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
        // through walls; Game/Scenes/DepthModel). Both renderers (Legacy since clip-4). CUO_DEPTH_MODEL=1|0 overrides it in developer
        // builds. Default off.
        [JsonPropertyName("depth_model")] public bool DepthModel { get; set; }

        // A mobile under a water surface (a wet static above its z on its tile) is drawn submerged: with depth_model, wet flats
        // drop 0.1 tile sums instead of FlatDrop's 1.5 (the waterline at the true height); without it, the mobile's strips all
        // take + 1 (no floating feet under the V-cut). Game/Scenes/WetSurfaceDepth. CUO_WET_SURFACE_DEPTH=1|0 overrides it and
        // CUO_WET_DEPTH_CHECK=1 logs the mobiles under a wet surface, in developer builds. Default off.
        [JsonPropertyName("wet_surface_depth")] public bool WetSurfaceDepth { get; set; }

        // Depth key mode (depth_model off): a mobile's strips below its anchor row (feet, robe hem, hooves) are capped just behind
        // a wall ahead of it (one or two diagonals, its height holding the mobile's z, its in-front half over the strip), so they
        // no longer show on the wall's face. Game/Scenes/MobileWallOrder. CUO_MOBILE_WALL_ORDER=1|0 overrides it and
        // CUO_MOBILE_WALL_ORDER_CHECK=1 logs every strip it moves, in developer builds. Default off.
        [JsonPropertyName("mobile_wall_order")] public bool MobileWallOrder { get; set; }

        // step_drain: a non-local mobile's step lasts its learned server pace (never longer than the table time; faster
        // only when the pace is clearly faster), with a bounded drain of a real backlog, instead of the table time and the
        // "mounted when steps are queued" guess (Game/Ecs/StepDrain). CUO_STEP_DRAIN=1|0 overrides it and
        // CUO_STEP_DRAIN_CHECK=1 compares the old timing with the new, in developer builds. Default off.
        [JsonPropertyName("step_drain")] public bool StepDrain { get; set; }

        // span_reader (net-rework step 2): packets are read with ModernUO's SpanReader and built with its SpanWriter instead of
        // StackDataReader / StackDataWriter (Network/NetReader, NetWriter, SpanBuffers). A packet a handler reads past its end
        // then ends the connection, logged with its bytes. Read once at startup (CUO_SPAN_READER=1|0 overrides it and
        // CUO_SPAN_READER_CHECK=1 runs both readers and writers and logs differences, in developer builds). Default on
        // (2026-11-21: per-packet and pipe tests pass unchanged in both states; the old path stays for a while).
        [JsonPropertyName("span_reader")] public bool SpanReader { get; set; } = true;

        // net_ring (net-rework step 3): received bytes are decoded into a double-mapped ring per connection and framed and read
        // in place; sends go through a bounded ring that ends the connection when the socket takes nothing for 5 s
        // (Network/NetRing, NetRings). Read once at startup (CUO_NET_RING=1|0 overrides it and CUO_NET_RING_CHECK=1 frames
        // every slice with the CircularBuffer path too and logs differences, in developer builds). A platform that cannot map
        // the ring keeps the CircularBuffer path (one log line). Default off.
        [JsonPropertyName("net_ring")] public bool NetRing { get; set; }

        // text_string_cache: server Cp1252 strings are decoded through a bounded cache keyed by their bytes (1024 sets x 2
        // ways, strings up to 64 bytes), so repeated names and lines allocate nothing (Game/TextStringCache). CUO_TEXT_STRING_CACHE=1|0
        // overrides it and CUO_TEXT_CACHE_CHECK=1 compares every cached string with a fresh decode, in developer builds.
        // Default on (2026-11-20: 85.6 % hit rate, ~166 KiB per tour not allocated, ~45 KiB held per client, at most ~0.3 MB).
        [JsonPropertyName("text_string_cache")] public bool TextStringCache { get; set; } = true;

        [JsonPropertyName("encryption")] public byte Encryption { get; set; }

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
