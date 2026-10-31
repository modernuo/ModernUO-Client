// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ClassicUO.IO.Audio
{
    /// <summary>
    ///     MP3 decoder over dr_mp3 (native cuo_mp3). Owns a native copy of the MP3 bytes, so the source buffer or
    ///     stream can go away after <see cref="TryOpen(ReadOnlySpan{byte}, out Mp3Decoder, out string)" />. Output is
    ///     interleaved signed 16-bit PCM. Gapless: the LAME delay is skipped and the padding trimmed by dr_mp3, so
    ///     looping with <see cref="Seek" />(0) at the end of the stream is sample-accurate when the track has a LAME tag.
    ///     Not thread-safe: use one instance from one thread at a time.
    /// </summary>
    public sealed unsafe class Mp3Decoder : IDisposable
    {
        private nint _handle;
        private byte* _data;
        private long _frameCount = -1;

        private Mp3Decoder(nint handle, byte* data, int dataLength, in Mp3Native.Info info)
        {
            _handle = handle;
            _data = data;
            DataLength = dataLength;
            SampleRate = (int)info.SampleRate;
            Channels = (int)info.Channels;
            DelayFrames = (int)info.DelayFrames;
            PaddingFrames = (int)info.PaddingFrames;
            HasXingFrameCount = info.RawFrames != ulong.MaxValue;
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
                if (_frameCount < 0)
                {
                    ThrowIfDisposed();
                    _frameCount = (long)Mp3Native.GetFrameCount(_handle);
                }

                return _frameCount;
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

            byte* copy = (byte*)NativeMemory.Alloc((nuint)mp3.Length);
            mp3.CopyTo(new Span<byte>(copy, mp3.Length));

            return TryOpenOwned(copy, mp3.Length, out decoder, out error);
        }

        /// <summary>Reads the rest of the stream (at most <see cref="Mp3Validator.MaxBytes" />) and opens it.</summary>
        public static bool TryOpen(Stream stream, out Mp3Decoder decoder, out string error)
        {
            decoder = null;

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

                byte* data = (byte*)NativeMemory.Alloc((nuint)length);

                try
                {
                    stream.ReadExactly(new Span<byte>(data, (int)length));
                }
                catch
                {
                    NativeMemory.Free(data);

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

        // Takes ownership of data (NativeMemory): freed on failure or by Dispose.
        private static bool TryOpenOwned(byte* data, int length, out Mp3Decoder decoder, out string error)
        {
            decoder = null;
            Mp3Validator.Result check = Mp3Validator.Validate(new ReadOnlySpan<byte>(data, length));

            if (!check.IsValid)
            {
                NativeMemory.Free(data);
                error = check.Error;

                return false;
            }

            nint handle = Mp3Native.OpenMemory(data, (nuint)length);

            if (handle == 0)
            {
                NativeMemory.Free(data);
                error = "dr_mp3 rejected the stream";

                return false;
            }

            Mp3Native.Info info;

            if (Mp3Native.GetInfo(handle, &info) == 0)
            {
                Mp3Native.Close(handle);
                NativeMemory.Free(data);
                error = "no stream info";

                return false;
            }

            decoder = new Mp3Decoder(handle, data, length, info);
            error = null;

            return true;
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
            ThrowIfDisposed();
            int frames = output.Length / Channels;

            if (frames == 0)
            {
                return 0;
            }

            fixed (short* p = output)
            {
                return (int)Mp3Native.ReadS16(_handle, (ulong)frames, p);
            }
        }

        /// <summary>Seeks to an audible PCM frame (0 = the first frame after the encoder delay).</summary>
        public bool Seek(long frame)
        {
            ThrowIfDisposed();

            return frame >= 0 && Mp3Native.Seek(_handle, (ulong)frame) != 0;
        }

        public void Dispose()
        {
            if (_handle != 0)
            {
                Mp3Native.Close(_handle);
                _handle = 0;
            }

            if (_data != null)
            {
                NativeMemory.Free(_data);
                _data = null;
            }

            GC.SuppressFinalize(this);
        }

        ~Mp3Decoder()
        {
            Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_handle == 0)
            {
                throw new ObjectDisposedException(nameof(Mp3Decoder));
            }
        }
    }
}
