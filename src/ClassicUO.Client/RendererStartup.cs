// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.InteropServices;
using ClassicUO.Configuration;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework.Graphics;
using SDL3;

namespace ClassicUO
{
    /// <summary>
    /// Runs RendererSelection at startup: <see cref="BeforeDevice"/> in Bootstrap before FNA creates its window and device,
    /// <see cref="AfterDevice"/> in GameController.LoadContent after the assets load, before the login scene.
    /// CUO_RENDERER_PROBE_FAIL=device|world makes the matching probe fail, to test the fallbacks on a capable machine.
    /// </summary>
    internal static unsafe class RendererStartup
    {
        private static readonly string _simulateFail = Environment.GetEnvironmentVariable("CUO_RENDERER_PROBE_FAIL")?.Trim().ToLowerInvariant();

        private static PreDeviceDecision _pre;
        private static string _source = "default";

        public static WorldRendererMode Mode => _pre.Mode;

        /// <summary>The Gpu world can run in this session: dev runtime switching (CUO_DEVTOOLS) refuses "gpu" otherwise.</summary>
        public static bool GpuAvailable { get; private set; }

        /// <summary>The decision line, e.g. "auto: Gpu (SDL_GPU vulkan, NVIDIA GeForce RTX 3090 Ti)".</summary>
        public static string Summary { get; private set; } = "not decided";

