// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO
{
    /// <summary>The world_renderer setting (settings.json, -worldrenderer).</summary>
    internal enum WorldRendererMode
    {
        /// <summary>Gpu when the machine supports it, else Legacy, decided once at startup.</summary>
        Auto,
        /// <summary>Forced Gpu: no Gpu world = error and exit (0xDEAF).</summary>
        Gpu,
        /// <summary>Forced Legacy (on whatever device FNA3D picks; SDL_GPU on a capable machine).</summary>
        Legacy
    }

    internal enum RendererOutcome
    {
        Gpu,
        Legacy,
        /// <summary>Forced gpu that can't run: GpuDeviceLost.FailWorldSetup (error, exit 0xDEAF).</summary>
        Fail
    }

    /// <summary>The pre-device probe's answer: can FNA3D's SDL_GPU driver run on Vulkan here?</summary>
    internal readonly record struct DeviceProbe(bool Ok, string Reason);

    /// <summary>Before FNA creates its device. <see cref="Fna3dDriver"/> non-null = set FNA3D_FORCE_DRIVER to it.</summary>
    internal readonly record struct PreDeviceDecision(WorldRendererMode Mode, bool GpuCandidate, string Fna3dDriver, string Reason, bool Probed);

    internal readonly record struct PostDeviceDecision(RendererOutcome Outcome, string Reason);

    /// <summary>
    /// The startup world-renderer choice, as pure functions (the unit tests cover them); RendererStartup runs the probes
    /// and applies the result. Decided once per session: device loss and a later world-pass failure (a facet change)
    /// still exit (GpuDeviceLost), they never switch to Legacy.
    /// </summary>
    internal static class RendererSelection
    {
        /// <summary>The effective mode: -worldrenderer (this run) over settings.json's world_renderer. Unknown or empty = auto.</summary>
        public static WorldRendererMode ParseMode(string setting, string commandLineOverride, out string source, out string warning)
        {
            warning = null;
            string value = setting;
            source = "settings.json";

            if (!string.IsNullOrWhiteSpace(commandLineOverride))
            {
                value = commandLineOverride;
                source = "-worldrenderer";
            }
            else if (string.IsNullOrWhiteSpace(setting))
            {
                source = "default";

                return WorldRendererMode.Auto;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "auto": return WorldRendererMode.Auto;
                case "gpu": return WorldRendererMode.Gpu;
                case "legacy": return WorldRendererMode.Legacy;
                default:
                    warning = $"unknown world_renderer \"{value}\" ({source}); using auto";
                    source = "default";

                    return WorldRendererMode.Auto;
            }
        }

        /// <summary>FNA3D_FORCE_DRIVER values that select FNA3D's SDL_GPU driver ("Vulkan", "D3D12" and "Metal" are aliases for it).</summary>
        public static bool IsSdlGpuDriver(string fna3dDriver) =>
            fna3dDriver != null && (fna3dDriver.Equals("SDLGPU", StringComparison.OrdinalIgnoreCase) || fna3dDriver.Equals("Vulkan", StringComparison.OrdinalIgnoreCase) ||
                fna3dDriver.Equals("D3D12", StringComparison.OrdinalIgnoreCase) || fna3dDriver.Equals("Metal", StringComparison.OrdinalIgnoreCase));

        /// <param name="userDriver">The FNA3D driver the user forced (force_driver, or FNA3D_FORCE_DRIVER set outside), or null.</param>
        /// <param name="probe">Runs the SDL_GPU/Vulkan probe; only called when the answer matters.</param>
        /// <param name="legacyDriver">The FNA3D driver for a Legacy fallback (D3D11 on Windows if it can run, else OpenGL).</param>
        public static PreDeviceDecision BeforeDevice(WorldRendererMode mode, string userDriver, Func<DeviceProbe> probe, Func<string> legacyDriver)
        {
            if (mode == WorldRendererMode.Legacy)
            {
                // forced Legacy keeps today's device (FNA3D's own order: SDL_GPU first), so Legacy-vs-Gpu parity compares the
                // same device; force_driver / FNA3D_FORCE_DRIVER still pick another one
                return new PreDeviceDecision(mode, false, null, "forced", false);
            }

            if (!string.IsNullOrEmpty(userDriver) && !IsSdlGpuDriver(userDriver))
            {
                return new PreDeviceDecision(mode, false, null, $"FNA3D driver forced to {userDriver}; the Gpu world renderer needs FNA3D's SDL_GPU driver", false);
            }

            DeviceProbe result = probe();

            if (result.Ok)
            {
                return new PreDeviceDecision(mode, true, null, null, true);
            }

            if (mode == WorldRendererMode.Gpu || !string.IsNullOrEmpty(userDriver))
            {
                // forced gpu: fails after the device (FailWorldSetup), as before. A user-forced SDL_GPU driver is kept even
                // though it will likely fail: the user chose it
                return new PreDeviceDecision(mode, false, null, result.Reason, true);
            }

            // auto: Legacy on D3D11/OpenGL, forced, so FNA3D doesn't try SDL_GPU again (and a simulated failure really
            // lands on another driver)
            return new PreDeviceDecision(mode, false, legacyDriver(), result.Reason, true);
        }

        /// <param name="sdlGpuDriver">The SDL_GPU backend FNA3D's device runs on ("vulkan"), or null when FNA3D isn't on SDL_GPU.</param>
        /// <param name="worldProbe">Creates the Gpu world pass (or fails): null on success, else the error. Only called when it matters.</param>
        public static PostDeviceDecision AfterDevice(PreDeviceDecision pre, string sdlGpuDriver, Func<string> worldProbe)
        {
            if (pre.Mode == WorldRendererMode.Legacy)
            {
                return new PostDeviceDecision(RendererOutcome.Legacy, "forced");
            }

            RendererOutcome unavailable = pre.Mode == WorldRendererMode.Gpu ? RendererOutcome.Fail : RendererOutcome.Legacy;

            if (!pre.GpuCandidate)
            {
                return new PostDeviceDecision(unavailable, pre.Reason);
            }

            if (sdlGpuDriver == null)
            {
                return new PostDeviceDecision(unavailable, "FNA3D did not create an SDL_GPU device (it fell back to another driver)");
            }

            if (!sdlGpuDriver.Equals("vulkan", StringComparison.OrdinalIgnoreCase))
            {
                // MojoShader gives FNA's effects only SPIR-V, so SDL_GPU here should only ever be Vulkan
                return new PostDeviceDecision(unavailable, $"SDL_GPU is on {sdlGpuDriver}, not vulkan");
            }

            string error = worldProbe();

            return error == null ? new PostDeviceDecision(RendererOutcome.Gpu, null) : new PostDeviceDecision(unavailable, error);
        }
    }
}
