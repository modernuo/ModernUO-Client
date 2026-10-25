// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ClassicUO.Utility.Logging;
using SDL3;

namespace ClassicUO
{
    // A driver reset (TDR) loses the GPU device. SDL_GPU can't recover from that, and its Vulkan backend leaks every
    // failed submit (the command buffer and everything it references), so a client that keeps drawing runs away on
    // memory within seconds. FNA3D's SDL_GPU driver ignores failed submits and swapchain acquires, so the only signal
    // is SDL's error text. On the first sign of a lost device we stop drawing, save what a normal exit saves and exit
    // with ExitCode. Recreating the device instead is possible in principle, but every texture would have to be
    // reloaded.
    internal static unsafe partial class GpuDeviceLost
    {
        /// <summary>Process exit code after a lost GPU device (tools/fps-test/run-fps-test.ps1 recognises it).</summary>
        public const int ExitCode = 0xDEAD;

        /// <summary>Process exit code when the GPU world renderer can't be set up (no SDL_GPU device, the pack can't be
        /// opened, a facet's world pass can't be created). There is no Legacy fallback for players.</summary>
        public const int WorldSetupExitCode = 0xDEAF;

        private static string _worldSetupError;

        /// <summary>The GPU world renderer can't be set up: logs why; at the end of the frame ClassicUO saves, tells the
        /// user and exits with <see cref="WorldSetupExitCode"/>, as for a lost device.</summary>
        public static void FailWorldSetup(string error)
        {
            if (_worldSetupError != null)
            {
                return;
            }

            _worldSetupError = error;
            Log.Error($"GPU world renderer setup failed: {error}. ClassicUO exits with code 0x{WorldSetupExitCode:X} (the Legacy world renderer is a developer mode: -worldrenderer legacy).");
        }

        public static bool WorldSetupFailed => _worldSetupError != null;

        // CUO_DEVICE_LOST_NOEXIT=1: keep running after the loss and log frames and private bytes (to study SDL's leak)
        private static readonly bool _noExit = Environment.GetEnvironmentVariable("CUO_DEVICE_LOST_NOEXIT") == "1";
        private static bool _shuttingDown;
        private static long _lostFrames, _lostStart, _nextDiag;

        // CUO_SIMULATE_DEVICE_LOST=<seconds>: that long after the first present, put Vulkan's device-lost error text into
        // SDL_GetError, as a failed submit does. Tests this handler without a real driver reset (which needs admin).
        private static readonly double _simulateAfter = double.TryParse(Environment.GetEnvironmentVariable("CUO_SIMULATE_DEVICE_LOST"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 0;
        private static long _simulateAt;

        public static bool IsLost { get; private set; }

        [LibraryImport("SDL3")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial byte* SDL_GetError();

        /// <summary>SDL's error texts for a lost device: Vulkan VK_ERROR_DEVICE_LOST, D3D12 DXGI_ERROR_DEVICE_REMOVED/HUNG/RESET.</summary>
        public static bool IsDeviceLostText(ReadOnlySpan<byte> text) =>
            text.IndexOf("DEVICE_LOST"u8) >= 0 ||
            text.IndexOf("DEVICE_REMOVED"u8) >= 0 ||
            text.IndexOf("DEVICE_HUNG"u8) >= 0 ||
            text.IndexOf("DEVICE_RESET"u8) >= 0;

        /// <summary>Call after every present: FNA3D drops a failed submit silently, but SDL leaves the reason in SDL_GetError.
        /// Exits the process if the device is lost.</summary>
        public static void AfterPresent()
        {
            if (_worldSetupError != null && !IsLost)
            {
                Shutdown($"The GPU world renderer could not start ({_worldSetupError}). ClassicUO will close.", WorldSetupExitCode,
                    "the GPU world renderer setup failed");

                return;
            }

            if (_simulateAfter > 0)
            {
                SimulateLoss();
            }

            if (!IsLost)
            {
                byte* error = SDL_GetError();

                if (error == null || *error == 0)
                {
                    return;
                }

                var text = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(error);

                if (!IsDeviceLostText(text))
                {
                    return;
                }

                Report("present", Encoding.UTF8.GetString(text));
            }

            if (_noExit)
            {
                LogLeak();

                return;
            }

            Shutdown();
        }

        private static void SimulateLoss()
        {
            long now = Stopwatch.GetTimestamp();

            if (_simulateAt == 0)
            {
                _simulateAt = now + (long)(_simulateAfter * Stopwatch.Frequency);
            }
            else if (_simulateAt > 0 && now >= _simulateAt)
            {
                _simulateAt = -1;
                Log.Warn("CUO_SIMULATE_DEVICE_LOST: simulating a lost GPU device");
                SDL.SDL_SetError("vkQueueSubmit VK_ERROR_DEVICE_LOST (simulated)");
            }
        }

        private static void LogLeak()
        {
            long now = Stopwatch.GetTimestamp();

            if (_lostFrames++ == 0)
            {
                _lostStart = now;
            }

            if (now >= _nextDiag)
            {
                _nextDiag = now + Stopwatch.Frequency / 4;
                using var p = Process.GetCurrentProcess();
                Log.Warn($"[device-lost] {_lostFrames} frames in {(now - _lostStart) * 1000 / Stopwatch.Frequency} ms since the loss; private bytes {p.PrivateMemorySize64 >> 20} MiB");
            }
        }

        /// <summary>Marks the device lost and logs why; the process exits at the end of the frame.</summary>
        public static void Report(string where, string sdlError)
        {
            if (IsLost)
            {
                return;
            }

            IsLost = true;
            Log.Error($"GPU device lost ({where}): {sdlError}. The graphics driver reset; ClassicUO stops drawing and exits with code 0x{ExitCode:X}.");
        }

        /// <summary>FNALoggerEXT.LogError hook. FNA throws from the native callback after this returns, which NativeAOT turns
        /// into a fail-fast, so for a lost device this exits instead of returning.</summary>
        public static void OnFnaError(string message)
        {
            Log.Error($"FNA: {message}");

            if (_shuttingDown || _noExit)
            {
                return;
            }

            if (IsLost || IsDeviceLostText(Encoding.UTF8.GetBytes(message)))
            {
                Report("FNA3D", message);
                Shutdown();
            }
        }

        /// <summary>Saves what a normal exit saves (no GPU teardown: FNA3D and SDL can't tear down a lost device cleanly),
        /// tells the user unless the run is automated, and exits with <see cref="ExitCode"/>.</summary>
        public static void Shutdown()
        {
            Shutdown("The graphics driver reset (GPU device lost). ClassicUO will close.", ExitCode, "the GPU device was lost");
        }

        private static void Shutdown(string userMessage, int exitCode, string why)
        {
            if (_shuttingDown)
            {
                return;
            }

            _shuttingDown = true;

            try
            {
                Client.Game?.SaveOnDeviceLost();
            }
            catch (Exception ex)
            {
                Log.Error($"Saving after {why} failed: {ex}");
            }

            if (!CUOEnviroment.NoMessageBox && !DevBench.Enabled && !HasBenchEnvironment())
            {
                SDL.SDL_ShowSimpleMessageBox(
                    SDL.SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR,
                    "ClassicUO",
                    userMessage,
                    IntPtr.Zero
                );
            }

            Log.Error($"Exiting after {why} (exit code 0x{exitCode:X}).");
            Environment.Exit(exitCode);
        }

        private static bool HasBenchEnvironment()
        {
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                if (e.Key is string k && k.StartsWith("CUO_BENCH_", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(e.Value as string))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
