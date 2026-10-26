# Renderer selection at startup: test matrix

What it tests: the startup choice of world renderer (`RendererSelection`/`RendererStartup`, poc-log "Renderer chosen at startup by capability"): auto picks Gpu where SDL_GPU/Vulkan and the Gpu world pass work, falls back to Legacy (D3D11, OpenGL or the SDL_GPU device) where they don't, honours forced `gpu`/`legacy` from settings.json and `-worldrenderer`, and a forced `gpu` that can't run shows an error and exits `0xDEAF`. Run it after any change to the startup path, the settings, `GpuWorldRenderer` creation or the build flavours, and on both flavours (default and `-p:CuoDevTools=false`).

## Setup

- Two builds of the same commit in your scratch folder: `bin-dev` (default) and `bin-nodev` (`-p:CuoDevTools=false`). Check the flavour: the strings `flip done`, `[bench-pick]` and `CUO_BENCH_PLAN` are in the dev `cuo.dll` (UTF-16) and absent from the release one.
- One session per case, each with a throwaway settings.json (`world_renderer` set or left out), the encrypted password and a temp profile folder; account lock as in the playbook. Close every client.
- Scripts used (2026-10-25, session scratchpad `correct\rendsel\`): `run.py` (one session; `--setting`, `--arg`, `--plan`, `--shots`, `--msgbox`, `--grab`) and `matrix.sh <bin> <group...>` with groups `startup`, `titles`, `msgbox`, `d3d`, `gl`, `world`, `nodev`.

## Cases

| Case | How | Pass |
|---|---|---|
| auto → Gpu | no `world_renderer` | `[renderer] auto: Gpu (SDL_GPU vulkan, <GPU>)`, exit 0 |
| Forced legacy / gpu, settings.json | `"world_renderer": "legacy"` / `"gpu"` | `legacy: Legacy (forced by settings.json; …)` / `gpu: Gpu (…)` |
| Forced legacy / gpu, command line | `-worldrenderer legacy` with `gpu` saved, and the reverse | `forced by -worldrenderer`; the saved value is unchanged after the run |
| Fallback to Legacy on D3D11 | auto + `CUO_RENDERER_PROBE_FAIL=device` | `auto: Legacy (…; FNA3D D3D11)`, `FNA3D Driver: D3D11`, `world:gpu` refused with the `runtime switch to gpu refused` warning, no errors |
| Fallback to Legacy on OpenGL | the same + `CUO_RENDERER_LEGACY_DRIVER=OpenGL` | `FNA3D OpenGL` (lands on the Intel UHD 770 on this machine) |
| Fallback to Legacy on the SDL_GPU device | auto + `CUO_RENDERER_PROBE_FAIL=world` | `auto: Legacy (…; SDL_GPU vulkan, <GPU>)` |
| User-forced OpenGL | auto + `FNA3D_FORCE_DRIVER=OpenGL` | `auto: Legacy (FNA3D driver forced to OpenGL; …)`, no probe |
| Forced gpu that can't run | `gpu` (settings or `-worldrenderer`) + `PROBE_FAIL=device` or `world` | `gpu: unavailable (…)`, `GPU world renderer setup failed`, exit `0xDEAF` |
| Fallback sessions still work | each fallback with a plan (Britain, `gumps`, bank, walk, house) and `CUO_BENCH_PICK=1` | Shots and picks as on Legacy/SDL_GPU; house near-black share as Legacy/SDL_GPU at the same size |

## Looking at the message box and the title

- Plans (`CUO_BENCH_*`) and `-nomessagebox` suppress the error box. For the box, run the forced-gpu failure without either and without logging in (it fails before login); capture the dialog (`#32770`) and the client window with `PrintWindow`, from a **DPI-aware** process (otherwise the window rectangles are scaled and the captures cropped), then press OK and check the exit code.
- The title is the client window's text (`GetWindowText`): `ClassicUO - <version>` in Gpu mode, `ClassicUO [Legacy renderer] - <version>` in Legacy, prefixed with the character name once logged in.

## Time

About 25 minutes for both flavours: about 23 s per startup case, 90 s per fallback plan, 45 s per picking run, 35 s per release-flavour case.
