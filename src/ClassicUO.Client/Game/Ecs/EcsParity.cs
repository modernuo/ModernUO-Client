// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using Uo.Pack;
using Uo.Render;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    /// <summary>
    /// W3 parity harness (<c>CUO_ECS_PARITY=N</c>: every Nth Gpu frame). For each mobile the Gpu world renderer captures
    /// from MobileView.Draw, runs the ECS path for the same mobile — <see cref="MobileAnimationSystem"/> then
    /// <see cref="MobileSpriteExtract"/> with the same screen position, base depth and offset — and diffs the two instance
    /// lists field by field and in order. Mismatches are logged with the entity's ECS state; a summary with both costs is
    /// logged every 5 s. The renderer keeps using the captured instances.
    /// </summary>
    internal static class EcsParity
    {
        private const int MAX_LOGGED = 40;

        public static readonly int Interval = ParseInterval();

        private static int _frame;
        private static long _captureTicks, _sampleTicks, _extractTicks;
        private static long _frames, _mobiles, _mismatched, _capInstances, _ecsInstances, _notMirrored;
        private static int _logged;
        private static uint _nextSummary;
        private static bool _frameCounted;

        public static bool Active { get; private set; }

        public static long Mismatched => _mismatched;

        private static int ParseInterval()
        {
            string v = Environment.GetEnvironmentVariable("CUO_ECS_PARITY");

            return int.TryParse(v, out int n) && n > 0 ? n : 0;
        }

        public static void BeginFrame()
        {
            Active = Interval > 0 && ++_frame % Interval == 0;
            _frameCounted = false;
        }

        /// <summary>
        /// Called by the capture right after <paramref name="mobile"/>'s view drew <c>buffer[start..end)</c>.
        /// <paramref name="captureTicks"/> is what the view draw cost.
        /// </summary>
        public static void OnCaptured(Mobile mobile, float depth, SpriteInstance[] buffer, int start, int end, PackSet pack, int offsetX, int offsetY, long captureTicks)
        {
            World world = mobile.World;
            WorldEcs ecs = world.Ecs;

            if (!_frameCounted)
            {
                _frameCounted = true;
                _frames++;
            }

            _captureTicks += captureTicks;

            long t0 = Stopwatch.GetTimestamp();
            bool mirrored = ecs.Animation.Update(world, mobile);
            long t1 = Stopwatch.GetTimestamp();

            MobileSpriteExtract extract = ecs.MobileExtract;
            extract.Begin(pack, offsetX, offsetY);

            if (mirrored && ecs.Mirror.TryGetEntity(mobile.Serial, out EcsEntity e))
            {
                extract.Emit(e, mobile.RealScreenPosition.X, mobile.RealScreenPosition.Y, depth);
            }
            else
            {
                _notMirrored++;
            }

            long t2 = Stopwatch.GetTimestamp();
            _sampleTicks += t1 - t0;
            _extractTicks += t2 - t1;

            ReadOnlySpan<SpriteInstance> cap = new ReadOnlySpan<SpriteInstance>(buffer, start, end - start);
            ReadOnlySpan<SpriteInstance> got = extract.Instances;
            _mobiles++;
            _capInstances += cap.Length;
            _ecsInstances += got.Length;

            if (mirrored && ecs.Mirror.TryGetEntity(mobile.Serial, out EcsEntity ce) && ce.HasComponent<AnimationState>())
            {
                CountCases(ce, got);
            }

            int first = FirstDifference(cap, got);

            if (first >= 0)
            {
                _mismatched++;
                // the first few mismatches of each mobile, so one stuck mobile doesn't hide the others
                bool newSerial = _mismatchSerials.Add(mobile.Serial);

                if (newSerial ? _logged++ < MAX_LOGGED : _logged++ < 8)
                {
                    Log.Warn(Describe(mobile, ecs, cap, got, first));
                }
            }

            if (Time.Ticks >= _nextSummary)
            {
                _nextSummary = Time.Ticks + 5000;
                Summary();
            }
        }

        public static void Summary()
        {
            if (_mobiles == 0)
            {
                return;
            }

            double f = Stopwatch.Frequency / 1_000_000.0;
            double frames = Math.Max(1, _frames);

            Log.Info
            (
                $"[PARITY] frames={_frames} mobiles={_mobiles} mismatched={_mismatched} not-mirrored={_notMirrored} instances capture={_capInstances} ecs={_ecsInstances} | " +
                $"per frame: capture {_captureTicks / f / frames:F2}us, ecs sample+select {_sampleTicks / f / frames:F2}us, extract {_extractTicks / f / frames:F2}us " +
                $"(per mobile {_captureTicks / f / _mobiles:F2} / {_sampleTicks / f / _mobiles:F2} / {_extractTicks / f / _mobiles:F2}us)\n  {Cases()}"
            );
        }

        private enum Case
        {
            Mounted,
            Sitting,
            CharSitting,
            GargoyleBody,
            GargoyleLayerGroups,
            Flying,
            Running,
            Walking,
            StepOffset,
            Hidden,
            Dead,
            NonHuman,
            Equipped,
            Cloak,
            Converted,
            OverrideHue,
            Translucent,
            ShadowInst,
            Spectral,
            Partial,
            Mirrored,
            ServerAnim,
            Count
        }

        private static readonly long[] _cases = new long[(int)Case.Count];
        private static readonly HashSet<uint> _serials = new HashSet<uint>();
        private static readonly HashSet<uint> _mismatchSerials = new HashSet<uint>();

        private static void CountCases(EcsEntity e, ReadOnlySpan<SpriteInstance> got)
        {
            ref readonly AnimationState s = ref e.GetComponent<AnimationState>();
            ref readonly MobileRenderState rs = ref e.GetComponent<MobileRenderState>();
            ref readonly EquipmentSlots eq = ref e.GetComponent<EquipmentSlots>();
            AnimStateFlags f = s.Flags;
            _serials.Add(e.GetComponent<Serial>().Value);

            void Hit(Case c, bool on)
            {
                if (on)
                {
                    _cases[(int)c]++;
                }
            }

            Hit(Case.Mounted, (f & AnimStateFlags.MountDrawn) != 0);
            Hit(Case.Sitting, (f & AnimStateFlags.Sitting) != 0);
            Hit(Case.CharSitting, (f & AnimStateFlags.CharSitting) != 0);
            Hit(Case.GargoyleBody, (f & AnimStateFlags.GargoyleBody) != 0);
            Hit(Case.Flying, (f & AnimStateFlags.Flying) != 0);
            Hit(Case.Running, (f & AnimStateFlags.Running) != 0 || (f & AnimStateFlags.StepRun) != 0);
            Hit(Case.Walking, (f & AnimStateFlags.HasSteps) != 0);
            Hit(Case.StepOffset, rs.OffsetX != 0 || rs.OffsetY != 0);
            Hit(Case.Hidden, (f & AnimStateFlags.Hidden) != 0);
            Hit(Case.Dead, (f & AnimStateFlags.Dead) != 0);
            Hit(Case.NonHuman, (f & AnimStateFlags.Human) == 0);
            Hit(Case.Equipped, eq.Count != 0);
            Hit(Case.OverrideHue, rs.OverrideHue != 0);
            Hit(Case.Translucent, rs.Alpha != 0xFF);
            Hit(Case.Mirrored, (f & AnimStateFlags.Mirror) != 0);
            Hit(Case.ServerAnim, (f & AnimStateFlags.FromServer) != 0);

            bool cloak = false, conv = false, layerGroups = false;

            for (int i = 0; i < eq.Count; i++)
            {
                cloak |= eq.Slots[i].Layer == (byte)ClassicUO.Game.Data.Layer.Cloak;
                conv |= (eq.Slots[i].Bits & EquipSlotBits.Converted) != 0;
                layerGroups |= eq.Slots[i].Group != 0xFF;
            }

            Hit(Case.Cloak, cloak);
            Hit(Case.Converted, conv);
            Hit(Case.GargoyleLayerGroups, layerGroups);

            bool shadow = false, spectral = false, partial = false;

            foreach (ref readonly SpriteInstance inst in got)
            {
                shadow |= inst.Mode == HueMode.Shadow;
                spectral |= inst.Mode == HueMode.Spectral;
                partial |= (inst.Flags & SpriteInstance.FlagPartial) != 0;
            }

            Hit(Case.ShadowInst, shadow);
            Hit(Case.Spectral, spectral);
            Hit(Case.Partial, partial);
        }

        private static string Cases()
        {
            var sb = new StringBuilder();
            sb.Append($"distinct mobiles {_serials.Count} (mismatching {_mismatchSerials.Count}); cases (mobile-frames):");

            for (int i = 0; i < (int)Case.Count; i++)
            {
                sb.Append($" {(Case)i}={_cases[i]}");
            }

            return sb.ToString();
        }

        private static int FirstDifference(ReadOnlySpan<SpriteInstance> a, ReadOnlySpan<SpriteInstance> b)
        {
            int n = Math.Min(a.Length, b.Length);

            for (int i = 0; i < n; i++)
            {
                if (!Same(in a[i], in b[i]))
                {
                    return i;
                }
            }

            return a.Length == b.Length ? -1 : n;
        }

        private static bool Same(in SpriteInstance a, in SpriteInstance b) =>
            a.X == b.X && a.Y == b.Y && a.Whf == b.Whf && a.Uv == b.Uv && a.PageHue == b.PageHue && a.Depth == b.Depth && a.Light == b.Light && a.Corners == b.Corners;

        private static string Describe(Mobile m, WorldEcs ecs, ReadOnlySpan<SpriteInstance> cap, ReadOnlySpan<SpriteInstance> got, int first)
        {
            var sb = new StringBuilder(512);
            sb.Append($"[PARITY] mismatch 0x{m.Serial:X8} g=0x{m.Graphic:X4} h=0x{m.Hue:X4} ({m.X},{m.Y},{m.Z}) dir={(byte)m.Direction} frame={m.AnimIndex} ");
            sb.Append($"capture={cap.Length} ecs={got.Length} first diff #{first}");

            if (ecs.Mirror.TryGetEntity(m.Serial, out EcsEntity e) && e.HasComponent<AnimationState>())
            {
                ref readonly AnimationState s = ref e.GetComponent<AnimationState>();
                ref readonly MobileRenderState rs = ref e.GetComponent<MobileRenderState>();
                ref readonly EquipmentSlots eq = ref e.GetComponent<EquipmentSlots>();
                sb.Append($"\n  ecs: body=0x{s.Body:X4} group={s.Group} dir={s.Dir} facing={s.Facing} frame={s.Frame} flags=[{s.Flags}] ");
                sb.Append($"mount=0x{s.MountBody:X4}/{s.MountGroup} off={s.MountOffsetY} sit=({s.SitX},{s.SitY}) hue=0x{rs.OverrideHue:X4} a={rs.Alpha} off=({rs.OffsetX},{rs.OffsetY}) slots={eq.Count}:");

                for (int i = 0; i < eq.Count; i++)
                {
                    ref readonly EquipSlot slot = ref eq.Slots[i];
                    sb.Append($" {slot.Layer:X2}=0x{slot.DrawBody:X4}/h{slot.Hue:X4}/g{slot.Group}");
                }
            }

            int from = Math.Max(0, first - 1), to = Math.Min(Math.Max(cap.Length, got.Length), first + 3);

            for (int i = from; i < to; i++)
            {
                sb.Append($"\n  #{i} cap {(i < cap.Length ? Fmt(in cap[i]) : "-")}\n     ecs {(i < got.Length ? Fmt(in got[i]) : "-")}");
            }

            return sb.ToString();
        }

        private static string Fmt(in SpriteInstance s) =>
            $"xy=({s.X},{s.Y}) wh={s.Width}x{s.Height} fl=0x{s.Flags:X2} page={s.Page} hue={s.PageHue >> 16} uv=({s.Uv & 0xFFFF},{s.Uv >> 16}) depth=0x{s.Depth:X8} mode={s.Mode} a={s.Alpha} pal={s.Corners}";
    }
}
