// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace ClassicUO
{
    internal static class CUOEnviroment
    {
        public static Thread GameThread;
        public static float DPIScaleFactor = 1.0f;
        public static bool NoSound;
        public static string[] Args;
        public static bool Debug;
        public static bool IsHighDPI;
        /// <summary>Frames drawn and presented per second (the measurement; diagnostics and the debug overlay).</summary>
        public static uint CurrentRefreshRate;
        /// <summary>The player-facing frames/s: the display rate while present on change runs (FpsDisplay.Shown).</summary>
        public static uint ShownRefreshRate;
        /// <summary>The last frame's build time in ms (Draw start to Present).</summary>
        public static double LastFrameBuildMs;
        public static uint CurrentLoopRate;
        public static bool SkipLoginScreen;
        public static bool NoServerPing;
        public static bool NoMessageBox;
        public static int EcsCheckFrames;
#if CUO_DEVTOOLS
        public static string EcsDriverScript;
#endif

        public static readonly bool IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT || Environment.OSVersion.Platform == PlatformID.Win32Windows || Environment.OSVersion.Platform == PlatformID.Win32S || Environment.OSVersion.Platform == PlatformID.WinCE;
        public static readonly bool IsUnix = !IsWindows;

        public static readonly string Version = Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "0.0.0.0";
        public static readonly string ExecutablePath =
#if NETFRAMEWORK
           AppContext.BaseDirectory; // Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
#else
            Environment.CurrentDirectory;
#endif
    }
}
