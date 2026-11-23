// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    /// <summary>
    /// mount_swap (mount-fix, owner 2026-11-23: approved; riding is the Mount layer on every shard, so the pairing is data,
    /// not an inferred server rule). A dismount sends the rider's Mount-layer item away (0x1D, or into a container) and
    /// creates the horse mobile (0x78). The rider's on-foot look and the new horse are drawable at different frames (the horse
    /// starts with no complete look: first-sight wait, Gpu pages not resident), so for a frame or two the rider stands with
    /// no horse. A matched dismount keeps drawing the rider's previous (mounted) look and hides the horse until both new
    /// looks are drawable, then both switch in the same frame.
    /// Match: a Mount-layer item leaving a mobile (WorldWriter: <see cref="Dismounted"/>) and a mobile created within
    /// <see cref="MatchWindowMs"/> (<see cref="Created"/>) whose body is the mount item's animation body, within one tile of
    /// the rider; exactly one candidate each way, otherwise nothing happens (unmatched cases draw as before). The hold ends
    /// when both looks are complete, when either mobile goes, or after <see cref="HoldMaxMs"/> (LookPrefetch.FirstSightMaxMs).
    /// A matched horse is drawn at its target alpha from its first frame (no fade-in from 0, whatever mobile_fade_in says).
    /// Not covered: the mount side (the horse's 0x1D removes it before the rider's mounted look is drawable; a removed mobile
    /// cannot be drawn on), and a server that sends the horse and the removal in separate flushes with a frame between.
    /// One per <see cref="World"/>; main thread; fixed arrays, no allocation. CUO_MOUNT_SWAP_CHECK=1 (needs
    /// CUO_LOOK_PREFETCH_CHECK=1 for the draw verdicts) counts frames that drew a matched pair inconsistently (rider on foot
    /// and no horse, or the mounted rider and the horse), per pair and in the 5 s summary.
    /// CUO_MOUNT_SWAP=1|0 overrides the setting in developer builds.
    /// </summary>
    internal sealed class MountSwap
    {
        public const int MatchWindowMs = 25;
        public const int HoldMaxMs = LookPrefetch.FirstSightMaxMs;
        private const int Pending = 4;

#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_MOUNT_SWAP") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_MOUNT_SWAP_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        /// <summary>Tests: whether a mobile's current look is drawable completely (null: LookPrefetch.LookComplete).</summary>
        [ThreadStatic] internal static Func<Mobile, bool> LookCompleteForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.MountSwap ?? false);

        private readonly World _world;

        // dismounts and created mobiles waiting for their partner (ring, newest overwrites the oldest)
        private readonly uint[] _riders = new uint[Pending];
        private readonly ushort[] _riderBody = new ushort[Pending];
        private readonly uint[] _riderAt = new uint[Pending];
        private readonly uint[] _created = new uint[Pending];
        private readonly uint[] _createdAt = new uint[Pending];
        private int _riderNext, _createdNext;

        // the active pair
        private uint _rider, _horse, _since;

        /// <summary>Pairs matched, ended complete, ended by the bound, ended because a mobile went (totals).</summary>
        public long Matched, Completed, TimedOut, Gone, Ambiguous;

        public MountSwap(World world)
        {
            _world = world;
        }

        public uint Rider => _rider;
        public uint Horse => _horse;
        public bool Active => _rider != 0;

        private static uint Now => LookPrefetch.Now;

        /// <summary>WorldWriter: <paramref name="mountItem"/> (Layer.Mount) is leaving <paramref name="rider"/>.</summary>
        public void Dismounted(Mobile rider, Item mountItem)
        {
            if (rider == null || mountItem == null || !(Enabled || Check))
            {
                return;
            }

            ushort body = mountItem.GetGraphicForAnimation();

            if (body == 0xFFFF)
            {
                return;
            }

            int i = _riderNext++ % Pending;
            _riders[i] = rider.Serial;
            _riderBody[i] = body;
            _riderAt[i] = Now;
        }

        /// <summary>WorldWriter.UpsertMobile: <paramref name="mobile"/> was just created.</summary>
        public void Created(Mobile mobile)
        {
            if (mobile == null || !(Enabled || Check))
            {
                return;
            }

            int i = _createdNext++ % Pending;
            _created[i] = mobile.Serial;
            _createdAt[i] = Now;
        }

        /// <summary>Draw start (both renderers): matches waiting dismounts, then holds or releases the active pair.</summary>
        public void Frame()
        {
            if (_rider == 0 && _riderNext == 0)
            {
                return;
            }

            uint now = Now;

            if (_rider == 0)
            {
                Match(now);
            }

            if (_rider == 0)
            {
                return;
            }

            Mobile r = _world.Mobiles.Get(_rider);
            Mobile h = _world.Mobiles.Get(_horse);

            if (r == null || r.IsDestroyed || h == null || h.IsDestroyed)
            {
                Gone++;
                End(r, h);

                return;
            }

            if (Complete(h) && Complete(r))
            {
                Completed++;
                End(r, h);

                return;
            }

            if (now - _since > HoldMaxMs)
            {
                TimedOut++;
                End(r, h);

                return;
            }

            if (r.FrameCache != null)
            {
                r.FrameCache.PairHold = true;
            }

            h.PairHidden = true;
            LookPrefetch.ScheduleBound(_since, HoldMaxMs);
        }

        private static bool Complete(Mobile m) => LookCompleteForTests?.Invoke(m) ?? LookPrefetch.LookComplete(m);

        private void Match(uint now)
        {
            for (int i = 0; i < Pending; i++)
            {
                uint rs = _riders[i];

                if (rs == 0)
                {
                    continue;
                }

                if (now - _riderAt[i] > MatchWindowMs * 4)
                {
                    // a dismount whose horse never came (an ethereal, a stabled pet): nothing to hold
                    _riders[i] = 0;

                    continue;
                }

                Mobile r = _world.Mobiles.Get(rs);

                if (r == null || r.IsDestroyed || r.IsMounted)
                {
                    _riders[i] = 0;

                    continue;
                }

                Mobile found = null;
                int candidates = 0;

                for (int j = 0; j < Pending; j++)
                {
                    uint cs = _created[j];

                    if (cs == 0 || cs == rs)
                    {
                        continue;
                    }

                    uint dt = _createdAt[j] > _riderAt[i] ? _createdAt[j] - _riderAt[i] : _riderAt[i] - _createdAt[j];

                    if (dt > MatchWindowMs)
                    {
                        continue;
                    }

                    Mobile c = _world.Mobiles.Get(cs);

                    if (c == null || c.IsDestroyed || (c.Graphic != _riderBody[i] && c.GetGraphicForAnimation() != _riderBody[i]))
                    {
                        continue;
                    }

                    if (Math.Abs(c.X - r.X) > 1 || Math.Abs(c.Y - r.Y) > 1)
                    {
                        continue;
                    }

                    candidates++;
                    found = c;
                }

                if (candidates == 0)
                {
                    // the horse may come in the next drain, inside the window
                    continue;
                }

                _riders[i] = 0;

                if (candidates > 1)
                {
                    Ambiguous++;

                    continue;
                }

                for (int j = 0; j < Pending; j++)
                {
                    if (_created[j] == found.Serial)
                    {
                        _created[j] = 0;
                    }
                }

                Matched++;
#if CUO_DEVTOOLS
                if (Check)
                {
                    MountSwapCheck.Begin(_world, rs, found.Serial);
                }
#endif

                if (!Enabled)
                {
                    // the checker's matching only: the pair draws as before
                    return;
                }

                _rider = rs;
                _horse = found.Serial;
                _since = now;
                found.AlphaSnap = true;

                return;
            }
        }

        private void End(Mobile r, Mobile h)
        {
            if (r?.FrameCache != null)
            {
                r.FrameCache.PairHold = false;
            }

            if (h != null)
            {
                h.PairHidden = false;
            }

            _rider = _horse = 0;
        }

        /// <summary>Map change, logout: forget everything.</summary>
        public void Clear()
        {
            End(_world.Mobiles.Get(_rider), _world.Mobiles.Get(_horse));
            Array.Clear(_riders);
            Array.Clear(_created);
            _riderNext = _createdNext = 0;
        }
    }

