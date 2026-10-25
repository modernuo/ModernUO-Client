# FNA and FNA3D forks (github.com/modernuo/FNA, github.com/modernuo/FNA3D)

The client builds FNA from our fork, and ships an FNA3D.dll built from our FNA3D fork. Written 2026-10-25; the owner created both forks that day and handles upstream contact.

## Why we fork

- **Interop:** ClassicUO shares FNA3D's SDL_GPU device with the Uo.Render world renderer. That needs two FNA3D exports upstream doesn't have (`FNA3D_GetSysRendererEXT` filling the SDL_GPU device, `FNA3D_SDLGPU_GetTextureEXT`) and the C# bindings for them in FNA. Before the forks this was a local patch (`external/patches/fna3d-sdlgpu-interop.patch`) plus local-only submodule commits nobody else could clone.
- **Two FNA3D SDL_GPU driver bugs:**
  - **A cycled index buffer isn't rebound** (the cause of the B1 Legacy corruption, see [known-issues.md](known-issues.md)). `SetIndexBufferData` with Discard cycles the buffer, but `SDLGPU_INTERNAL_BindDeferredState` skips `SDL_BindGPUIndexBuffer` when the handle is unchanged, so the open render pass keeps reading the old buffer. The batcher used to work around it by forcing an effect re-apply after each index upload (`Batcher2D.IndexDataChanged`, removed with the fix).
  - **A freed texture stays a pending render-pass target.** `SDLGPU_INTERNAL_FreeTextureHandle` cleared `boundRenderTargets` but not `nextRenderPass*`, so disposing a render target that was still set left `BeginRenderPass` reading freed memory (found by the Codex audit; never observed in the client).
- **Contribution policy:** FNA3D's `AGENTS.md` forbids AI-generated contributions, including bug reports. The patches stay in our forks. Don't open or draft upstream issues or PRs; the owner talks to the FNA team.

## Branch rule (owner, 2026-10-25)

A production branch is `modernuo/<release>-patched`, created from upstream's release tag, with our patches as the only commits on top, one commit per change. That keeps each patch re-applicable to any later release until it is fixed upstream. GitHub's ahead/behind numbers against `master` don't mean anything for these branches; compare with the tag (`git rev-list --count 26.10..modernuo/26.10-patched`). The same rule applies to the SDL fork ([sdl-fork.md](sdl-fork.md)).

## Branches

| Repo | Branch | Base | Commits on top |
|---|---|---|---|
| modernuo/FNA3D | `modernuo/26.10-patched` | tag `26.10` (62a41708) | f4c99c9 SDL_GPU interop exports; e3bd4f2 rebind the index buffer after a cycling upload; 19e5188 drop a freed texture from the pending render pass |
| modernuo/FNA | `modernuo/26.10-patched` | tag `26.10` (651c0ffd) | 7b7d14c C# bindings for the interop exports; 8e0a284 `lib/FNA3D` points at modernuo/FNA3D `modernuo/26.10-patched` |

The forks' `master` branches are untouched.

## Using it in the client

- `external/FNA` is a submodule of modernuo/FNA, branch `modernuo/26.10-patched` (`.gitmodules`). FNA's own `lib/FNA3D` submodule points at modernuo/FNA3D. After pulling, run `git submodule sync --recursive && git submodule update --init --recursive external/FNA`.
- The built `external/x64/FNA3D.dll` is committed with `external/x64/FNA3D.version` (the FNA3D and FNA commits, the SDL3.dll it was linked against, the build date and the SHA-256), so normal builds and `tools/fps-test/publish-aot.ps1` need no C toolchain.
- Only win-x64 is built. `external/lib64` and `external/osx` still hold the unpatched 26.10 dailies.

## Rebuilding

```
git submodule update --init --recursive external/FNA external/SDL
powershell -ExecutionPolicy Bypass -File tools\fna3d\build-fna3d.ps1
```

- **Needs:** CMake and the Visual Studio C++ tools (found with vswhere).
- **Links against our SDL fork:** headers from `external/SDL/include`, `SDL3.lib` from the SDL build folder (`%TEMP%\cuo-sdl-build`; the script runs `tools\sdl\build-sdl.ps1 -NoCopy` when it's missing).
- **Build settings:** Release, NMake, MSVC x64, static CRT. The DLL imports only `SDL3.dll` and `KERNEL32.dll`, and exports 85 `FNA3D_*` functions (84 upstream plus `FNA3D_SDLGPU_GetTextureEXT`).
- The build folder is `%TEMP%\cuo-fna3d-build`. The script copies the DLL to `external/x64/FNA3D.dll` and rewrites `FNA3D.version`. Commit both together with the submodule pointers.

## Moving to a newer FNA release

1. In `external/FNA/lib/FNA3D`, fetch the new tag from upstream by URL (`git fetch https://github.com/FNA-XNA/FNA3D.git tag <release>`), create `modernuo/<release>-patched` from it, and cherry-pick our commits. Drop any that upstream has fixed. Push to modernuo/FNA3D.
2. In `external/FNA`, do the same from FNA's tag: cherry-pick the bindings commit, then redo the `lib/FNA3D` commit (`.gitmodules` url and branch, gitlink to the new FNA3D branch head). Push to modernuo/FNA.
3. In the client, set `.gitmodules` `branch =` for `external/FNA`, move the gitlink, run `tools\fna3d\build-fna3d.ps1`.
4. Verify before committing the DLL:
   - the Legacy batcher-state check at the house spot ([testing/ui-batcher.md](testing/ui-batcher.md)): about 2% near-black, not 25–67%;
   - Legacy and Gpu world shots, UI shots and renderer flips ([testing-playbook.md](testing-playbook.md));
   - `poc/Uo.Render.App --external-test --driver vulkan`.
