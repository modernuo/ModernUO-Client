// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using System.Collections.Generic;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network
{
    /// <summary>
    /// CLEANUP(release): CUO_OPL_SPREAD_CHECK=1 checks the property-list sends against the requests, whichever path sends
    /// (<c>opl_request_budget</c> on: <see cref="OplRequestQueue"/>; off: the old list). Every request the old path would
    /// send (each AddMegaClilocRequest) opens a demand unless one is open: pending, or sent and unanswered (a hover or gump
    /// request within <see cref="OplRequestQueue.RetryMs"/>, a revision request for the same revision). Each demand must be
    /// sent once within CUO_OPL_SPREAD_CHECK_SECONDS (default 120) unless World removes the object first. Logs
    /// <c>[opl-spread-check] miss|duplicate|unrequested</c> lines (the first 200) and a summary every 5 s with the switch,
    /// the counts and the most 0xD6 packets and serials sent in one frame.
    /// </summary>
    internal static class OplSpreadCheck
    {
        private const int MaxLogged = 200;

        private struct State
        {
            public uint DemandTick;
            public uint SentTick;
            public uint Revision;
            public bool Pending;
            public bool HasRevision;
            public bool Urgent;
            public bool MissLogged;
        }

        private static readonly bool s_on = Environment.GetEnvironmentVariable("CUO_OPL_SPREAD_CHECK") == "1";
        private static readonly uint s_windowMs = (uint) (int.TryParse(Environment.GetEnvironmentVariable("CUO_OPL_SPREAD_CHECK_SECONDS"), out int s) && s > 0 ? s : 120) * 1000;

        /// <summary>Tests: the checker (null: CUO_OPL_SPREAD_CHECK=1).</summary>
        internal static bool? Override;

        /// <summary>Tests: the miss window in ms (null: CUO_OPL_SPREAD_CHECK_SECONDS).</summary>
        internal static uint? WindowOverride;

        /// <summary>Tests: sees each mismatch line.</summary>
        internal static Action<string> MismatchObserver;

        public static bool On => Override ?? s_on;

        private static readonly Dictionary<uint, State> s_states = new Dictionary<uint, State>();
        private static uint s_nextSummary;
        private static int s_logged;

        public static long Demands, Merged, Sends, Duplicates, Unrequested, Dropped, Misses;
        public static int MaxFramePackets, MaxFrameSerials, FramesSending;
        public static uint MaxLatencyMs, MaxUrgentLatencyMs;

        private static uint WindowMs => WindowOverride ?? s_windowMs;

        public static void Reset()
        {
            s_states.Clear();
            s_nextSummary = 0;
            s_logged = 0;
            Demands = Merged = Sends = Duplicates = Unrequested = Dropped = Misses = 0;
            MaxFramePackets = MaxFrameSerials = FramesSending = 0;
            MaxLatencyMs = MaxUrgentLatencyMs = 0;
        }

        public static void Demand(uint serial, OplRequestKind kind, uint revision, uint now)
        {
            bool urgent = kind != OplRequestKind.Revision;

            if (s_states.TryGetValue(serial, out State st))
            {
                if (st.Pending)
                {
                    if (kind == OplRequestKind.Revision)
                    {
                        st.Revision = revision;
                        st.HasRevision = true;
                    }

                    st.Urgent |= urgent;
                    s_states[serial] = st;
                    Merged++;

                    return;
                }

                bool open = kind == OplRequestKind.Revision ? st.HasRevision && st.Revision == revision : now - st.SentTick < OplRequestQueue.RetryMs;

                if (open)
                {
                    Merged++;

                    return;
                }
            }

            if (kind == OplRequestKind.Revision)
            {
                st.Revision = revision;
                st.HasRevision = true;
            }

            st.Pending = true;
            st.Urgent = urgent;
            st.DemandTick = now;
            st.MissLogged = false;
            s_states[serial] = st;
            Demands++;
        }

        public static void Sent(uint serial, uint now)
        {
            Sends++;

            if (!s_states.TryGetValue(serial, out State st))
            {
                Unrequested++;
                Report($"unrequested serial 0x{serial:X8}: sent without a request (removed, or answered)");
                st = default;
            }
            else if (!st.Pending)
            {
                Duplicates++;
                Report($"duplicate serial 0x{serial:X8}: sent again {now - st.SentTick} ms after the last send, no new request");
            }
            else
            {
                uint latency = now - st.DemandTick;

                if (latency > MaxLatencyMs)
                {
                    MaxLatencyMs = latency;
                }

                if (st.Urgent && latency > MaxUrgentLatencyMs)
                {
                    MaxUrgentLatencyMs = latency;
                }
            }

            st.Pending = false;
            st.SentTick = now;
            s_states[serial] = st;
        }

        public static void Answered(uint serial)
        {
            if (s_states.TryGetValue(serial, out State st) && !st.Pending)
            {
                s_states.Remove(serial);
            }
        }

        public static void Removed(uint serial)
        {
            if (s_states.Remove(serial, out State st) && st.Pending)
            {
                Dropped++;
            }
        }

        public static void Cleared()
        {
            foreach (KeyValuePair<uint, State> kv in s_states)
            {
                if (kv.Value.Pending)
                {
                    Dropped++;
                }
            }

            s_states.Clear();
        }

        /// <summary>End of the frame's sends; <paramref name="sending"/> is false while tooltips are off (nothing is sent,
        /// so nothing is missed).</summary>
        public static void FrameEnd(uint now, int packets, int serials, bool sending, int budget)
        {
            if (packets > MaxFramePackets)
            {
                MaxFramePackets = packets;
            }

            if (serials > MaxFrameSerials)
            {
                MaxFrameSerials = serials;
            }

            if (packets != 0)
            {
                FramesSending++;
            }

            if (now < s_nextSummary)
            {
                return;
            }

            s_nextSummary = now + 5000;
            int pending = 0;
            uint oldest = 0;

            foreach (KeyValuePair<uint, State> kv in s_states)
            {
                if (!kv.Value.Pending)
                {
                    continue;
                }

                pending++;
                uint age = now - kv.Value.DemandTick;
                oldest = Math.Max(oldest, age);

                if (sending && age > WindowMs && !kv.Value.MissLogged)
                {
                    Misses++;
                    Report($"miss serial 0x{kv.Key:X8}: requested {age} ms ago ({(kv.Value.Urgent ? "hover/gump" : "revision")}), not sent");
                    // a ref write does not invalidate the enumeration
                    System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(s_states, kv.Key).MissLogged = true;
                }
            }

            Log.Info(
                $"[opl-spread-check] opl_request_budget={budget} ({(budget > 0 ? "on" : "off")}), demands {Demands}, merged {Merged}, sends {Sends}, duplicates {Duplicates}, unrequested {Unrequested}, dropped {Dropped}, misses {Misses}, pending {pending} (oldest {oldest} ms), max/frame packets {MaxFramePackets} serials {MaxFrameSerials}, frames sending {FramesSending}, max latency {MaxLatencyMs} ms (hover/gump {MaxUrgentLatencyMs} ms)"
            );
        }

        private static void Report(string what)
        {
            MismatchObserver?.Invoke(what);

            if (s_logged < MaxLogged)
            {
                s_logged++;
                Log.Warn($"[opl-spread-check] {what}");
            }
        }
    }
}
#endif
