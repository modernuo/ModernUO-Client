# World settle: holding the last frame on entry, teleports and recreation

How the "hold the last world frame until the GPU world is ready" work (`WorldSettleGate`) is tested. Written 2026-10-25 by the world-settle agent. Read [testing-playbook.md](../testing-playbook.md) first for accounts, locks and cleanup.

## What it tests

- **No raw frames after a discontinuity:** a world entry (login), a teleport (the player moves more than 8 tiles in one frame), a facet change, a recreated GPU world pass, or a switch back from the developer Legacy mode. The client presents no frame with unbuilt chunks (gaps, black land) or missing atlas pages until the view is ready or 1.5 s pass.
- **Walking never holds.**
- **No Legacy frames** unless Legacy is requested explicitly. If the GPU world can't be set up, the client exits with code `0xDEAF`.

## Switches

| Variable or entry | Effect |
|---|---|
| `CUO_WORLD_HOLD=0` | Observe only: never hold, still count. This is the "before" baseline (the base build presents every frame) |
| `CUO_WORLD_HOLD_TIMEOUT=<ms>` | Hold timeout (default 1500) |
| `CUO_WORLD_SETTLE_LOG=1` | Logs every presented frame that wasn't ready: chunks missing near the view, pages missing, anim pages missing |
| `CUO_BENCH_TPSHOTS=<dir>[\|frames]` | In `ltravel`, `reinit` and `reinitrt`: saves the world, lights and UI targets of the first N frames (default 8) after each teleport or recreation as `tpNN_fMM.png`. The separator is `\|` because the path has a colon |
| Plan mode `name:reinit` | Drops the GPU world pass every 2 s. It is recreated on the next frame, as on a facet change |
| Plan mode `name:reinitrt` | Drops the world render target as well, as at login. There is nothing to hold, so the world area shows black and then fades in |
| `world:legacy` / `world:gpu` | Developer flip. The switch back holds Legacy's last frame until the GPU world is ready |

**Log lines:**
- **`[settle] <Kind>: ready|timed out after N ms, H frames held, U unsettled frames presented`**, one per episode. Kind is Entry, Teleport, Facet, Recreated or Resume.
- **`[settle] <entry>: hold on|off; presented …`**, the cumulative counters at the end of each plan entry. It contains:
  - unsettled frames presented, split into those while walking, those after a timeout, and those in an episode without hold;
  - episodes, holds, held frames (black ones in brackets), hold ms (total and max), timeouts;
  - Legacy frames.

## Procedure (about 10 minutes)

Use the kbatman3 lock and a throwaway settings.json with the password encrypted, as in the playbook. The runner the world-settle agent used is in its scratchpad, `settle\run.py`; it takes `--env`, `--fps` and `--record`.

1. **A/B, alternated:** run plan `w:walk;lt:ltravel` with `CUO_FRAMESTATS=1` and `CUO_WORLD_SETTLE_LOG=1`, twice with `CUO_WORLD_HOLD=0` and twice with hold on, alternating.
   - Compare the `[settle]` lines.
   - Compare FrameStats for walk and ltravel: fps, frame p50 and p99, world_prepare, world_draw.
2. **Flip and recreation:** run plan `g1:walk;world:legacy;l1:walk;world:gpu;g2:walk;r:reinit;rr:reinitrt` with hold off, then with hold on.
3. **Visual check:** run plan `lt:ltravel;r:reinit;rr:reinitrt` at `-fps 60` without FrameStats, with `CUO_BENCH_TPSHOTS=<dir>|6`, hold off and on. Look at the images.
   - With hold off, the first frames after a teleport are black apart from mobiles and items.
   - With hold on, the held frames are byte-identical copies of the last frame at the old place, followed by a complete new frame.
   - The shots' PNG encodes slow those frames to about 150 ms each, so episode durations in such a run are not representative.
4. **Saved setting:** a settings.json with `"world_renderer": "legacy"` and no `-worldrenderer` flag logs a warning and runs Gpu (legacy frames 0).
5. **Setup failure:** `FNA3D_FORCE_DRIVER=D3D11` (no SDL_GPU device) must exit with `0xDEAF` after login, with no Legacy frame drawn.

## Pass criteria

- With hold on, 0 unsettled frames are presented in any episode; only frames after a timeout may be unsettled, and they are counted.
- Legacy frames are 0 unless the plan has `world:legacy`.
- Walking unsettled frames are equal with hold on and off, since walking never holds. These are single-page misses lasting about 10 ms at ~1000 fps.
- No FrameStats regression beyond noise.

## Known noise and limits

- ffmpeg `gdigrab` of the client window doesn't work on this machine: the 150% DPI scaling gives a wrong capture region, and other windows overlap it. Use `CUO_BENCH_TPSHOTS` instead. The shots show the world target, which is what the composite draws under the lights and UI.
- `CUO_GPU_REINIT` (the page-fault agent's stress hook) drops the world RT too. So it looks like `reinitrt` (black, then fade-in), not like `reinit` (held frame).
- **Hold lengths:**

  | Event | ~1000 fps (uncapped) | `-fps 60` |
  |---|---|---|
  | Teleport | 5–16 frames, 15–60 ms | 3–5 frames, 46–82 ms |
  | `reinit` | 19–39 frames, 50–65 ms | 8 frames, about 133 ms |
  | Login | 8–14 black frames, 460–615 ms, then a 250 ms fade-in | same |
