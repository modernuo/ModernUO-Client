// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using ClassicUO.Ecs;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    /// <summary>
    /// T4 (ecs-transition-plan.md, "thin handles; the ECS owns scalar state"): with <c>ecs_owns_scalars</c> on, the getters
    /// of the cold world-state fields read the entity's mirror record through its cached handle
    /// (<c>Entity.TryScalarEntity</c>) and the cache the funnel writes becomes write-only (T9 deletes it). An entity without
    /// a held record (detached, destroyed, a stale instance) reads its cache, as with the switch off.
    /// <para>
    /// Converted: <c>Name</c>, <c>Hits</c>, <c>HitsMax</c> (Entity), <c>Stamina</c>, <c>StaminaMax</c>, <c>Mana</c>,
    /// <c>ManaMax</c> (Mobile), <c>Price</c> (Item). Kept as write-through caches because per-frame readers read them per
    /// object (the census's hot readers, measured with <see cref="ScalarReads"/>): <c>Flags</c>, <c>Direction</c>,
    /// <c>NotorietyFlag</c>, <c>Amount</c>, <c>Container</c>, <c>Layer</c>, like X, Y, Z, Graphic and Hue.
    /// </para>
    /// </summary>
    internal static class EcsScalars
    {
        /// <summary>The switch, read once (the first entity read after the settings load); default on.</summary>
        public static readonly bool Owns = Initial();

        private static bool Initial()
        {
#if CUO_DEVTOOLS
            // CLEANUP(release): the gate's override of the setting (CUO_ECS_OWNS_SCALARS=1|0)
            switch (Environment.GetEnvironmentVariable("CUO_ECS_OWNS_SCALARS"))
            {
                case "1": return true;
                case "0": return false;
            }
#endif
            return Configuration.Settings.GlobalSettings?.EcsOwnsScalars ?? true;
        }

        // The record reads. An absent optional component is the GameObject default (null, 0), as the funnel adds each only
        // on its first non-default write. No allocation: the name is the string reference the funnel committed.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string Name(Friflo.Engine.ECS.Entity e) => e.TryGetComponent(out EntityName n) ? n.Value : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Hits(Friflo.Engine.ECS.Entity e) => e.TryGetComponent(out Vitals v) ? v.Hits : (ushort)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort HitsMax(Friflo.Engine.ECS.Entity e) => e.TryGetComponent(out Vitals v) ? v.HitsMax : (ushort)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Stamina(Friflo.Engine.ECS.Entity e) => e.TryGetComponent(out Vitals v) ? v.Stamina : (ushort)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort StaminaMax(Friflo.Engine.ECS.Entity e) => e.TryGetComponent(out Vitals v) ? v.StaminaMax : (ushort)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Mana(Friflo.Engine.ECS.Entity e) => e.TryGetComponent(out Vitals v) ? v.Mana : (ushort)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ManaMax(Friflo.Engine.ECS.Entity e) => e.TryGetComponent(out Vitals v) ? v.ManaMax : (ushort)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Price(Friflo.Engine.ECS.Entity e) => e.TryGetComponent(out ItemPrice p) ? p.Value : 0;
    }

    internal enum ScalarField
    {
        Name,
        Hits,
        HitsMax,
        Stamina,
        StaminaMax,
        Mana,
        ManaMax,
        Price,
        Flags,
        Direction,
        NotorietyFlag,
        Amount,
        Container,
        Layer,
        Count
    }

    /// <summary>
    /// T4, dev builds: <c>CUO_SCALAR_READS=1</c> counts the getter reads of the T4 candidate fields and logs reads per frame
    /// every 5 s (<c>[scalars]</c>), the evidence for which fields stay cached (hot) and which read the ECS (cold).
    /// </summary>
    internal static class ScalarReads
    {
#if CUO_DEVTOOLS
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("CUO_SCALAR_READS") == "1";
        private static readonly long[] _counts = new long[(int)ScalarField.Count];
        private static readonly long[] _max = new long[(int)ScalarField.Count];
        private static readonly long[] _frameCounts = new long[(int)ScalarField.Count];
        private static long _frames, _lastLog;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Count(ScalarField f)
        {
            if (Enabled)
            {
                _frameCounts[(int)f]++;
            }
        }

        /// <summary>Called once per frame (WorldEcs.Update).</summary>
        public static void EndFrame()
        {
            if (!Enabled)
            {
                return;
            }

            _frames++;

            for (int i = 0; i < _frameCounts.Length; i++)
            {
                _counts[i] += _frameCounts[i];

                if (_frameCounts[i] > _max[i])
                {
                    _max[i] = _frameCounts[i];
                }

                _frameCounts[i] = 0;
            }

            long now = Stopwatch.GetTimestamp();

            if (_lastLog == 0)
            {
                _lastLog = now;
            }
            else if (now - _lastLog >= 5 * Stopwatch.Frequency)
            {
                _lastLog = now;
                var sb = new StringBuilder($"[scalars] reads/frame over {_frames} frames (avg/max), owns={EcsScalars.Owns}:");

                for (int i = 0; i < _counts.Length; i++)
                {
                    sb.Append($" {(ScalarField)i}={(double)_counts[i] / _frames:F1}/{_max[i]}");
                }

                Log.Info(sb.ToString());
            }
        }
#else
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Count(ScalarField f) { }

        public static void EndFrame() { }
#endif
    }
}
