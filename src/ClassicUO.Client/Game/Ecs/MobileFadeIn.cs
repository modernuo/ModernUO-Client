// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Diagnostics;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    /// <summary>
    /// mobile_fade_in (mount-fix, owner 2026-11-23). On (the upstream behaviour, an option since 2026-11-23): a mobile the server creates starts
    /// at AlphaHue 0 and fades in by the scene's fade steps, so the horse a dismount leaves behind is invisible for its first
    /// frames. Off (the default): <see cref="WorldWriter.UpsertMobile"/> marks a created mobile (<see cref="GameObject.AlphaSnap"/>) and its
    /// first walk sets its alpha to the target at once (GameScene.ProcessAlpha: full, or 0 above the max Z). Later roof,
    /// max-Z and translucency changes fade as before; items are not affected. Both renderers read the same AlphaHue.
    /// CUO_MOBILE_FADE_CHECK=1 (<see cref="MobileFadeCheck"/>) counts, in both states, the created mobiles first drawn below
    /// their target alpha and the time from the first draw until they reach it.
    /// </summary>
    internal static class MobileFadeIn
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_MOBILE_FADE_IN") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_MOBILE_FADE_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.MobileFadeIn ?? false);

        /// <summary>WorldWriter.UpsertMobile: <paramref name="mobile"/> was just created.</summary>
        public static void Created(Mobile mobile)
        {
            mobile.AlphaSnap = !Enabled;
#if CUO_DEVTOOLS
            if (Check)
            {
                MobileFadeCheck.Created(mobile.Serial);
            }
#endif
        }
    }

#if CUO_DEVTOOLS
    /// <summary>
    /// CUO_MOBILE_FADE_CHECK=1 (CLEANUP(release)): mobile_fade_in's measure, both states. Each mobile WorldWriter creates is
    /// tracked until its walk draws it at its target alpha (GameScene's mobile branch reports each draw with the target its
    /// ProcessAlpha used). Every 5 s: mobiles created, drawn, first drawn below the target, and the ms from the first draw
    /// to the target (p50/p95/max), with the switch state. Main thread.
    /// </summary>
    internal static class MobileFadeCheck
    {
        private struct Track
        {
            public long Created, FirstDraw;
            public bool Below;
        }

        private static readonly Dictionary<uint, Track> _tracked = new Dictionary<uint, Track>(256);
        private static readonly MsSamples _rampMs = new MsSamples();
        private static long _created, _drawn, _firstBelow, _start;

        public static void Created(uint serial)
        {
            _created++;
            _tracked[serial] = new Track { Created = Stopwatch.GetTimestamp() };
        }

        /// <summary>The walk drew <paramref name="m"/> with <paramref name="target"/> as its alpha target.</summary>
        public static void Drawn(Mobile m, byte target)
        {
            if (!_tracked.TryGetValue(m.Serial, out Track t))
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();

            if (t.FirstDraw == 0)
            {
                _drawn++;
                t.FirstDraw = now;
                t.Below = m.AlphaHue != target;

                if (t.Below)
                {
                    _firstBelow++;
                }
            }

            if (m.AlphaHue == target)
            {
                if (t.Below)
                {
                    _rampMs.Add((uint)((now - t.FirstDraw) * 1000 / Stopwatch.Frequency), false);
                }

                _tracked.Remove(m.Serial);

                return;
            }

            _tracked[m.Serial] = t;
        }

        /// <summary>Draw start: the periodic summary; mobiles never drawn within 10 s are dropped.</summary>
        public static void Frame()
        {
            long now = Stopwatch.GetTimestamp();

            if (_start == 0)
            {
                _start = now;
            }

            if (now - _start < Stopwatch.Frequency * 5)
            {
                return;
            }

            _start = now;
            Log.Info($"[mobile-fade-check] switch={(MobileFadeIn.Enabled ? "on" : "off")} created {_created} drawn {_drawn} first_drawn_below_target {_firstBelow} ramp_ms {_rampMs.Summary()} tracked {_tracked.Count}");
            _created = _drawn = _firstBelow = 0;

            if (_tracked.Count > 128)
            {
                long old = now - Stopwatch.Frequency * 10;
                List<uint> drop = null;

                foreach (KeyValuePair<uint, Track> kv in _tracked)
                {
                    if (kv.Value.Created < old)
                    {
                        (drop ??= new List<uint>()).Add(kv.Key);
                    }
                }

                if (drop != null)
                {
                    foreach (uint s in drop)
                    {
                        _tracked.Remove(s);
                    }
                }
            }
        }

        internal static void ResetForTests()
        {
            _tracked.Clear();
            _created = _drawn = _firstBelow = 0;
        }

        internal static (long created, long drawn, long firstBelow) Counts => (_created, _drawn, _firstBelow);
    }
#endif
}
