// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network
{
    /// <summary>
    /// net_ring (net-rework step 3): received bytes are decoded straight into a double-mapped ring per NetClient and framed,
    /// read and released in place (no CircularBuffer staging, no per-packet copy), and sends go through a bounded ring
    /// that ends the connection ("send buffer exhausted") instead of growing. Read once at startup (CUO_NET_RING=1|0
    /// overrides it and CUO_NET_RING_CHECK=1 frames every slice with the old CircularBuffer path too and logs differences,
    /// in developer builds). A platform that cannot map the ring falls back to the CircularBuffer path. Default off.
    /// </summary>
    internal static class NetRings
    {
        /// <summary>Receive ring: one partial packet (under 64 KB) plus a decoded slice (at most 16 KB) always fit.</summary>
        public const int ReceiveSize = 256 * 1024;

        /// <summary>Send ring: when a packet does not fit, the ring is flushed in place; still full after the socket's 5 s budget ends the connection.</summary>
        public const int SendSize = 128 * 1024;

#if CUO_DEVTOOLS
        private static readonly bool s_on = Environment.GetEnvironmentVariable("CUO_NET_RING") switch
        {
            "1" => true,
            "0" => false,
            _ => Settings.GlobalSettings?.NetRing ?? false
        };

        private static readonly bool s_check = Environment.GetEnvironmentVariable("CUO_NET_RING_CHECK") == "1";
#else
        private static readonly bool s_on = Settings.GlobalSettings?.NetRing ?? false;
#endif

        /// <summary>Tests: the switch (null: the setting / CUO_NET_RING).</summary>
        internal static bool? OnOverride;

        /// <summary>Tests: ring sizes (null: <see cref="ReceiveSize"/>, <see cref="SendSize"/>).</summary>
        internal static int? ReceiveSizeOverride, SendSizeOverride;

        public static bool On => OnOverride ?? s_on;

        private static bool _fallbackLogged;

        /// <summary>Maps a ring, or logs once why it could not and returns null (the caller keeps the CircularBuffer path).</summary>
        public static NetRing Create(int size, string what)
        {
            if (NetRing.TryCreate(size, out var ring, out var error))
            {
                return ring;
            }

            if (!_fallbackLogged)
            {
                _fallbackLogged = true;
                Log.Warn($"[net-ring] cannot map the {what} ring ({size} bytes): {error}; using the CircularBuffer path");
            }

            return null;
        }

#if CUO_DEVTOOLS
        /// <summary>Tests: the checker (null: CUO_NET_RING_CHECK).</summary>
        internal static bool? CheckOverride;

        /// <summary>The checker runs only with the ring on: the old path is its shadow.</summary>
        public static bool CheckOn => On && (CheckOverride ?? s_check);

        /// <summary>Checker counts over the session.</summary>
        public static long CheckedPackets, CheckedSlices, CheckedSends, Mismatches;

        /// <summary>Tests: sees each mismatch line.</summary>
        internal static Action<string> MismatchObserver;

        private static long _logAt;
        private static bool _announced, _exitHooked;

        public static void Mismatch(string what)
        {
            Mismatches++;
            string line = $"[net-ring-check] mismatch: {what}";
            MismatchObserver?.Invoke(line);

            if (Mismatches <= 200)
            {
                Log.Warn(line);
            }
        }

        public static void LogSummary(bool force = false)
        {
            if (!_announced)
            {
                _announced = true;
                Log.Info($"[net-ring] net_ring={(On ? "on" : "off")} check={(CheckOn ? "on" : "off")}");
            }

            if (!CheckOn || (!force && Time.Ticks < _logAt))
            {
                return;
            }

            if (!_exitHooked)
            {
                _exitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => LogSummary(true);
            }

            _logAt = Time.Ticks + 5000;
            Log.Info($"[net-ring-check] {CheckedPackets} packets, {CheckedSlices} slices, {CheckedSends} sends; mismatches {Mismatches} (net_ring={(On ? "on" : "off")})");
        }

        internal static void ResetCheckForTests()
        {
            CheckedPackets = CheckedSlices = CheckedSends = Mismatches = 0;
        }
#endif
    }
}
