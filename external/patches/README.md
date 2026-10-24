# Local patches to external dependencies

## fna3d-sdlgpu-interop.patch

Lets ClassicUO share FNA3D's SDL_GPU device (see `docs/modern-client/classicuo-integration-plan.md`).

- `FNA3D_GetSysRendererEXT` now fills `renderer.sdlgpu.device` (`SDL_GPUDevice*`) on the SDL_GPU driver. Upstream left it as a TODO.
- New export `void* FNA3D_SDLGPU_GetTextureEXT(FNA3D_Device*, FNA3D_Texture*)` returns the `SDL_GPUTexture*` behind a texture or render target. It returns NULL when the device isn't on the SDL_GPU driver.
- C# externs for both in FNA's `src/Graphics/FNA3D.cs`. ClassicUO calls the natives through its own `LibraryImport`s in `src/ClassicUO.Renderer/GpuInterop.cs`, because FNA's `FNA3D` class is internal.

**Base:** FNA `651c0ffd` (26.10) with FNA3D `62a41708` (26.10). The patch paths are relative to `external/FNA`.

**Don't send this upstream.** It was written with an AI assistant, and FNA3D's contribution policy (`lib/FNA3D/AGENTS.md`) forbids AI-generated code.

### Source of truth

This patch file is the source of truth. The same change also exists as local-only commits on branch `modern-client/sdlgpu-interop`:

- in `external/FNA/lib/FNA3D` (`f4c99c9`);
- in `external/FNA` (`e5a6fb4`, which also moves FNA's FNA3D gitlink).

The main repo's `external/FNA` gitlink stays at 26.10 (`651c0ffd`). Nobody else can clone a gitlink that points at a local-only commit. Push both commits to forks, and repoint `.gitmodules`, before moving any gitlink to them.

### Apply

From a clean 26.10 checkout:

```sh
cd external/FNA
git apply --exclude='lib/FNA3D/*' ../patches/fna3d-sdlgpu-interop.patch
cd lib/FNA3D
git apply -p3 --include='include/*' --include='src/*' ../../../patches/fna3d-sdlgpu-interop.patch
```

### Build FNA3D.dll (win-x64)

`external/x64/FNA3D.dll` is built from the patched source as shown below. It links the CRT statically, so its only imports are `SDL3.dll` and `KERNEL32.dll`.

1. **SDL3 headers and import lib:** `SDL3-devel-3.4.18-VC.zip` from the libsdl-org/SDL GitHub releases (`gh release download release-3.4.18 -R libsdl-org/SDL -p SDL3-devel-3.4.18-VC.zip`). It is ABI-compatible with the SDL3.dll in `external/x64`, which is 3.4.19, a release-3.4 branch daily.
2. **Toolchain:** VS 2026 (18) with the C++ x64 tools. Find it with vswhere, then call `vcvars64.bat`. VS bundles CMake.
3. **Configure and build:**

```bat
set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%"
for /f "usebackq delims=" %%i in (`vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat"
cmake -S external\FNA\lib\FNA3D -B build-fna3d -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded ^
  -DSDL3_INCLUDE_DIRS=%SDL3DIR%\include -DSDL3_LIBRARIES=%SDL3DIR%\lib\x64\SDL3.lib
cmake --build build-fna3d --parallel 4
copy build-fna3d\FNA3D.dll external\x64\FNA3D.dll
```

4. **Check:** `dumpbin /exports FNA3D.dll` lists `FNA3D_SDLGPU_GetTextureEXT`. That makes 85 `FNA3D_*` exports, against 84 in the 26.10 daily it replaces.

The dailies' MinGW-built FNA3D.dll (which imports msvcrt.dll) was replaced. Linux and macOS natives aren't rebuilt yet: `external/lib64` and `external/osx` still have the unpatched 26.10 dailies, so the interop returns NULL or fails to resolve there.
