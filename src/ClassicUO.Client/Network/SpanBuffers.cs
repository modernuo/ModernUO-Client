// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Text;
using ClassicUO.Configuration;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network
{
    /// <summary>Which reader and writer a packet uses (<see cref="SpanBuffers.Mode"/>).</summary>
    public enum SpanBufferMode : byte
    {
        /// <summary>StackDataReader / StackDataWriter (span_reader off).</summary>
        Old,

        /// <summary>SpanReader / SpanWriter (span_reader on).</summary>
        New,

        /// <summary>CUO_SPAN_READER_CHECK with the switch off: both run, the old values are used.</summary>
        CheckOld,

        /// <summary>CUO_SPAN_READER_CHECK with the switch on: both run, the new values are used.</summary>
        CheckNew
    }

    /// <summary>
    /// span_reader (net-rework step 2): packets are read with ModernUO's SpanReader and built with its SpanWriter instead of
    /// StackDataReader / StackDataWriter (<see cref="NetReader"/>, <see cref="NetWriter"/>). With it on, a packet a handler
    /// reads past its end throws, and the dispatch drops the connection with the packet logged (PacketHandlers.AnalyzePacket).
    /// Read once at startup (CUO_SPAN_READER=1|0 overrides it in developer builds; CUO_SPAN_READER_CHECK=1 runs the old and
    /// the new reader on every read and the old and the new writer on every outgoing packet, and logs differences). Default off.
    /// </summary>
    internal static class SpanBuffers
    {
#if CUO_DEVTOOLS
        private static readonly bool s_on = Environment.GetEnvironmentVariable("CUO_SPAN_READER") switch
        {
            "1" => true,
            "0" => false,
            _ => Settings.GlobalSettings?.SpanReader ?? false
        };

        private static readonly bool s_check = Environment.GetEnvironmentVariable("CUO_SPAN_READER_CHECK") == "1";

        private static readonly SpanBufferMode s_mode = s_check
            ? (s_on ? SpanBufferMode.CheckNew : SpanBufferMode.CheckOld)
            : (s_on ? SpanBufferMode.New : SpanBufferMode.Old);
#else
        private static readonly SpanBufferMode s_mode = Settings.GlobalSettings?.SpanReader ?? false ? SpanBufferMode.New : SpanBufferMode.Old;
#endif

        /// <summary>Tests: the mode (null: the setting and the checker variable).</summary>
        internal static SpanBufferMode? ModeOverride;

        public static SpanBufferMode Mode => ModeOverride ?? s_mode;

        /// <summary>The new reader decides: a short packet throws and ends the connection.</summary>
        public static bool On => Mode is SpanBufferMode.New or SpanBufferMode.CheckNew;

#if CUO_DEVTOOLS
        public static bool CheckOn => Mode is SpanBufferMode.CheckOld or SpanBufferMode.CheckNew;

        /// <summary>Checker counts over the session: packets read and built with both, reads compared, differences.</summary>
        public static long Packets, Reads, Writes, ReaderMismatches, WriterMismatches;

        // packets checked per id, and per 0xBF sub-command (coverage of the parity proof)
        private static readonly long[] s_ids = new long[256], s_bf = new long[256];

        /// <summary>Tests: sees each mismatch line.</summary>
        internal static Action<string> MismatchObserver;

        private static long _logAt, _coverageAt;
        private static bool _announced, _exitHooked;

        /// <summary>The dispatch: a packet about to be read with both readers.</summary>
        public static void Packet(ReadOnlySpan<byte> packet)
        {
            Packets++;
            s_ids[packet[0]]++;

            if (packet[0] == 0xBF && packet.Length >= 5)
            {
                s_bf[packet[4]]++;
            }
        }

        internal static void ReaderMismatch(ReadOnlySpan<byte> packet, string call, int at, string oldValue, string newValue,
            int oldPos, int newPos, Exception e)
        {
            ReaderMismatches++;
            string line = $"[span-reader-check] reader mismatch {Id(packet)} len {packet.Length} {call} at {at}: old {oldValue} (pos {oldPos}), new {newValue} (pos {newPos})" +
                (e != null ? $" {e.GetType().Name}: {e.Message}" : "") + $"; bytes {Hex(packet, 256)}";
            Report(line, ReaderMismatches);
        }

        internal static void WriterMismatch(ReadOnlySpan<byte> oldBytes, ReadOnlySpan<byte> newBytes)
        {
            WriterMismatches++;
            int diff = oldBytes.CommonPrefixLength(newBytes);
            string line = $"[span-reader-check] writer mismatch {Id(oldBytes.IsEmpty ? newBytes : oldBytes)} old len {oldBytes.Length}, new len {newBytes.Length}, first difference at {diff}; " +
                $"old {Hex(oldBytes, 256)}; new {Hex(newBytes, 256)}";
            Report(line, WriterMismatches);
        }

        private static void Report(string line, long count)
        {
            MismatchObserver?.Invoke(line);

            if (count <= 100)
            {
                Log.Warn(line);
            }
        }

        private static string Id(ReadOnlySpan<byte> packet)
        {
            if (packet.IsEmpty)
            {
                return "(empty)";
            }

            return packet[0] == 0xBF && packet.Length >= 5 ? $"0xBF.{packet[3] << 8 | packet[4]:X2}" : $"0x{packet[0]:X2}";
        }

        internal static string Hex(ReadOnlySpan<byte> bytes, int max)
        {
            int n = Math.Min(bytes.Length, max);
            var sb = new StringBuilder(n * 2 + 16);

            for (int i = 0; i < n; i++)
            {
                sb.Append(bytes[i].ToString("X2"));
            }

            if (n < bytes.Length)
            {
                sb.Append($"..(+{bytes.Length - n})");
            }

            return sb.ToString();
        }

        /// <summary>The switch state once, then the checker's 5 s summary and its coverage every 60 s and at exit (ParseQueue;
        /// forced at the end of a harness replay).</summary>
        public static void LogSummary(bool force = false)
        {
            if (!_announced)
            {
                _announced = true;
                Log.Info($"[span-reader] span_reader={(On ? "on" : "off")} check={(CheckOn ? "on" : "off")}");
            }

            if (!CheckOn || (!force && Time.Ticks < _logAt))
            {
                return;
            }

            if (!_exitHooked)
            {
                _exitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => LogCoverage();
            }

            _logAt = Time.Ticks + 5000;
            Log.Info($"[span-reader-check] {Packets} packets, {Reads} reads, {Writes} packets built; mismatches reader {ReaderMismatches}, writer {WriterMismatches} (span_reader={(On ? "on" : "off")})");

            if (force || Time.Ticks >= _coverageAt)
            {
                _coverageAt = Time.Ticks + 60000;
                LogCoverage();
            }
        }

        public static void LogCoverage()
        {
            var sb = new StringBuilder("[span-reader-check] coverage ids");

            for (int i = 0; i < 256; i++)
            {
                if (s_ids[i] > 0)
                {
                    sb.Append($" {i:X2}:{s_ids[i]}");
                }
            }

            sb.Append("; 0xBF subs");

            for (int i = 0; i < 256; i++)
            {
                if (s_bf[i] > 0)
                {
                    sb.Append($" {i:X2}:{s_bf[i]}");
                }
            }

            Log.Info(sb.ToString());
        }
#endif
    }
}
