// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Utility.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClassicUO.IO.Audio
{
    /// <summary>
    ///     One playing music track: the decoder lives on <see cref="MusicDecodeWorker" />'s thread, which fills PCM
    ///     buffers ahead; the game thread only dequeues ready buffers, submits them and hands them back. Looping seeks
    ///     to frame 0 inside a buffer fill, so the loop seam is sample-continuous (gapless when the track has a LAME tag,
    ///     because dr_mp3 skips the delay and trims the padding).
    /// </summary>
    public sealed class MusicStream
    {
        /// <summary>PCM frames per buffer: 8192 frames = 32 KB stereo, 0.19 s at 44.1 kHz (the old chunk size).</summary>
        public const int FramesPerBuffer = 8192;

        /// <summary>Decoded buffers kept ready ahead of the game thread (on top of what FAudio has queued).</summary>
        public const int ReadyTarget = 4;

        private readonly ConcurrentQueue<byte[]> _ready = new ConcurrentQueue<byte[]>();
        private readonly ConcurrentQueue<byte[]> _free = new ConcurrentQueue<byte[]>();
        private readonly Dictionary<byte[], int> _lengths = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        private Mp3Decoder _decoder;
        private int _allocated;
        private volatile int _loops;
        private volatile bool _cancelled, _opened, _failed, _ended;

        /// <summary>A track read from a file (tests and tools).</summary>
        public MusicStream(string path, string name, bool loop) : this(null, path, name, loop)
        {
        }

        /// <summary>
        ///     A track whose MP3 bytes <paramref name="open" /> returns (the client: a positional stream over the pack's
        ///     music record). The worker calls it once per play, reads the bytes into native memory, validates them
        ///     (Mp3Validator) and disposes the stream. Null: <paramref name="path" /> is a file.
        /// </summary>
        public MusicStream(Func<System.IO.Stream> open, string path, string name, bool loop)
        {
            _open = open;
            Path = path;
            Name = name;
            Loop = loop;
            Created = Stopwatch.GetTimestamp();
        }

        private readonly Func<System.IO.Stream> _open;

        public string Path { get; }
        public string Name { get; }
        public bool Loop { get; }
        public long Created { get; }
        public bool IsOpened => _opened;
        public bool IsFailed => _failed;
        public bool IsCancelled => _cancelled;

        /// <summary>True once the decoder reached the end of a non-looping track and queued its last buffer.</summary>
        public bool IsEnded => _ended;

        public string Error { get; private set; }
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public double OpenMilliseconds { get; private set; }
        public int ReadyCount => _ready.Count;

        /// <summary>How many times a looping track wrapped to its start.</summary>
        public int Loops => _loops;

        /// <summary>Test hook: runs on the worker after each buffer fill, before the buffer is queued.</summary>
        internal Action<MusicStream> TestAfterFill;

        /// <summary>Game thread: next decoded buffer and its byte length, or false if none is ready.</summary>
        public bool TryDequeue(out byte[] buffer, out int bytes)
        {
            if (_ready.TryDequeue(out buffer))
            {
                lock (_lengths)
                {
                    bytes = _lengths[buffer];
                }

                return true;
            }

            bytes = 0;

            return false;
        }

        /// <summary>Game thread: returns a buffer after SubmitBuffer (FNA copies the data).</summary>
        public void Recycle(byte[] buffer) => _free.Enqueue(buffer);

        /// <summary>Game thread: stop decoding; the worker disposes the decoder.</summary>
        public void Cancel()
        {
            _cancelled = true;
            MusicDecodeWorker.Signal();
        }

        /// <summary>Worker thread: open if needed and decode ahead. Returns false when the stream is finished.</summary>
        internal bool Pump()
        {
            if (_cancelled || _failed)
            {
                Release();

                return false;
            }

            try
            {
                if (!_opened)
                {
                    if (!Mp3Decoder.IsAvailable)
                    {
                        Fail("native MP3 decoder unavailable");

                        return false;
                    }

                    long t0 = Stopwatch.GetTimestamp();

                    string error;
                    bool opened;

                    if (_open != null)
                    {
                        using System.IO.Stream source = _open();
                        opened = Mp3Decoder.TryOpen(source, out _decoder, out error);
                    }
                    else
                    {
                        opened = Mp3Decoder.TryOpenFile(Path, out _decoder, out error);
                    }

                    if (!opened)
                    {
                        Fail(error);

                        return false;
                    }

                    SampleRate = _decoder.SampleRate;
                    Channels = _decoder.Channels;
                    OpenMilliseconds = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    _opened = true;
                }

                bool ended = false;

                while (!ended && !_cancelled && _ready.Count < ReadyTarget)
                {
                    if (!_free.TryDequeue(out byte[] buffer))
                    {
                        if (_allocated >= ReadyTarget + 2)
                        {
                            break; // the game thread still holds the rest
                        }

                        buffer = new byte[FramesPerBuffer * Channels * sizeof(short)];
                        _allocated++;
                    }

                    int frames = Fill(buffer, out ended);
                    TestAfterFill?.Invoke(this);

                    if (frames > 0)
                    {
                        lock (_lengths)
                        {
                            _lengths[buffer] = frames * Channels * sizeof(short);
                        }

                        _ready.Enqueue(buffer);
                    }
                    else
                    {
                        _free.Enqueue(buffer);
                    }
                }

                // Published only after the last buffer is in _ready: the game thread stops the track once it sees
                // IsEnded with nothing ready and nothing queued in FAudio, so an earlier flag could drop the tail.
                if (ended)
                {
                    _ended = true;
                }
            }
            catch (Exception ex)
            {
                Fail(ex.Message);

                return false;
            }

            if (_cancelled)
            {
                Release();

                return false;
            }

            if (_ended)
            {
                Release(); // the decoder is no longer needed; ready buffers stay for the game thread

                return false;
            }

            return true;
        }

        // Fills one buffer; on a looping track wraps to frame 0 mid-buffer (the old code dropped the wrapped part).
        private int Fill(byte[] buffer, out bool ended)
        {
            ended = false;
            Span<short> pcm = MemoryMarshal.Cast<byte, short>(buffer.AsSpan());
            int want = pcm.Length / Channels;
            int got = 0;
            bool justSeeked = false;

            while (got < want)
            {
                int n = _decoder.Read(pcm.Slice(got * Channels));
                got += n;

                if (got == want)
                {
                    break;
                }

                // Short read: end of stream (or undecodable data, which dr_mp3 treats the same way). Nothing at all
                // right after a seek to 0 means the track has no decodable audio: stop instead of spinning.
                if (!Loop || justSeeked && n == 0 || !_decoder.Seek(0))
                {
                    ended = true;

                    break;
                }

                justSeeked = true;
                _loops++;
            }

            return got;
        }

        private void Fail(string error)
        {
            Error = error;
            _failed = true;
            Release();
        }

        private void Release()
        {
            _decoder?.Dispose();
            _decoder = null;
        }
    }

    /// <summary>
    ///     One background thread that opens and decodes every playing <see cref="MusicStream" />, so no MP3 work runs on
    ///     the game thread. Woken by <see cref="Signal" /> (a buffer was consumed or a stream started/stopped) and every
    ///     50 ms as a fallback.
    /// </summary>
    public static class MusicDecodeWorker
    {
        private static readonly List<MusicStream> s_Streams = new List<MusicStream>();
        private static readonly AutoResetEvent s_Wake = new AutoResetEvent(false);
        private static Thread s_Thread;

        public static void Add(MusicStream stream)
        {
            lock (s_Streams)
            {
                s_Streams.Add(stream);

                if (s_Thread == null)
                {
                    s_Thread = new Thread(Run) { IsBackground = true, Name = "Music decode", Priority = ThreadPriority.AboveNormal };
                    s_Thread.Start();
                }
            }

            s_Wake.Set();
        }

        public static void Signal() => s_Wake.Set();

        private static void Run()
        {
            List<MusicStream> snapshot = new List<MusicStream>();

            while (true)
            {
                s_Wake.WaitOne(50);

                snapshot.Clear();

                lock (s_Streams)
                {
                    snapshot.AddRange(s_Streams);
                }

                foreach (MusicStream s in snapshot)
                {
                    bool keep;

                    try
                    {
                        keep = s.Pump();
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"music decode: {s.Name}: {ex}");
                        keep = false;
                    }

                    if (!keep)
                    {
                        lock (s_Streams)
                        {
                            s_Streams.Remove(s);
                        }
                    }
                }
            }
        }
    }
}
