// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    /// <summary>
    /// CUO_LOOK_PREFETCH_CHECK=1 (CLEANUP(release)): asset_prefetch_on_packet's measure, both switch states. Every look a
    /// WorldWriter operation noted (LookPrefetch.Note) is tracked; at each draw start it is polled: a mobile by the verdict of
    /// its last draw since the note (MobileFrameCache.CheckDrawComplete: every layer the draw selected was loaded, nothing
    /// held or waited; a mobile not drawn since the note is not polled and, after 3 s on screen, counts as undrawn), an
    /// item by LookPrefetch.LookComplete (art decoded or resident). Only looks on screen count (GameScene.IsOnScreen,
    /// the draw walk's culling rectangle): the latency starts at packet arrival when the object was on screen then, else at
    /// the first frame it is on screen (p3 counted mobiles beyond the screen edge, never drawn, as seconds late). Looks
    /// never on screen within 3 s of arrival count as offscreen; items not on the ground are not tracked. Reports per kind
    /// that latency to the first complete draw (p50/p95/max), looks complete at their first visible frame, frames that drew
    /// a tracked look incomplete, and looks never complete within 3 s of being visible. Every 5 s, with the switch state.
    /// Also every mobile draw of the frame before (LookPrefetch.Frame*): draws that kept the previous look (held), that drew
    /// a look with layers missing although a complete one existed before (partial: the target is 0 with the switch on),
    /// with no complete look before (first_sight), or drew nothing (blank), with the frames that had any, and holds ended by
    /// LookPrefetch.HoldMaxMs; the hold durations and first-sight waits (p50/p95/p99/max, cap hits) and the frames that
    /// drew a mobile as nothing while waiting for its first complete look (first_sight_wait).
    /// </summary>
    internal static class LookPrefetchCheck
    {
        private const int Tracked = 1024, Samples = 8192, SlowMs = 250;
        private static readonly uint[] _serial = new uint[Tracked];
        private static readonly LookPrefetch.Kind[] _kind = new LookPrefetch.Kind[Tracked];
        private static readonly long[] _arrival = new long[Tracked];
        private static readonly bool[] _polled = new bool[Tracked], _seenOff = new bool[Tracked];
        private static readonly long[] _visible = new long[Tracked];
        // LookPrefetch.DrawSeq at the note: a mobile draw after it shows the noted look
        private static readonly uint[] _arrivalSeq = new uint[Tracked];
        // the last incomplete poll's missing part (LookPrefetch.LookComplete) and the incomplete polls, for the slow-look lines
        private static readonly int[] _missing = new int[Tracked], _polls = new int[Tracked];
        private static readonly ushort[] _missingBody = new ushort[Tracked];
        private static long _slowLogged;
        private static int _count;

        private static readonly long[][] _lat = { new long[Samples], new long[Samples], new long[Samples] };
        private static readonly int[] _latCount = new int[3];
        private static readonly long[] _firstComplete = new long[3], _arrived = new long[3], _merged = new long[3], _complete = new long[3];
        private static long _incompleteFrames, _never, _undrawn, _gone, _offscreen, _overflow, _start, _waitDraws, _waitFrames;
        private static long _heldDraws, _heldFrames, _partialDraws, _partialFrames, _firstSightDraws, _firstSightFrames, _blankDraws, _blankFrames;
        private static bool _switch;

        // mount-fix uncovered detector: every mobile draw of the frame before that drew a layer as nothing or partial (a page or
        // decode missing; first-sight waits included; holds are not), whether or not a note preceded it, by cause: the look
        // of a note still loading (noted), a mobile never drawn complete (first_sight), or neither (UNCOVERED: no WorldWriter
        // operation noted the change; logged with its look and the last WorldWriter operation on it and its equipment)
        private static readonly Dictionary<uint, (string Op, long At)> _lastOp = new Dictionary<uint, (string, long)>(4096);
        private static readonly HashSet<uint> _everComplete = new HashSet<uint>(1024);
        private static long _uncNoted, _uncFirstSight, _uncUncovered, _uncLogged, _uncFrames;

        /// <summary>WorldWriter.Admitted: <paramref name="op"/> on <paramref name="serial"/> (the detector's last operation).</summary>
        public static void Op(string op, uint serial)
        {
            if (serial == 0)
            {
                return;
            }

            if (_lastOp.Count >= 16384)
            {
                _lastOp.Clear();
            }

            _lastOp[serial] = (op, Stopwatch.GetTimestamp());
        }

        private static bool IsTracked(uint serial)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_serial[i] == serial)
                {
                    return true;
                }
            }

            return false;
        }

        private static string LastOp(uint serial, long now)
        {
            return _lastOp.TryGetValue(serial, out var o) ? FormattableString.Invariant($"{o.Op} {(now - o.At) * 1000.0 / Stopwatch.Frequency:F0}ms ago") : "none";
        }

        // the frame before's mobile draws (MobileFrameCache.Check*, stamped with LookPrefetch.DrawSeq)
        private static void Uncovered(World world, long now)
        {
            uint seq = LookPrefetch.DrawSeq;
            bool any = false;

            foreach (Mobile m in world.Mobiles.Values)
            {
                MobileFrameCache c = m.FrameCache;

                if (c == null || c.CheckDrawSeq != seq || c.CheckDrawHeld)
                {
                    continue;
                }

                if (c.CheckDrawComplete)
                {
                    _everComplete.Add(m.Serial);

                    continue;
                }

                any = true;
                bool noted = IsTracked(m.Serial);

                if (!noted)
                {
                    foreach (Item it in world.State.Children(m))
                    {
                        if (it.Layer != Data.Layer.Invalid && IsTracked(it.Serial))
                        {
                            noted = true;

                            break;
                        }
                    }
                }

                if (noted)
                {
                    _uncNoted++;
                }
                else if (!_everComplete.Contains(m.Serial))
                {
                    _uncFirstSight++;
                }
                else
                {
                    _uncUncovered++;

                    if (++_uncLogged <= 60)
                    {
                        var sb = new System.Text.StringBuilder();
                        ushort g = m.GetGraphicForAnimation();
                        sb.Append(FormattableString.Invariant(
                            $"[look-prefetch-check] UNCOVERED 0x{m.Serial:X8} body 0x{g:X4} group {Mobile.GetGroupForAnimation(m, g, true)} dir {(byte)m.GetDirectionForAnimation() & 7} mounted {m.IsMounted} war {m.InWarMode} player {ReferenceEquals(m, world.Player)}; last op {LastOp(m.Serial, now)}"));

                        foreach (Item it in world.State.Children(m))
                        {
                            if (it.Layer != Data.Layer.Invalid && _lastOp.ContainsKey(it.Serial))
                            {
                                sb.Append(FormattableString.Invariant($"; {it.Layer} 0x{it.Serial:X8}: {LastOp(it.Serial, now)}"));
                            }
                        }

                        Log.Info(sb.ToString());
                    }
                }
            }

            if (any)
            {
                _uncFrames++;
            }

            if (_everComplete.Count > 8192)
            {
                _everComplete.Clear();
            }
        }

        /// <summary>DevBench plan: one summary per plan step (<see cref="Step"/>) instead of every 5 s, so arms compare per scene.</summary>
        public static bool StepWindows;

        /// <summary>DevBench: the window ends; <paramref name="tag"/> null discards it (a step starts), else it is printed.</summary>
        public static void Step(string tag)
        {
            StepWindows = true;

            if (tag != null)
            {
                Summary(tag);
            }
            else
            {
                Summary(null, print: false);
            }
        }

        public static void Arrived(uint serial, LookPrefetch.Kind kind, bool on)
        {
            if (serial == 0)
            {
                return;
            }

            _switch = on;

            for (int i = 0; i < _count; i++)
            {
                if (_serial[i] == serial)
                {
                    // the earliest arrival of a look still pending counts: a 0x78's worn list (Equip, the wearer's serial)
                    // folds into the look its placement noted just before (p4 printed those as Equip arrived, never complete)
                    _merged[(int)kind]++;

                    return;
                }
            }

            _arrived[(int)kind]++;

            if (_count == Tracked)
            {
                _overflow++;

                return;
            }

            _serial[_count] = serial;
            _kind[_count] = kind;
            _arrival[_count] = Stopwatch.GetTimestamp();
            _polled[_count] = false;
            _seenOff[_count] = false;
            _visible[_count] = 0;
            _arrivalSeq[_count] = LookPrefetch.DrawSeq;
            _missing[_count] = _polls[_count] = 0;
            _missingBody[_count] = 0;
            _count++;
        }

        /// <summary>Draw start.</summary>
        private static World _lastWorld;

        public static void Draw(World world)
        {
            _lastWorld = world;
            long now = Stopwatch.GetTimestamp();

            if (_start == 0)
            {
                _start = now;
            }

            // the frame before's mobile draws (LookPrefetch.BeginDraw resets them after this)
            Count(LookPrefetch.FrameHeld, ref _heldDraws, ref _heldFrames);
            Count(LookPrefetch.FramePartial, ref _partialDraws, ref _partialFrames);
            Count(LookPrefetch.FrameFirstSight, ref _firstSightDraws, ref _firstSightFrames);
            Count(LookPrefetch.FrameBlank, ref _blankDraws, ref _blankFrames);
            Count(LookPrefetch.FrameFirstSightWait, ref _waitDraws, ref _waitFrames);

            if (world != null)
            {
                Uncovered(world, now);
            }
            var scene = Client.Game.GetScene<Scenes.GameScene>();

            bool incomplete = false;

            for (int i = 0; i < _count; i++)
            {
                Entity e = world?.Get(_serial[i]);

                if (e is Item item && _kind[i] == LookPrefetch.Kind.Equip && item.Container != 0 && world.Get(item.Container) is Mobile wearer)
                {
                    e = wearer;
                }

                bool drop;
                int poll = 0;

                if (e == null || e.IsDestroyed || world.Player == null || e.Distance > world.ClientViewRange || e is Item { OnGround: false })
                {
                    _gone++;
                    drop = true;
                }
                else if (_visible[i] == 0 && scene != null && !scene.IsOnScreen(e))
                {
                    // not drawn yet: its latency starts when it comes on screen
                    _seenOff[i] = true;
                    drop = now - _arrival[i] > Stopwatch.Frequency * 3;

                    if (drop)
                    {
                        _offscreen++;
                    }
                }
                else if (Visible(i, now) & (poll = Poll(i, e, out int missing, out ushort missingBody)) > 0)
                {
                    int k = (int)_kind[i];
                    _complete[k]++;

                    if (!_polled[i])
                    {
                        _firstComplete[k]++;
                    }

                    if (_latCount[k] < Samples)
                    {
                        _lat[k][_latCount[k]++] = now - _visible[i];
                    }

                    if (now - _visible[i] > Stopwatch.Frequency * SlowMs / 1000)
                    {
                        Slow(i, e, now, "complete");
                    }

                    drop = true;
                }
                else if (now - _visible[i] > Stopwatch.Frequency * 3)
                {
                    drop = true;

                    if (poll < 0)
                    {
                        // on screen by the culling rectangle, yet never drawn since the note (hidden, not visible)
                        _undrawn++;
                    }
                    else
                    {
                        _never++;
                        Slow(i, e, now, "never");

                        if (_never <= 20)
                        {
                            Log.Warn($"[look-prefetch-check] 0x{_serial[i]:X8} ({_kind[i]}) not complete 3 s after its packet, switch={(_switch ? "on" : "off")}");
                        }
                    }
                }
                else if (poll < 0)
                {
                    // not drawn since the note yet: nothing to poll
                    drop = false;
                }
                else
                {
                    _polled[i] = true;
                    incomplete = true;
                    drop = false;
                    _polls[i]++;
                    _missing[i] = missing;
                    _missingBody[i] = missingBody;
                }

                if (drop)
                {
                    _count--;
                    _serial[i] = _serial[_count];
                    _kind[i] = _kind[_count];
                    _arrival[i] = _arrival[_count];
                    _polled[i] = _polled[_count];
                    _seenOff[i] = _seenOff[_count];
                    _visible[i] = _visible[_count];
                    _arrivalSeq[i] = _arrivalSeq[_count];
                    _missing[i] = _missing[_count];
                    _polls[i] = _polls[_count];
                    _missingBody[i] = _missingBody[_count];
                    i--;
                }
            }

            if (incomplete)
            {
                _incompleteFrames++;
            }

            if (!StepWindows && now - _start >= Stopwatch.Frequency * 5)
            {
                Summary(null);
                _start = now;
            }
        }

        // a look complete only after SlowMs (or never): what was missing at its last incomplete poll, to tell a slow load from a
        // look the checker waits on and the draw does not (p4: single Legacy mobiles at 777-974 ms with no partial draw)
        private static void Slow(int i, Entity e, long now, string how)
        {
            if (++_slowLogged > 40)
            {
                return;
            }

            var m = e as Mobile;
            string what = _missing[i] == 0 ? "-" : _missing[i] < 0 ? $"body 0x{_missingBody[i]:X4}" : $"layer {(Data.Layer)_missing[i]} draw body 0x{_missingBody[i]:X4}";
            string look = m == null ? "" : $" graphic 0x{m.GetGraphicForAnimation():X4} group {Mobile.GetGroupForAnimation(m, m.GetGraphicForAnimation(), true)} dir {(byte)m.GetDirectionForAnimation() & 7} steps {m.Steps.Count} player {ReferenceEquals(m, m.World?.Player)}";
            double f = 1000.0 / Stopwatch.Frequency;
            Log.Info(FormattableString.Invariant(
                $"[look-prefetch-check] slow 0x{_serial[i]:X8} ({_kind[i]}) {how} after {(now - _visible[i]) * f:F0} ms visible ({(now - _arrival[i]) * f:F0} ms since arrival), {_polls[i]} incomplete polls, last missing {what};{look}"));
        }

        // 1: complete, 0: not yet, -1: a mobile not drawn since its note. A mobile's verdict is its draw's (the recomputed
        // look, LookPrefetch.LookComplete, disagreed with the draw in p4/p5: covered layers such as shoes under a dress, a
        // beard the draw leaves out, other groups; it only names what is missing in the slow-look lines now).
        private static int Poll(int i, Entity e, out int missing, out ushort missingBody)
        {
            missing = 0;
            missingBody = 0;

            if (e is Mobile m)
            {
                MobileFrameCache c = m.FrameCache;

                if (c == null || c.CheckDrawSeq == 0 || c.CheckDrawSeq <= _arrivalSeq[i])
                {
                    return -1;
                }

                if (c.CheckDrawComplete)
                {
                    return 1;
                }

                LookPrefetch.LookComplete(e, out missing, out missingBody);

                return 0;
            }

            return LookPrefetch.LookComplete(e, out missing, out missingBody) ? 1 : 0;
        }

        // the look's latency start: packet arrival if it was on screen then, else this first frame on screen
        private static bool Visible(int i, long now)
        {
            if (_visible[i] == 0)
            {
                _visible[i] = _seenOff[i] ? now : _arrival[i];
            }

            return true;
        }

        private static void Count(int draws, ref long total, ref long frames)
        {
            if (draws != 0)
            {
                total += draws;
                frames++;
            }
        }

        private static void Summary(string step, bool print = true)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[look-prefetch-check] {(step != null ? "step=" + step + " " : "")}switch={(_switch ? "on" : "off")}");

            for (int k = 0; k < 3; k++)
            {
                var span = _lat[k].AsSpan(0, _latCount[k]);
                span.Sort();
                double f = 1000.0 / Stopwatch.Frequency;
                double p50 = span.Length == 0 ? 0 : span[span.Length / 2] * f;
                double p95 = span.Length == 0 ? 0 : span[Math.Min(span.Length - 1, span.Length * 95 / 100)] * f;
                double max = span.Length == 0 ? 0 : span[^1] * f;
                sb.Append(FormattableString.Invariant($" {(LookPrefetch.Kind)k}: arrived {_arrived[k]} complete {_complete[k]} complete_at_first_draw {_firstComplete[k]} p50_ms {p50:F1} p95_ms {p95:F1} max_ms {max:F1} merged {_merged[k]} samples {span.Length};"));
                _latCount[k] = 0;
                _firstComplete[k] = _arrived[k] = _merged[k] = _complete[k] = 0;
            }

            sb.Append($" incomplete_frames {_incompleteFrames} never_complete {_never} undrawn {_undrawn} gone {_gone} offscreen {_offscreen} overflow {_overflow} tracked {_count}; prefetch resolved {LookPrefetch.Resolved} requested {LookPrefetch.Requested} dropped {LookPrefetch.Dropped}");
            sb.Append(FormattableString.Invariant(
                $" (mobile queue hw {LookPrefetch.Mobiles.HighWater}/{LookPrefetch.Mobiles.Capacity} dropped {LookPrefetch.Mobiles.Dropped}, item queue hw {LookPrefetch.Items.HighWater}/{LookPrefetch.Items.Capacity} dropped {LookPrefetch.Items.Dropped}, drain_max_ms {LookPrefetch.DrainMaxTicks * 1000.0 / Stopwatch.Frequency:F2})"));
            LookPrefetch.Mobiles.HighWater = LookPrefetch.Mobiles.Count;
            LookPrefetch.Items.HighWater = LookPrefetch.Items.Count;
            LookPrefetch.DrainMaxTicks = 0;
            sb.Append($"; mobile draws held {_heldDraws} ({_heldFrames} frames) partial {_partialDraws} ({_partialFrames} frames) first_sight {_firstSightDraws} ({_firstSightFrames} frames) blank {_blankDraws} ({_blankFrames} frames) hold_timeouts {LookPrefetch.HoldTimeouts}");
            sb.Append($"; incomplete_draws noted {_uncNoted} first_sight {_uncFirstSight} UNCOVERED {_uncUncovered} ({_uncFrames} frames)");
            _uncNoted = _uncFirstSight = _uncUncovered = _uncFrames = 0;
            sb.Append($"; first_sight_wait {_waitDraws} ({_waitFrames} frames); hold_ms {LookPrefetch.HoldMs.Summary()}; first_sight_ms {LookPrefetch.FirstSightMs.Summary()}; cap_frame_gap_ms {LookPrefetch.CapFrameGapMs.Summary()}");
            if (_lastWorld != null && !IdleOnSpawn.Enabled)
            {
                sb.Append("; ").Append(_lastWorld.IdleCadence.TakeSummary());
            }

            var gpu = Client.Game.GetGpuWorld()?.World;

            if (gpu != null && gpu.AnimPrefetchLooks != 0)
            {
                double looks = gpu.AnimPrefetchLooks;
                double mib = gpu.AnimPageBytes / (1024.0 * 1024.0);
                sb.Append(FormattableString.Invariant(
                    $"; gpu anim prefetch looks {gpu.AnimPrefetchLooks} pages/look dir {gpu.AnimPrefetchPagesDir / looks:F2} all8 {gpu.AnimPrefetchPagesAll / looks:F2} queued {gpu.AnimPrefetchQueued / looks:F2} MiB/look dir {gpu.AnimPrefetchPagesDir / looks * mib:F2} all8 {gpu.AnimPrefetchPagesAll / looks * mib:F2} queued {gpu.AnimPrefetchQueued / looks * mib:F2}"));
            }

            if (print)
            {
                Log.Info(sb.ToString());
            }

            _incompleteFrames = _never = _undrawn = _gone = _offscreen = _overflow = _waitDraws = _waitFrames = 0;
            _heldDraws = _heldFrames = _partialDraws = _partialFrames = _firstSightDraws = _firstSightFrames = _blankDraws = _blankFrames = 0;
        }
    }
}
#endif