        public static void BeforeDevice()
        {
            Settings settings = Settings.GlobalSettings;
            WorldRendererMode mode = RendererSelection.ParseMode(settings.WorldRendererSetting, Settings.WorldRendererOverride, out _source, out string warning);

            if (warning != null)
            {
                Log.Warn($"[renderer] {warning}");
            }

            string userDriver = settings.ForceDriver switch
            {
                1 => "OpenGL",
                2 => "Vulkan",
                _ => Environment.GetEnvironmentVariable("FNA3D_FORCE_DRIVER")
            };

            if (string.IsNullOrWhiteSpace(userDriver))
            {
                userDriver = null;
            }

            _pre = RendererSelection.BeforeDevice(mode, userDriver, ProbeDevice, LegacyDriver, global::Uo.Render.Renderer.SpirvShadersEmbedded);

            if (userDriver != null)
            {
                Environment.SetEnvironmentVariable("FNA3D_FORCE_DRIVER", userDriver);
            }
            else if (_pre.Fna3dDriver != null)
            {
                Environment.SetEnvironmentVariable("FNA3D_FORCE_DRIVER", _pre.Fna3dDriver);
            }

            if (_pre.GpuCandidate && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SDL_GPU_DRIVER")))
            {
                // the probe checked Vulkan; FNA3D's SDL_GPU device must land on the same backend
                Environment.SetEnvironmentVariable("SDL_GPU_DRIVER", "vulkan");
            }

            Log.Trace($"[renderer] world_renderer {mode.ToString().ToLowerInvariant()} ({_source}); FNA3D driver {(userDriver ?? _pre.Fna3dDriver ?? "default order")}" +
                (_pre.Probed ? $"; SDL_GPU vulkan probe: {(_pre.GpuCandidate ? "ok" : _pre.Reason)}" : ""));
        }

        /// <summary>Decides Gpu or Legacy for the session and logs it. With Gpu, <paramref name="createWorld"/> has created the
        /// world pass (returns null, or the error). A forced gpu that can't run calls GpuDeviceLost.FailWorldSetup.</summary>
        public static void AfterDevice(GraphicsDevice device, IntPtr gpuDevice, Func<string> createWorld)
        {
            string sdlDriver = GpuInterop.GetDriverName(gpuDevice);
            string fna3d = GpuInterop.GetFna3dDriverName(device) ?? "unknown";
            // FNA's GraphicsAdapter.Description is the display's name, not the GPU's; on D3D11/OpenGL FNA3D logs its adapter itself
            string adapter = gpuDevice != IntPtr.Zero ? GpuInterop.GetAdapterName(gpuDevice) : null;

            PostDeviceDecision post = RendererSelection.AfterDevice(_pre, sdlDriver, () =>
                _simulateFail == "world" ? "simulated (CUO_RENDERER_PROBE_FAIL=world)" : createWorld());

            string deviceText = sdlDriver != null ? $"SDL_GPU {sdlDriver}, {adapter ?? "unknown adapter"}" : $"FNA3D {fna3d}";
            string mode = _pre.Mode.ToString().ToLowerInvariant();

            Settings.GlobalSettings.WorldRenderer = post.Outcome == RendererOutcome.Gpu ? "gpu" : "legacy";
            // dev runtime switching to gpu: a forced-legacy session on SDL_GPU creates the world on demand, as before
            GpuAvailable = post.Outcome == RendererOutcome.Gpu || (_pre.Mode == WorldRendererMode.Legacy && sdlDriver != null);

            switch (post.Outcome)
            {
                case RendererOutcome.Gpu:
                    Summary = $"{mode}: Gpu ({deviceText})";
                    Log.Info($"[renderer] {Summary}");

                    break;

                case RendererOutcome.Legacy:
                    Summary = _pre.Mode == WorldRendererMode.Legacy
                        ? $"legacy: Legacy (forced by {_source}; {deviceText})"
                        : $"auto: Legacy ({post.Reason}; {deviceText})";
                    Log.Info($"[renderer] {Summary}");

                    break;

                default:
                    Summary = $"gpu: unavailable ({post.Reason}; {deviceText})";
                    Log.Error($"[renderer] {Summary}");
                    GpuDeviceLost.FailWorldSetup(post.Reason);

                    break;
            }
        }

#if CUO_DEVTOOLS
        // CLEANUP(release): runtime renderer switching is dev tooling (parity shots, picking, the FPS test's Legacy column).
        /// <summary>Switches this session's world renderer ("gpu" or "legacy"). Refuses "gpu" when the session has no usable
        /// Gpu world (a Legacy fallback), which would otherwise exit through FailWorldSetup.</summary>
        public static bool TrySwitch(string renderer, out string message)
        {
            renderer = renderer?.Trim().ToLowerInvariant();

            if (renderer != "gpu" && renderer != "legacy")
            {
                message = $"unknown world renderer \"{renderer}\" (gpu or legacy)";

                return false;
            }

            if (renderer == "gpu" && !GpuAvailable)
            {
                message = $"the Gpu world renderer is not available in this session ({Summary})";
                Log.Warn($"[renderer] runtime switch to gpu refused: {message}");

                return false;
            }

            Settings.GlobalSettings.WorldRenderer = renderer;
            message = renderer;

            return true;
        }
#endif

        private static DeviceProbe ProbeDevice()
        {
            if (_simulateFail == "device")
            {
                return new DeviceProbe(false, "simulated (CUO_RENDERER_PROBE_FAIL=device)");
            }

            string backend = Environment.GetEnvironmentVariable("SDL_GPU_DRIVER");

            if (string.IsNullOrEmpty(backend))
            {
                backend = "vulkan";
            }

            // SDL_GPU's Vulkan check loads the Vulkan loader through the video subsystem. FNA's own SDL_Init comes later
            // (FNAPlatform), so init it for the probe only and quit again: FNA then initialises video as it always did.
            if (!SDL.SDL_InitSubSystem(SDL.SDL_InitFlags.SDL_INIT_VIDEO))
            {
                return new DeviceProbe(false, $"SDL video init failed: {SDL.SDL_GetError()}");
            }

            try
            {
                // MojoShader gives FNA's effects only SPIR-V on SDL_GPU, so FNA3D's SDL_GPU driver works only on Vulkan
                if (!SDL.SDL_GPUSupportsShaderFormats(SDL.SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV, backend))
                {
                    return new DeviceProbe(false, $"no SDL_GPU {backend} device for SPIR-V shaders ({ErrorOr("no Vulkan driver")})");
                }

                // the same properties FNA3D's SDL_GPU driver checks in PrepareWindowAttributes and creates its device with
                // (SDLGPU_INTERNAL_FillProperties): SPIR-V only, no optional features, hardware Vulkan only
                uint props = SDL.SDL_CreateProperties();

                try
                {
                    SDL.SDL_SetStringProperty(props, "SDL.gpu.device.create.name", backend);
                    SDL.SDL_SetBooleanProperty(props, "SDL.gpu.device.create.debugmode", false);
                    SDL.SDL_SetBooleanProperty(props, "SDL.gpu.device.create.preferlowpower", SDL.SDL_GetHintBoolean("FNA3D_PREFER_LOW_POWER", false));
                    SDL.SDL_SetBooleanProperty(props, "SDL.gpu.device.create.shaders.spirv", true);
                    SDL.SDL_SetBooleanProperty(props, "SDL.gpu.device.create.feature.clip_distance", false);
                    SDL.SDL_SetBooleanProperty(props, "SDL.gpu.device.create.feature.depth_clamping", false);
                    SDL.SDL_SetBooleanProperty(props, "SDL.gpu.device.create.feature.indirect_draw_first_instance", false);
                    SDL.SDL_SetBooleanProperty(props, "SDL.gpu.device.create.vulkan.requirehardwareacceleration", true);

                    if (!SDL.SDL_GPUSupportsProperties(props))
                    {
                        return new DeviceProbe(false, $"SDL_GPU {backend} does not support FNA3D's device properties ({ErrorOr("no suitable device")})");
                    }
                }
                finally
                {
                    SDL.SDL_DestroyProperties(props);
                }

                return new DeviceProbe(true, null);
            }
            finally
            {
                SDL.SDL_QuitSubSystem(SDL.SDL_InitFlags.SDL_INIT_VIDEO);
            }
        }

        private static string ErrorOr(string fallback)
        {
            string e = SDL.SDL_GetError();

            return string.IsNullOrEmpty(e) ? fallback : e;
        }

        /// <summary>Legacy's FNA3D driver when SDL_GPU can't run: D3D11 on Windows when a hardware feature level 10.0+ device
        /// exists (what FNA3D's D3D11 driver requires), else OpenGL.</summary>
        private static string LegacyDriver()
        {
            if (Environment.GetEnvironmentVariable("CUO_RENDERER_LEGACY_DRIVER") is { Length: > 0 } forced)
            {
                return forced;
            }

            return OperatingSystem.IsWindows() && D3D11Available() ? "D3D11" : "OpenGL";
        }

        private static bool D3D11Available()
        {
            if (!NativeLibrary.TryLoad("d3d11.dll", out IntPtr module))
            {
                return false;
            }

            try
            {
                if (!NativeLibrary.TryGetExport(module, "D3D11CreateDevice", out IntPtr fn))
                {
                    return false;
                }

                // D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_HARDWARE, software, flags, levels, count, D3D11_SDK_VERSION,
                // ppDevice, pFeatureLevel, ppContext); null out-pointers = only test that a device could be created
                var create = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, uint, int*, uint, uint, IntPtr, int*, IntPtr, int>)fn;
                int* levels = stackalloc int[] { 0xb100, 0xb000, 0xa100, 0xa000 };
                int level = 0;
                int hr = create(IntPtr.Zero, 1, IntPtr.Zero, 0, levels, 4, 7, IntPtr.Zero, &level, IntPtr.Zero);

                return hr >= 0;
            }
            finally
            {
                NativeLibrary.Free(module);
            }
        }
    }
}
