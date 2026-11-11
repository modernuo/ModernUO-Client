// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Renderer.Animations;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    /// <summary>The frame count of (body, action, stored dir) through the Legacy frame cache.</summary>
    internal interface ILegacyFrameCount
    {
        int FrameCount(ushort body, byte action, byte dir);
    }

    /// <summary><see cref="ILegacyFrameCount"/> over <see cref="Animations.GetAnimationFrames"/>: loads the direction (decode,
    /// RGBA8 atlas upload, picker masks) when it is not cached.</summary>
    internal readonly struct LegacyFrameCount : ILegacyFrameCount
    {
        private readonly Animations _animations;

        public LegacyFrameCount(Animations animations) => _animations = animations;

        public int FrameCount(ushort body, byte action, byte dir) => _animations.GetAnimationFrames(body, action, dir, out _, out _).Length;
    }

    /// <summary>
    /// anim_frame_count_tables: the animation advance's frame count (<see cref="IAnimationAdvanceSource.FrameCount"/>) from the
    /// pack's animation tables (<see cref="MobileSpriteExtract.TryGetFrames(AnimationsLoader, ushort, byte, byte, bool, bool, out uint, out int, out ushort)"/>,
    /// the same <c>AnimDirEntry.FrameCount</c>) instead of the Legacy frame cache, which decodes and uploads the direction into
    /// the RGBA8 anim atlas for every advancing mobile, on screen or not. Only while the Gpu world renderer draws: it never
    /// samples that atlas. The Legacy renderer keeps the cache read, whose load of every in-range mobile's direction also
    /// readies a mobile before it walks into view (without it, a body's first sight would blink in for a few frames there).
    /// Off: the Legacy cache. Read once at startup (CUO_ANIM_FRAME_COUNT_TABLES=1|0 overrides it in developer builds;
    /// CUO_ANIM_FRAME_COUNT_CHECK=1 computes both at every call and logs differences).
    /// </summary>
    internal static class AnimFrameCount
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting and the checker
        private static readonly bool s_on = Environment.GetEnvironmentVariable("CUO_ANIM_FRAME_COUNT_TABLES") switch
        {
            "1" => true,
            "0" => false,
            _ => Configuration.Settings.GlobalSettings.AnimFrameCountTables
        };

        private static readonly bool s_check = Environment.GetEnvironmentVariable("CUO_ANIM_FRAME_COUNT_CHECK") == "1";
#else
        private static readonly bool s_on = Configuration.Settings.GlobalSettings.AnimFrameCountTables;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OnOverride;

        /// <summary>The setting.</summary>
        public static bool On => OnOverride ?? s_on;

        /// <summary>The Gpu world renderer drew the last world frame (GameScene.Draw sets it every frame).</summary>
        internal static bool GpuWorld;

        /// <summary>The advance reads the tables: the switch is on and the Gpu world renderer draws.</summary>
        public static bool UseTables => GpuWorld && On;

        /// <summary>The frame count of (body, action, stored dir), as <c>GetAnimationFrames(...).Length</c> resolves it.</summary>
        public static int Get<TLegacy>(AnimationsLoader tables, in TLegacy legacy, ushort body, byte action, byte dir)
            where TLegacy : struct, ILegacyFrameCount
        {
            if (!UseTables)
            {
#if CUO_DEVTOOLS
                if (CheckOn)
                {
                    int old = legacy.FrameCount(body, action, dir);
                    Compare(body, action, dir, FromTables(tables, body, action, dir), old);

                    return old;
                }
#endif
                return legacy.FrameCount(body, action, dir);
            }

            int count = FromTables(tables, body, action, dir);
#if CUO_DEVTOOLS
            if (CheckOn)
            {
                TableReads++;
                Compare(body, action, dir, count, legacy.FrameCount(body, action, dir));
            }
#endif
            return count;
        }

        /// <summary>The frame count from the tables only: no decode, no atlas upload.</summary>
        public static int FromTables(AnimationsLoader tables, ushort body, byte action, byte dir)
        {
            MobileSpriteExtract.TryGetFrames(tables, body, action, dir, false, false, out _, out int count, out _);

            return count;
        }

#if CUO_DEVTOOLS
        /// <summary>Tests: the checker (null: <c>CUO_ANIM_FRAME_COUNT_CHECK=1</c>).</summary>
        internal static bool? CheckOverride;

        public static bool CheckOn => CheckOverride ?? s_check;

        /// <summary>Checker counts over the session: counts compared, different, and the compared calls answered from the
        /// tables (the Gpu world drawing with the switch on).</summary>
        public static long Checks, Mismatches, TableReads;

        /// <summary>Tests: sees each mismatch line.</summary>
        internal static Action<string> MismatchObserver;

        private static long _logAt;

        private static void Compare(ushort body, byte action, byte dir, int tables, int legacy)
        {
            Checks++;

            if (tables == legacy)
            {
                return;
            }

            Mismatches++;
            string line = $"[anim-frame-count-check] body 0x{body:X4} action {action} dir {dir}: tables {tables}, legacy {legacy}";
            MismatchObserver?.Invoke(line);

            if (Mismatches <= 50)
            {
                Log.Warn(line);
            }
        }

        private static bool _announced;

        /// <summary>The switch state once per session (the pick job's proof that the switch was on), then the checker's 5 s
        /// summary (WorldEcs.Update).</summary>
        public static void LogSummary()
        {
            if (!_announced)
            {
                _announced = true;
                Log.Info($"[anim-frame-count] anim_frame_count_tables={(On ? "on" : "off")}");
            }

            if (!CheckOn || Time.Ticks < _logAt)
            {
                return;
            }

            _logAt = Time.Ticks + 5000;
            Log.Info($"[anim-frame-count-check] {Checks} counts compared, {Mismatches} mismatches (anim_frame_count_tables={(On ? "on" : "off")}; {TableReads} from the tables)");
        }
#endif
    }
}
