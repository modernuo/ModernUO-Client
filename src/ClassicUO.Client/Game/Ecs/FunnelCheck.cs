// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    /// <summary>T1: the class-A world-state properties that report their writes to <see cref="FunnelCheck"/>.</summary>
    /// <summary>
    /// T1: the world-state members of the world objects that still report their writes to <see cref="FunnelCheck"/>. T9 S11:
    /// every other class-A field is record-only (get-only, no backing field: ClassAMemberTests pins it); what is left are the
    /// funnel-written caches (X, Y, Z: the position cache the owner kept, S10; <see cref="Mobile"/>'s notoriety and pushed
    /// inputs; an item's corpse/boat animation fields and IsMulti), the serial, and the record writes the Mobile's helpers
    /// make for the operations (its step queue, offset, step time and animation timer).
    /// </summary>
    internal enum FunnelField : byte
    {
        X,
        Y,
        Z,
        Offset,
        Serial,
        AnimIndex,
        ExecuteAnimation,
        LastAnimationChangeTime,
        LastStepTime,
        IsMulti,
        IsRunning,
        NotorietyFlag,
        IsDead,
        LastStepSoundTime,
        StepSoundOffset,
        // mutation paths that are not property setters (Codex audit of slice 1, #2)
        IsSAPoisoned,
        Steps,
        AnimationState,
        Count
    }

    /// <summary>
    /// T1 (<c>CUO_FUNNEL_CHECK=1</c>, CUO_DEVTOOLS builds only): every class-A property setter calls <see cref="Write"/>. A
    /// write to an <see cref="Entity"/> that its <see cref="World"/> holds, made outside a <see cref="WorldWriter"/> scope, is a
    /// direct write (a write to the subject of the operation that runs is not; a write to any other object during it is): it
    /// is counted per field, and the first writes of each (field, caller) pair are logged as
    /// <c>[funnel] direct write &lt;field&gt; at &lt;caller&gt;</c>. Land, statics, multis, effects and detached objects (the
    /// character-creation mobiles, the house placement preview) are not world objects and are not counted.
    /// <para>
    /// The call is <see cref="ConditionalAttribute"/>: a <c>-p:CuoDevTools=false</c> build compiles it out of every setter. In
    /// a dev build with the check off it costs one static load and a branch per write.
    /// </para>
    /// <para>
    /// Besides the setters: <c>Mobile.SetSAPoison</c>, the step queue writes (<c>AddStep</c>, <c>RemoveFirstStep</c>,
    /// <c>ReplaceStep</c>, <c>ClearStepQueue</c>), <c>SetOffset</c>, <c>SetLastStepTime</c> and <c>WriteTimer</c> (every
    /// animation timer write). The counters and the site table are not synchronised: they assume the single world-mutation
    /// thread (the game loop), like the World they observe.
    /// </para>
    /// </summary>
    internal static class FunnelCheck
    {
        [Conditional("CUO_DEVTOOLS")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(GameObject obj, FunnelField field)
        {
#if CUO_DEVTOOLS
            if (Enabled)
            {
                Observe(obj, field, 0, false);
            }
#endif
        }

        /// <summary>
        /// The Serial setter: an object World holds under the old serial or (a re-key: the dictionary moved first, as the
        /// death re-key in 0xAF does) under the new one is world state.
        /// </summary>
        [Conditional("CUO_DEVTOOLS")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteSerial(Entity e, uint newSerial)
        {
#if CUO_DEVTOOLS
            if (Enabled)
            {
                Observe(e, FunnelField.Serial, newSerial, true);
            }
#endif
        }

        /// <summary>
        /// A known dev-only direct write (DevBench's client-side teleport): writes made while a scope is open are counted in
        /// <see cref="DevExempt"/> and not reported. Gates used to recognise these writes by the caller name in the warning,
        /// which NativeAOT cannot resolve (its frames read <c>Bootstrap.Main</c>). Scopes nest; the scope is a struct, so
        /// opening one does not allocate. Without CUO_DEVTOOLS both calls are empty.
        /// </summary>
        public static DevScope DevWrite()
        {
#if CUO_DEVTOOLS
            _devDepth++;
#endif
            return default;
        }

        public readonly struct DevScope : IDisposable
        {
            public void Dispose()
            {
#if CUO_DEVTOOLS
                _devDepth--;
#endif
            }
        }

#if CUO_DEVTOOLS
        public const bool Available = true;

        private static int _devDepth;

        /// <summary>Direct writes made inside a <see cref="DevWrite"/> scope; not in <see cref="Total"/>.</summary>
        public static long DevExempt { get; private set; }

        /// <summary>Set from <c>CUO_FUNNEL_CHECK=1</c> at startup; tests turn it on and off.</summary>
        public static bool Enabled = Environment.GetEnvironmentVariable("CUO_FUNNEL_CHECK") == "1";

        // the first writes of every field resolve their caller (a stack walk), then one in SAMPLE_EVERY: enough to name every
        // writer that runs often, without walking the stack on every per-frame write (Offset, the animation timer)
        private const int RESOLVE_FIRST = 64;
        private const int SAMPLE_EVERY = 1024;
        private const int MAX_LOGGED_SITES = 400;

        private static readonly long[] _counts = new long[(int)FunnelField.Count];
        private static readonly Dictionary<(FunnelField, string), long> _sites = new Dictionary<(FunnelField, string), long>();
        private static uint _summaryAt;

        public static long Total { get; private set; }

        public static long Count(FunnelField field) => _counts[(int)field];

        public static void Reset()
        {
            Array.Clear(_counts, 0, _counts.Length);
            _sites.Clear();
            Total = 0;
            DevExempt = 0;
        }

        /// <summary>The (field, caller) pairs seen so far with their sampled counts.</summary>
        public static IReadOnlyDictionary<(FunnelField, string), long> Sites => _sites;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Observe(GameObject obj, FunnelField field, uint otherSerial, bool hasOtherSerial)
        {
            if (obj is not Entity e)
            {
                return;
            }

            World world = e.World;

            if (world == null || world.Writer == null || world.Writer.Owns(e))
            {
                return;
            }

            if (!IsHeld(world, e, e.Serial) && !(hasOtherSerial && IsHeld(world, e, otherSerial)))
            {
                return;
            }

            if (_devDepth > 0)
            {
                DevExempt++;

                return;
            }

            Total++;
            long n = ++_counts[(int)field];

            if (n > RESOLVE_FIRST && n % SAMPLE_EVERY != 0)
            {
                return;
            }

            string caller = Caller();
            var key = (field, caller);
            _sites.TryGetValue(key, out long seen);
            _sites[key] = seen + 1;

            if (seen == 0 && _sites.Count <= MAX_LOGGED_SITES)
            {
                Log.Warn($"[funnel] direct write {field} at {caller}");
            }
        }

        // only the instance World holds for its serial is world state (WorldEcs.Sync ignores every other one)
        private static bool IsHeld(World world, Entity e, uint serial) => world.Holds(e, serial);

        // the first frame that is not a property setter, this class or the funnel itself
        private static string Caller()
        {
            var trace = new StackTrace(2, true);
            string writer = null, via = null;
            bool writerIsPlumbing = false;

            for (int i = 0; i < trace.FrameCount; i++)
            {
                StackFrame f = trace.GetFrame(i);
                System.Reflection.MethodBase m = f?.GetMethod();

                if (m == null || m.Name.StartsWith("set_", StringComparison.Ordinal) || m.DeclaringType == typeof(FunnelCheck))
                {
                    continue;
                }

                string file = f.GetFileName();
                string where = file == null ? "" : $" ({System.IO.Path.GetFileName(file)}:{f.GetFileLineNumber()})";
                string frame = $"{m.DeclaringType?.Name}.{m.Name}{where}";

                // the writing method, then (T1 part 7) the method that called it, so a shared helper's writes name their caller
                if (writer == null)
                {
                    writer = frame;
                    // the JIT inlines Destroy into World.RemoveItem once that method is tiered up, so the writing frame can
                    // itself be removal plumbing: there is no separate "via" frame, the next non-plumbing frame is the caller
                    writerIsPlumbing = IsRemovalPlumbing(m);

                    continue;
                }

                if (writerIsPlumbing)
                {
                    if (IsRemovalPlumbing(m))
                    {
                        continue;
                    }

                    return $"{writer} from {frame}";
                }

                if (via == null)
                {
                    via = frame;

                    if (!IsRemovalPlumbing(m))
                    {
                        return $"{writer} via {via}";
                    }

                    continue;
                }

                // (T1 part 9) a removal's Destroy writes: past the removal plumbing (Destroy, World.RemoveItem/RemoveMobile and
                // their helpers), the method that asked for the removal
                if (IsRemovalPlumbing(m))
                {
                    continue;
                }

                return $"{writer} via {via} from {frame}";
            }

            return via != null ? $"{writer} via {via}" : writer ?? "?";
        }

        private static bool IsRemovalPlumbing(System.Reflection.MethodBase m)
        {
            switch (m.Name)
            {
                case "Destroy":
                    return typeof(GameObject).IsAssignableFrom(m.DeclaringType);
                case "RemoveItem":
                case "RemoveMobile":
                case "RemoveContentsAndDestroy":
                case "RemoveDetachedItem":
                case "RemoveItemFromContainer":
                    return m.DeclaringType == typeof(World);
                default:
                    return false;
            }
        }

        /// <summary>Logs the totals by field every 5 s while the check is on (called from WorldEcs.Update).</summary>
        public static void LogSummary(bool force = false)
        {
            if (!Enabled || (!force && Time.Ticks < _summaryAt))
            {
                return;
            }

            _summaryAt = Time.Ticks + 5000;
            var sb = new StringBuilder();
            sb.Append($"[funnel] direct writes {Total}");
            string sep = ": ";

            for (int i = 0; i < _counts.Length; i++)
            {
                if (_counts[i] != 0)
                {
                    sb.Append(sep).Append((FunnelField)i).Append(' ').Append(_counts[i]);
                    sep = ", ";
                }
            }

            sb.Append($" | (field, caller) sites {_sites.Count}");

            if (DevExempt != 0)
            {
                sb.Append($" | dev-exempt {DevExempt}");
            }

            Log.Info(sb.ToString());
        }
#else
        public const bool Available = false;

        public static bool Enabled
        {
            get => false;
            set { }
        }

        public static long Total => 0;

        public static long DevExempt => 0;

        public static long Count(FunnelField field) => 0;

        public static IReadOnlyDictionary<(FunnelField, string), long> Sites { get; } = new Dictionary<(FunnelField, string), long>();

        public static void Reset() { }

        public static void LogSummary(bool force = false) { }
#endif
    }
}
