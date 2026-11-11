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
    /// T4 (ecs-transition-plan.md, "thin handles; the ECS owns scalar state") and T9 (cold-caches): the getters of the cold
    /// world-state fields read the entity's mirror record through its cached handle (<c>Entity.TryScalarEntity</c>); there is
    /// no cache behind them. An entity without a held record (detached, destroyed, a stale instance) reads the defaults.
    /// <para>
    /// Record-only: <c>Name</c>, <c>Hits</c>, <c>HitsMax</c> (Entity), <c>Stamina</c>, <c>StaminaMax</c>, <c>Mana</c>,
    /// <c>ManaMax</c> (Mobile), <c>Price</c> (Item); T9 S5 added <c>Amount</c>, <c>Container</c>, <c>Layer</c> and the traits
    /// (Item <c>LightID</c>, <c>IsDamageable</c>, <c>UsedLayer</c>; Mobile <c>Race</c>, <c>SpeedMode</c>, <c>IsFemale</c>,
    /// <c>IsRenamable</c>, the player's <c>InWarMode</c>), and T9 S6 <c>Flags</c> and <c>Direction</c>, read through
    /// <c>Entity.TryRecord</c> (previews too). Kept as write-through caches because per-frame readers read them per object
    /// (the census's hot readers, measured with <see cref="ScalarReads"/>): <c>NotorietyFlag</c>, like X, Y, Z, Graphic and
    /// Hue.
    /// </para>
    /// </summary>
    internal static class EcsScalars
    {
        // The record reads. An absent optional component is the GameObject default (null, 0), as the funnel adds each only
        // on its first non-default write. No allocation: the name is the string reference the funnel committed.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string Name(Friflo.Engine.ECS.Entity e) => EcsRead.TryGet(e, out EntityName n) ? n.Value : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Hits(Friflo.Engine.ECS.Entity e) => EcsRead.TryGet(e, out Vitals v) ? v.Hits : (ushort)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort HitsMax(Friflo.Engine.ECS.Entity e) => EcsRead.TryGet(e, out Vitals v) ? v.HitsMax : (ushort)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Stamina(Friflo.Engine.ECS.Entity e) => EcsRead.TryGet(e, out Vitals v) ? v.Stamina : (ushort)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort StaminaMax(Friflo.Engine.ECS.Entity e) => EcsRead.TryGet(e, out Vitals v) ? v.StaminaMax : (ushort)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Mana(Friflo.Engine.ECS.Entity e) => EcsRead.TryGet(e, out Vitals v) ? v.Mana : (ushort)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ManaMax(Friflo.Engine.ECS.Entity e) => EcsRead.TryGet(e, out Vitals v) ? v.ManaMax : (ushort)0;

        // T9 S6: the flags and the facing (the entity's Direction, with the running bit) every record has from its creation

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte FlagBits(Friflo.Engine.ECS.Entity e) => EcsRead.Get<Flags>(e).Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Facing(Friflo.Engine.ECS.Entity e) => EcsRead.Get<Facing>(e).Value;

        // T9 S5: the item fields every item record has from its creation (ItemData), and its parent (WorldMirror.SetParent:
        // ContainedIn for an item parent, EquippedOn for a wearer, neither on the ground; a Container of 0 is stored as on
        // the ground and reads back as 0xFFFFFFFF, which every reader treats the same: SerialHelper.IsValid is false for both)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Amount(Friflo.Engine.ECS.Entity e) => EcsRead.Get<ItemData>(e).Amount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Layer(Friflo.Engine.ECS.Entity e) => EcsRead.Get<ItemData>(e).Layer;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Container(Friflo.Engine.ECS.Entity e)
        {
            EcsRow row = EcsRead.Row(e);

            if (row.TryGet(out ContainedIn inside))
            {
                return inside.Parent;
            }

            return row.TryGet(out EquippedOn worn) ? worn.Parent : WorldMirror.NO_CONTAINER;
        }

        // the traits (ecs_small_fields): absent until the first non-default write

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte LightId(Friflo.Engine.ECS.Entity e) => EcsRead.TryGet(e, out ItemTraits t) ? t.LightId : (byte)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ItemBit(Friflo.Engine.ECS.Entity e, ItemTraitBits bit) => EcsRead.TryGet(e, out ItemTraits t) && (t.Bits & bit) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Race(Friflo.Engine.ECS.Entity e) => EcsRead.TryGet(e, out MobileTraits t) ? t.Race : (byte)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte SpeedMode(Friflo.Engine.ECS.Entity e) => EcsRead.TryGet(e, out MobileTraits t) ? t.SpeedMode : (byte)0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool MobileBit(Friflo.Engine.ECS.Entity e, MobileTraitBits bit) => EcsRead.TryGet(e, out MobileTraits t) && (t.Bits & bit) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Price(Friflo.Engine.ECS.Entity e) => EcsRead.TryGet(e, out ItemPrice p) ? p.Value : 0;
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
        NotorietyFlag,
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
                var sb = new StringBuilder($"[scalars] reads/frame over {_frames} frames (avg/max):");

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
