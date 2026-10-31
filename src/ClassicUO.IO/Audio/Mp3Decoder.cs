// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Utility.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClassicUO.IO.Audio
{
    /// <summary>
    ///     MP3 decoder over dr_mp3 (native cuo_mp3). Owns a native copy of the MP3 bytes, so the source buffer or
    ///     stream can go away after <see cref="TryOpen(ReadOnlySpan{byte}, out Mp3Decoder, out string)" />. Output is
    ///     interleaved signed 16-bit PCM. Gapless: the LAME delay is skipped and the padding trimmed by dr_mp3, so
    ///     looping with <see cref="Seek" />(0) at the end of the stream is sample-accurate when the track has a LAME tag.
    ///     Meant for one thread at a time, but Read, Seek, TotalFrames and Dispose are serialised, so a Dispose from
    ///     another thread waits for a native call in progress and later calls throw <see cref="ObjectDisposedException" />.
    ///     The native decoder and its input bytes are owned by one <see cref="SafeHandle" />, which is held
    ///     (DangerousAddRef) across every native call, so neither can be freed or finalised under the decoder.
    ///     The release entry point (cuo_mp3_close) is resolved before any native allocation, so a library without it
    ///     fails the open instead of leaking; release itself never throws (it can run on the finaliser thread).
    /// </summary>
    public sealed unsafe class Mp3Decoder : IDisposable
    {
        private readonly Mp3Handle _owner;
        private readonly object _sync = new object();
        private long _frameCount = -1;

        private Mp3Decoder(Mp3Handle owner, int dataLength, in Mp3Native.Info info)
        {
            _owner = owner;
            DataLength = dataLength;
            SampleRate = (int)info.SampleRate;
            Channels = (int)info.Channels;
            DelayFrames = (int)info.DelayFrames;
            PaddingFrames = (int)info.PaddingFrames;
            HasXingFrameCount = info.RawFrames != ulong.MaxValue;
        }

        private static int s_Available; // 0 unknown, 1 yes, -1 no
        private static int s_CloseResolved; // 0 unknown, 1 resolved, -1 missing (cached for the real library only)
        private static long s_LiveInputs;
        private static long s_CloseFailures;

        /// <summary>Test hook: replaces cuo_mp3_open_memory (e.g. to force a load failure). Null in production.</summary>
        internal static Func<nint, nuint, nint> OpenMemoryOverride;

        /// <summary>
        ///     Test hook: replaces cuo_mp3_close (e.g. to simulate a library without the export). The pre-allocation
        ///     probe calls it with 0, which cuo_mp3_close treats as a no-op. Null in production.
        /// </summary>
        internal static Action<nint> CloseOverride;

        /// <summary>Native closes that threw during release (the input copy is still freed). Diagnostics and tests.</summary>
        internal static long CloseFailures => Interlocked.Read(ref s_CloseFailures);

        /// <summary>Input copies currently allocated (each is freed by a failed open or by the owning handle).</summary>
        internal static long LiveInputAllocations => Interlocked.Read(ref s_LiveInputs);

        /// <summary>
        ///     False when the native cuo_mp3 library can't be loaded (e.g. a Linux/macOS build made without running
        ///     src/native/cuo_mp3/build-linux.sh or build-macos.sh). Probed once; the first failure logs one error and music stays off.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                int state = Volatile.Read(ref s_Available);

                if (state == 0)
                {
                    try
                    {
                        Mp3Native.Version();
                        Mp3Native.Close(0); // binds the release entry point too (a no-op on null)
                        state = 1;
                    }
                    catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
                    {
                        state = -1;

                        if (Interlocked.CompareExchange(ref s_Available, state, 0) == 0)
                        {
                            Log.Error($"music disabled: the native MP3 decoder ({Mp3Native.Library}) could not be loaded: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    Interlocked.CompareExchange(ref s_Available, state, 0);
                    state = Volatile.Read(ref s_Available);
                }

                return state == 1;
            }
        }

        public int SampleRate { get; }
        public int Channels { get; }

        /// <summary>LAME encoder delay + 529 (skipped by the decoder); 0 without a LAME tag.</summary>
        public int DelayFrames { get; }

        /// <summary>LAME padding - 529 (trimmed by the decoder); 0 without a LAME tag.</summary>
        public int PaddingFrames { get; }

        public bool HasXingFrameCount { get; }
        public int DataLength { get; }

        /// <summary>
        ///     Audible PCM frames. From the Xing tag when present, else a header scan on first use (linear time).
        /// </summary>
        public long TotalFrames
        {
            get
            {
                lock (_sync)
                {
                    // Checked before the cache, so a count read earlier isn't returned after Dispose either.
                    if (_owner.IsClosed)
                    {
                        throw new ObjectDisposedException(nameof(Mp3Decoder));
                    }

                    if (_frameCount < 0)
                    {
                        nint h = Enter();

                        try
                        {
                            _frameCount = (long)Mp3Native.GetFrameCount(h);
                        }
                        finally
                        {
                            _owner.DangerousRelease();
                        }
                    }

                    return _frameCount;
                }
            }
        }

        public static string NativeVersion
        {
            get
            {
                uint v = Mp3Native.Version();

                return $"{v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF}";
            }
        }

        /// <summary>Validates and opens MP3 bytes (copied). Never throws for bad data: returns false with a reason.</summary>
        public static bool TryOpen(ReadOnlySpan<byte> mp3, out Mp3Decoder decoder, out string error)
        {
            decoder = null;

            if (mp3.Length == 0 || mp3.Length > Mp3Validator.MaxBytes)
            {
                error = mp3.Length == 0 ? "empty" : $"size {mp3.Length} exceeds {Mp3Validator.MaxBytes}";

                return false;
            }

            if (!CloseResolved(out error))
            {
                return false;
            }

            byte* copy = AllocInput(mp3.Length);
            mp3.CopyTo(new Span<byte>(copy, mp3.Length));

            return TryOpenOwned(copy, mp3.Length, out decoder, out error);
        }

        /// <summary>Reads the rest of the stream (at most <see cref="Mp3Validator.MaxBytes" />) and opens it.</summary>
        public static bool TryOpen(Stream stream, out Mp3Decoder decoder, out string error)
        {
            decoder = null;

            if (!CloseResolved(out error))
            {
                return false;
            }

            try
            {
                if (!stream.CanSeek)
                {
                    using MemoryStream ms = new MemoryStream();
                    byte[] chunk = new byte[81920];
                    int n;

                    while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (ms.Length + n > Mp3Validator.MaxBytes)
                        {
                            error = $"size exceeds {Mp3Validator.MaxBytes}";

                            return false;
                        }

                        ms.Write(chunk, 0, n);
                    }

                    return TryOpen(new ReadOnlySpan<byte>(ms.GetBuffer(), 0, (int)ms.Length), out decoder, out error);
                }

                long length = stream.Length - stream.Position;

                if (length <= 0 || length > Mp3Validator.MaxBytes)
                {
                    error = length <= 0 ? "empty" : $"size {length} exceeds {Mp3Validator.MaxBytes}";

                    return false;
                }

                byte* data = AllocInput((int)length);

                try
                {
                    stream.ReadExactly(new Span<byte>(data, (int)length));
                }
                catch
                {
                    FreeInput(data);

                    throw;
                }

                return TryOpenOwned(data, (int)length, out decoder, out error);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = ex.Message;

                return false;
            }
        }

        // Resolves cuo_mp3_close (by calling it with null, a no-op) before anything native is allocated, so a library
        // that exports open but not close fails the open with an error instead of leaving what nothing can release.
        private static bool CloseResolved(out string error)
        {
            Action<nint> hook = CloseOverride;

            if (hook == null && Volatile.Read(ref s_CloseResolved) == 1)
            {
                error = null;

                return true;
            }

            try
            {
                if (hook != null)
                {
                    hook(0);
                }
                else
                {
                    Mp3Native.Close(0);
                    Volatile.Write(ref s_CloseResolved, 1);
                }

                error = null;

                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
            {
                error = $"native MP3 decoder ({Mp3Native.Library}) has no usable release entry point: {ex.GetType().Name}: {ex.Message}";

                if (hook == null && Interlocked.Exchange(ref s_CloseResolved, -1) != -1)
                {
                    Log.Error(error);
                }

                return false;
            }
        }

        private static void NativeClose(nint handle)
        {
            Action<nint> hook = CloseOverride;

            if (hook != null)
            {
                hook(handle);
            }
            else
            {
                Mp3Native.Close(handle);
            }
        }

        private static byte* AllocInput(int length)
        {
            byte* p = (byte*)NativeMemory.Alloc((nuint)length);
            Interlocked.Increment(ref s_LiveInputs);

            return p;
        }

        private static void FreeInput(byte* p)
        {
            NativeMemory.Free(p);
            Interlocked.Decrement(ref s_LiveInputs);
        }

        // Takes ownership of data (from AllocInput). Until an Mp3Handle owns it, the finally frees it and closes the
        // native decoder on every exit, including exceptions from the native load (DllNotFoundException etc.).
        private static bool TryOpenOwned(byte* data, int length, out Mp3Decoder decoder, out string error)
        {
            decoder = null;
            nint handle = 0;
            bool owned = false;

            try
            {
                Mp3Validator.Result check = Mp3Validator.Validate(new ReadOnlySpan<byte>(data, length));

                if (!check.IsValid)
                {
                    error = check.Error;

                    return false;
                }

                Func<nint, nuint, nint> open = OpenMemoryOverride;
                handle = open != null ? open((nint)data, (nuint)length) : Mp3Native.OpenMemory(data, (nuint)length);

                if (handle == 0)
                {
                    error = "dr_mp3 rejected the stream";

                    return false;
                }

                Mp3Native.Info info;

                if (Mp3Native.GetInfo(handle, &info) == 0)
                {
                    error = "no stream info";

                    return false;
                }

                Mp3Handle owner = new Mp3Handle(handle, data);
                owned = true;
                decoder = new Mp3Decoder(owner, length, info);
                error = null;

                return true;
            }
            finally
            {
                if (!owned)
                {
                    try
                    {
                        if (handle != 0)
                        {
                            NativeClose(handle);
                        }
                    }
                    finally
                    {
                        FreeInput(data);
                    }
                }
            }
        }

        public static bool TryOpenFile(string path, out Mp3Decoder decoder, out string error)
        {
            decoder = null;

            try
            {
                using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);

                return TryOpen(fs, out decoder, out error);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = ex.Message;

                return false;
            }
        }

        /// <summary>Decodes up to output.Length / Channels frames. Returns frames written; 0 at the end of the stream.</summary>
        public int Read(Span<short> output)
        {
            int frames = output.Length / Channels;

            lock (_sync)
            {
                nint h = Enter();

                try
                {
                    if (frames == 0)
                    {
                        return 0;
                    }

                    fixed (short* p = output)
                    {
                        return (int)Mp3Native.ReadS16(h, (ulong)frames, p);
                    }
                }
                finally
                {
                    _owner.DangerousRelease();
                }
            }
        }

        /// <summary>Seeks to an audible PCM frame (0 = the first frame after the encoder delay).</summary>
        public bool Seek(long frame)
        {
            lock (_sync)
            {
                nint h = Enter();

                try
                {
                    return frame >= 0 && Mp3Native.Seek(h, (ulong)frame) != 0;
                }
                finally
                {
                    _owner.DangerousRelease();
                }
            }
        }

        public bool IsDisposed => _owner.IsClosed;

        /// <summary>Idempotent and thread-safe; waits for a Read/Seek in progress on another thread.</summary>
        public void Dispose()
        {
            lock (_sync)
            {
                _owner.Dispose();
            }
        }

        // Under _sync: throws once disposed, else holds a reference on the handle until the caller's DangerousRelease,
        // so the SafeHandle (and with it the decoder and input bytes) can't be released or finalised mid-call.
        private nint Enter()
        {
            bool added = false;

            try
            {
                _owner.DangerousAddRef(ref added);
            }
            catch (ObjectDisposedException)
            {
                throw new ObjectDisposedException(nameof(Mp3Decoder));
            }

            return _owner.DangerousGetHandle();
        }

        /// <summary>Owns the dr_mp3 decoder and its input bytes; releases both exactly once (Dispose or finaliser).</summary>
        private sealed class Mp3Handle : SafeHandle
        {
            private byte* _data;

            public Mp3Handle(nint handle, byte* data) : base(0, true)
            {
                SetHandle(handle);
                _data = data;
            }

            public override bool IsInvalid => handle == 0;

            // Never throws (it can run on the finaliser thread): a failing close is counted and the input is still freed.
            protected override bool ReleaseHandle()
            {
                bool closed = true;

                try
                {
                    NativeClose(handle);
                }
                catch
                {
                    closed = false;
                    Interlocked.Increment(ref s_CloseFailures);
                }
                finally
                {
                    if (_data != null)
                    {
                        FreeInput(_data);
                        _data = null;
                    }
                }

                return closed;
            }
        }
    }
}