#if CUO_DEVTOOLS
    /// <summary>
    /// CUO_MOUNT_SWAP_CHECK=1 (CLEANUP(release)): mount_swap's measure, both states. Each matched pair (or, with the switch
    /// off, each dismount the matcher would have paired: the checker forces matching) is followed from the match until the horse has
    /// been drawn complete for 3 frames, at most 1 s. Each frame it reads the last draw's verdicts (MobileFrameCache.Check*,
    /// set with CUO_LOOK_PREFETCH_CHECK=1): the rider drawn mounted (held) or on foot, the horse drawn complete and visible
    /// (alpha &gt; 0) or not. Inconsistent: on foot with no horse, or mounted with the horse. Per pair one line; every 5 s a
    /// summary.
    /// </summary>
    internal static class MountSwapCheck
    {
        private static World _world;
        private static uint _rider, _horse, _lastSeq;
        private static long _start, _pairs, _frames, _bad, _bothFrames, _noneFrames;
        private static int _pairFrames, _pairBad, _horseOk;
        private static System.Text.StringBuilder _seq = new System.Text.StringBuilder(64);

        public static void Begin(World world, uint rider, uint horse)
        {
            _world = world;
            _rider = rider;
            _horse = horse;
            _pairFrames = _pairBad = _horseOk = 0;
            _lastSeq = 0;
            _seq.Clear();
            _pairs++;
            _start = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        /// <summary>Draw start, after MountSwap.Frame: the frame before's draws of the pair.</summary>
        public static void Frame()
        {
            if (_rider == 0 || _world == null)
            {
                return;
            }

            uint seq = LookPrefetch.DrawSeq;

            if (seq == _lastSeq)
            {
                return;
            }

            _lastSeq = seq;
            Mobile r = _world.Mobiles.Get(_rider);
            Mobile h = _world.Mobiles.Get(_horse);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();

            if (r == null || h == null || now - _start > System.Diagnostics.Stopwatch.Frequency)
            {
                Done("gone_or_1s");

                return;
            }

            MobileFrameCache rc = r.FrameCache, hc = h.FrameCache;
            bool riderDrawn = rc != null && rc.CheckDrawSeq == seq;
            bool riderMounted = riderDrawn && rc.CheckDrawHeld;
            bool horseVisible = hc != null && hc.CheckDrawSeq == seq && hc.CheckDrawComplete && h.AlphaHue != 0;

            if (!riderDrawn)
            {
                _seq.Append('-');

                return;
            }

            _pairFrames++;
            _frames++;
            bool bad = riderMounted == horseVisible;

            if (bad)
            {
                _pairBad++;
                _bad++;

                if (riderMounted)
                {
                    _bothFrames++;
                }
                else
                {
                    _noneFrames++;
                }
            }

            _seq.Append(riderMounted ? (horseVisible ? 'B' : 'M') : (horseVisible ? 'H' : 'N'));

            if (horseVisible && !riderMounted && ++_horseOk >= 3)
            {
                Done("settled");
            }
        }

        private static void Done(string how)
        {
            Log.Info($"[mount-swap-check] pair rider 0x{_rider:X8} horse 0x{_horse:X8} switch={(MountSwap.Enabled ? "on" : "off")} {how} frames {_pairFrames} inconsistent {_pairBad} seq {_seq} (M mounted rider no horse, H horse and rider on foot, B both, N neither); totals pairs {_pairs} frames {_frames} inconsistent {_bad} (both {_bothFrames}, neither {_noneFrames})");
            _rider = _horse = 0;
        }
    }
#endif
}
