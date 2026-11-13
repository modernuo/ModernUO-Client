# M1: draw and present at the window display rate

`settings.json`: `"present_at_display_rate": true` (default false). Both world renderers use the same controller pacing. The polling loop still runs Update, SDL input, network drain/flush, timers, UI updates, cursor and audio every iteration. A suppressed frame calls FNA SuppressDraw and never enters Draw or EndDraw. There is no sleep in this polling path. This is software pacing, not synchronization to the monitor's scanout phase.

## Policy

- Only FPS setting 0 and vsync off qualify. An explicit FPS cap, including one above refresh, retains the existing policy. DevBench/FrameStats forcing the effective interval to zero does not override an explicit nonzero FPS setting for this switch.
- Vsync takes precedence; software display pacing is disabled while vsync is enabled.
- Existing minimized suppression and inactive FPS reduction take precedence. An unfocused client with inactive reduction disabled remains eligible. Minimized Update throttling and event-loop maximum waits keep their existing behavior.
- `event_loop` uses the absolute draw deadline alongside its socket/input wake, queued actions, player-step deadline and maximum wait. Early network/input wakes update without drawing until due. If the event waiter is unavailable or backs off, polling continues without a sleep in this path.
- SDL_GetDisplayForWindow chooses the window's display (SDL's choice for spanning windows). SDL_GetCurrentDisplayMode supplies the fractional refresh rate. Poll once per second while enabled or checked: monitor changes, unplug/replug and refresh changes reset pacing. There can be up to one second of the old rate during a change. Zero, unavailable or nonfinite refresh falls back to the old uncapped path; no guessed 60 Hz limit.
- Deadlines are integer Stopwatch ticks, with rounded frequency/Hz period. Next is previous deadline plus period; missing a whole period resynchronizes to now plus period, without catch-up bursts. Disabling/re-enabling or changing displays resets the phase and permits an immediate first draw.
- DevBench and FrameStats honor the switch; they do not silently uncap Draw. Pickscan and renderer flip stress advance their frame counters on completed presents. Other time-based automation continues on Update. FrameStats alloc/frame and phase totals describe one presented frame, including all intervening Updates; compare allocations/second too when loop counts differ.

## Draw-owned state

SelectedObject, healthbar/container selection, overhead ordering and UI draw state retain the last rendered result between draws. Input continues every Update; visual picking can therefore lag by a display period plus any stall, as with an ordinary render cap. The gate scans picking at actual presented frames using Input.Mouse.Override, with no OS cursor movement.

Mobile/item/effect animation advances and text lifetime/alpha use Update/game time. World-object fades previously took one fixed 25-alpha step per draw opportunity after a 20 ms timer. The paced path carries elapsed fade steps, preventing a 60 Hz draw loop from stretching a 20 ms fade clock to 33 ms. The old path keeps its previous fade behavior. Weather Draw already advances by elapsed Time.Ticks, so no skipped-draw simulation is called in this path (that minimized-only helper resets the particle clock). World-settle time is not paused between display draws; residency and asset preparation still run in Update.

## Counters and checker

The UI FPS number counts completed EndDraw calls, alongside loops/s. LoopStats separates actual draws, completed presents and drains and logs CPU ms/s. CUO_DIAG keeps separate updates/draws/presents/drains, raw ping samples and per-ping maximum Update interval. Its maximum Update duration now measures individual Updates, not the accumulated Updates between presents. FrameStats sums packet counts across intervening Updates and accumulates loop gaps separately from Update work.

`CUO_PRESENT_CAP_CHECK=1` is compiled only under CUO_DEVTOOLS. Every five seconds it logs switch/active state, refresh Hz, draws/s, presents/s, updates/s, old draw decisions, avoided draws, maximum present gap, period and late/over-rate mismatch counts. It shadows the original draw decision on the same input; it does not execute a second Draw/Present, which would defeat the feature and change GPU state. A mismatch is an active presents/s rate above refresh * 1.05 or an active present gap over period + 1 ms. Late gaps on this busy machine remain mismatches, not waived passes. State transitions reset its window; the checker includes the current Update in the ending window (one-event boundary tolerance).

Additional work counters: FillGameObjectList full calls, walked objects and glyph quads; Uo.Render buffer/texture upload calls and bytes; FNA batch vertex-buffer upload calls and bytes. Texture atlas loaders outside these probes and native-driver internal copies are not counted. This base has no full-prepare reuse path: reuse hits are zero; M2 owns that work.

## Reproduction

Durable scripts/scenes: `cuo-poc-state/measure/present-cap/`. `set.env` names isolated base/new binaries; the driver encrypts the runner's password only in temporary settings, has a hard client timeout and removes its settings afterward. Pack CURRENT is read at job execution. Jobs honor account owner holds and shared locks.

- `job-gate.sh`: both renderers, switch off/on, explicit cap, vsync, event loop, forced inactive/minimized/restore, effects/UI and actual-frame picking scans. Strict pacing mismatches fail the gate, including late presents.
- `job-counts.sh`: both renderers; same binary/session/spot, off/on/off/on, then event on/off. CUO_DIAG and LoopStats record work counts, allocations, GC and indicative CPU/ping data; `counts-table.py` removes mixed boundary windows and emits raw windows and medians.
- `job-alloc-g.sh`, `job-alloc-l.sh`: handles/tour; `job-alloc-g-boatfx.sh`, `job-alloc-l-boatfx.sh`: boat/effects. Each compares base, branch default-off and branch-on, reversing arm order in tour. Checkers off. These are allocation checks, not frame-time conclusions.
- `job-timing-g-PARKED.sh`, `job-timing-l-PARKED.sh`: three rounds base/off/on, interleaved, DOTNET_TieredCompilation=0. Prepared only: coordinator submits after the machine is quiet.

Physical multi-monitor movement, hotplug and unknown-mode behavior need a manual display pass; unit tests cover the scheduler reset/fallback arithmetic, not SDL hardware events. Queued jobs supply runtime evidence; do not infer a runtime pass from the unit tests.
