// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClassicUO.Game;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network
{
    /// <summary>
    /// CLEANUP(release): CUO_OPL_DEBOUNCE_CHECK=1 counts, per serial, the 0xDC notices (with their revisions), the
    /// property-list requests sent (0xD6, whichever path sends: <c>opl_request_budget</c> on or off) and the answers. A
    /// request is a <b>repeat</b> when the serial was already requested for the same revision (the last 0xDC's, or none)
    /// and World has not removed the object since: the client did not debounce. A notice is a <b>same-revision repeat</b>
    /// when the serial's previous 0xDC carried the same revision: the server re-sent an unchanged revision. An answer
    /// with no request since the previous answer is <b>unrequested</b> (a server push, e.g. a vendor buy list). Logs
    /// <c>[opl-debounce-check] repeat ...</c> lines (the first 50) and a summary with histograms every 10 s and at exit.
    /// </summary>
    internal static class OplDebounceCheck
    {
        private const int MaxLogged = 50;

        private struct State
        {
            public uint NoticeRevision;
            public uint SentRevision;
            public uint SentTick;
            public int Notices;
            public int NoticeRepeats;
            public int NoticesHeld;
            public int RevisionChanges;
            public int Requests;
            public int Repeats;
            public int RepeatsAfterAnswer;
            public int Reentries;
            public int Answers;
            public int UnrequestedAnswers;
            public int Removals;
            public int SentRemovals;
            public int SentClears;
            public bool HasNotice;
            public bool HasSent;
            public bool AnsweredSinceSend;
        }

        private static readonly bool s_on = Environment.GetEnvironmentVariable("CUO_OPL_DEBOUNCE_CHECK") == "1";

        /// <summary>Tests: the checker (null: CUO_OPL_DEBOUNCE_CHECK=1).</summary>
        internal static bool? Override;

        /// <summary>Tests: sees each repeat line.</summary>
        internal static Action<string> RepeatObserver;

        public static bool On => Override ?? s_on;

        private static readonly Dictionary<uint, State> s_states = new Dictionary<uint, State>();
        private static int s_clears;
        private static uint s_nextSummary;
        private static int s_logged;
        private static bool s_exitLogged;

        public static long Notices, NoticeRepeats, NoticeRepeatsMobile, NoticesHeld, Requests, Repeats, RepeatsAfterAnswer, Reentries, Answers, UnrequestedAnswers;

        public static void Reset()
        {
            s_states.Clear();
            s_clears = 0;
            s_nextSummary = 0;
            s_logged = 0;
            s_exitLogged = false;
            Notices = NoticeRepeats = NoticeRepeatsMobile = NoticesHeld = Requests = Repeats = RepeatsAfterAnswer = Reentries = Answers = UnrequestedAnswers = 0;
        }

        private static uint Strip(uint revision) => revision & ~0x40000000u;

        /// <summary>A 0xDC for <paramref name="serial"/>; <paramref name="held"/>: the client already holds that revision.</summary>
        public static void Notice(uint serial, uint revision, bool held)
        {
            ref State st = ref CollectionsMarshal.GetValueRefOrAddDefault(s_states, serial, out _);
            Notices++;
            st.Notices++;

            if (st.HasNotice)
            {
                if (Strip(st.NoticeRevision) == Strip(revision))
                {
                    NoticeRepeats++;
                    st.NoticeRepeats++;

                    if (SerialHelper.IsMobile(serial))
                    {
                        NoticeRepeatsMobile++;
                    }
                }
                else
                {
                    st.RevisionChanges++;
                }
            }

            if (held)
            {
                NoticesHeld++;
                st.NoticesHeld++;
            }

            st.NoticeRevision = revision;
            st.HasNotice = true;
        }

        /// <summary>A request for <paramref name="serial"/> left in a 0xD6 (or 0xBF.10).</summary>
        public static void Sent(uint serial, uint now)
        {
            ref State st = ref CollectionsMarshal.GetValueRefOrAddDefault(s_states, serial, out _);
            uint want = st.HasNotice ? Strip(st.NoticeRevision) : 0;
            Requests++;
            st.Requests++;

            if (st.HasSent)
            {
                if (st.SentRemovals != st.Removals || st.SentClears != s_clears)
                {
                    Reentries++;
                    st.Reentries++;
                }
                else if (st.SentRevision == want)
                {
                    Repeats++;
                    st.Repeats++;

                    if (st.AnsweredSinceSend)
                    {
                        RepeatsAfterAnswer++;
                        st.RepeatsAfterAnswer++;
                    }

                    Report(
                        $"repeat serial 0x{serial:X8} ({(SerialHelper.IsMobile(serial) ? "mobile" : "item")}): request {st.Requests} at unchanged revision 0x{want:X8}, {now - st.SentTick} ms after the last, {(st.AnsweredSinceSend ? "answered in between" : "unanswered")}; 0xDC {st.Notices} ({st.NoticeRepeats} same-revision)"
                    );
                }
            }

            st.HasSent = true;
            st.SentRevision = want;
            st.SentTick = now;
            st.SentRemovals = st.Removals;
            st.SentClears = s_clears;
            st.AnsweredSinceSend = false;
        }

        /// <summary>A 0xD6 answer came in.</summary>
        public static void Answered(uint serial)
        {
            ref State st = ref CollectionsMarshal.GetValueRefOrAddDefault(s_states, serial, out _);
            Answers++;
            st.Answers++;

            if (!st.HasSent || st.AnsweredSinceSend)
            {
                UnrequestedAnswers++;
                st.UnrequestedAnswers++;
            }

            st.AnsweredSinceSend = true;
        }

        /// <summary>World removed the object: a later request is a re-entry, not a repeat.</summary>
        public static void Removed(uint serial)
        {
            ref State st = ref CollectionsMarshal.GetValueRefOrNullRef(s_states, serial);

            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref st))
            {
                st.Removals++;
                st.HasNotice = false;
            }
        }

        public static void Cleared()
        {
            s_clears++;

            foreach (KeyValuePair<uint, State> kv in s_states)
            {
                CollectionsMarshal.GetValueRefOrNullRef(s_states, kv.Key).HasNotice = false;
            }
        }

        /// <summary>The 10 s summary, from the frame's request pass.</summary>
        public static void Tick(uint now, int budget)
        {
            if (!On || now < s_nextSummary)
            {
                return;
            }

            s_nextSummary = now + 10000;
            LogSummary(budget, "10 s");
        }

        public static void LogSummary(int budget, string when)
        {
            if (!On || s_exitLogged)
            {
                return;
            }

            // the driver's quit logs it before the exit, UnloadContent again for a window close
            s_exitLogged = when == "exit";

            // requests per serial 0, 1, 2, 3+; notices per serial 0, 1, 2, 3-5, 6+; same-revision notices 0, 1-4, 5+
            Span<int> req = stackalloc int[4];
            Span<int> not = stackalloc int[5];
            Span<int> rep = stackalloc int[3];
            int repeatSerials = 0, unanswered = 0, maxNotices = 0, maxRequests = 0;
            uint maxNoticeSerial = 0, maxRequestSerial = 0;

            foreach (KeyValuePair<uint, State> kv in s_states)
            {
                State st = kv.Value;
                req[Math.Min(st.Requests, 3)]++;
                not[st.Notices <= 2 ? st.Notices : st.Notices <= 5 ? 3 : 4]++;
                rep[st.NoticeRepeats == 0 ? 0 : st.NoticeRepeats < 5 ? 1 : 2]++;

                if (st.Repeats != 0)
                {
                    repeatSerials++;
                }

                if (st.HasSent && !st.AnsweredSinceSend)
                {
                    unanswered++;
                }

                if (st.Notices > maxNotices)
                {
                    maxNotices = st.Notices;
                    maxNoticeSerial = kv.Key;
                }

                if (st.Requests > maxRequests)
                {
                    maxRequests = st.Requests;
                    maxRequestSerial = kv.Key;
                }
            }

            Log.Info(
                $"[opl-debounce-check] {when}: opl_request_budget={budget} ({(budget > 0 ? "on" : "off")}), serials {s_states.Count} | requests {Requests}: per serial 0:{req[0]} 1:{req[1]} 2:{req[2]} 3+:{req[3]}, max {maxRequests} (0x{maxRequestSerial:X8}); repeats at an unchanged revision {Repeats} on {repeatSerials} serials ({RepeatsAfterAnswer} after an answer), re-entries {Reentries} | 0xDC {Notices}: per serial 0:{not[0]} 1:{not[1]} 2:{not[2]} 3-5:{not[3]} 6+:{not[4]}, max {maxNotices} (0x{maxNoticeSerial:X8}); same-revision repeats {NoticeRepeats} (mobiles {NoticeRepeatsMobile}), serials with 0:{rep[0]} 1-4:{rep[1]} 5+:{rep[2]}; already held {NoticesHeld} | answers {Answers}, unrequested {UnrequestedAnswers}, unanswered serials {unanswered}"
            );
        }

        private static void Report(string what)
        {
            RepeatObserver?.Invoke(what);

            if (s_logged < MaxLogged)
            {
                s_logged++;
                Log.Warn($"[opl-debounce-check] {what}");
            }
        }
    }
}
#endif
