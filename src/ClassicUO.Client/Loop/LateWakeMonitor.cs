// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.Loop
{
    /// <summary>
    /// Late-wake backoff for the event loop (the concept follows ModernUO's idle-sleep health check; no code is shared). Each
    /// timed wait reports whether it returned late (more than <see cref="LateThresholdMs"/> after its deadline). Once a second
    /// the rate is checked: a sample is bad when more than <see cref="MinLate"/> waits were late and they were at least
    /// <see cref="LatePercent"/> % of the waits. Two bad samples in a row suspend the event loop (the legacy loop runs) for a
    /// backoff that starts at 5 s and doubles up to 120 s; 60 s without a backoff resets the escalation. Times are in ms on a
    /// monotonic clock and compared by subtraction.
    /// </summary>
    internal sealed class LateWakeMonitor
    {
        public const double LateThresholdMs = 2;
        public const int MinLate = 1;
        public const int LatePercent = 10;
        public const long SampleMs = 1000;
        public const long BackoffBaseMs = 5000;
        public const long BackoffMaxMs = 120_000;
        public const long ResetAfterCleanMs = 60_000;

        private int _late, _waits, _badSamples, _backoffs;
        private long _nextSample, _suspendedUntil, _lastBackoff, _currentBackoffMs;
        private bool _started, _suspended;

        /// <summary>Backoffs so far (for the log).</summary>
        public int TotalBackoffs { get; private set; }

        /// <summary>The length of the backoff in force or last applied, in ms.</summary>
        public long CurrentBackoffMs => _currentBackoffMs;

        public static bool IsLate(double latenessMs) => latenessMs > LateThresholdMs;

        public void RecordWait(bool late)
        {
            _waits++;

            if (late)
            {
                _late++;
            }
        }

        public bool IsSuspended(long nowMs) => _suspended && nowMs - _suspendedUntil < 0;

        /// <summary>Call once per loop iteration; returns true when this call started a backoff.</summary>
        public bool Sample(long nowMs)
        {
            if (!_started)
            {
                _started = true;
                _nextSample = nowMs + SampleMs;

                return false;
            }

            if (nowMs - _nextSample < 0)
            {
                return false;
            }

            _nextSample = nowMs + SampleMs;
            int late = _late, waits = _waits;
            _late = _waits = 0;

            if (_backoffs > 0 && nowMs - _lastBackoff > ResetAfterCleanMs)
            {
                _backoffs = 0;
            }

            if (late <= MinLate || late * 100 < waits * LatePercent)
            {
                _badSamples = 0;

                return false;
            }

            if (++_badSamples < 2)
            {
                return false;
            }

            _badSamples = 0;
            _currentBackoffMs = Math.Min(BackoffBaseMs << Math.Min(_backoffs, 5), BackoffMaxMs);
            _backoffs++;
            TotalBackoffs++;
            _lastBackoff = nowMs;
            _suspendedUntil = nowMs + _currentBackoffMs;
            _suspended = true;

            return true;
        }
    }
}
