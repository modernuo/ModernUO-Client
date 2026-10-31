// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework.Audio;
using System;
using System.Diagnostics;

namespace ClassicUO.IO.Audio
{
    /// <summary>
    ///     A music track. Decoding (dr_mp3) runs on <see cref="MusicDecodeWorker" />; <see cref="Update" /> on the game
    ///     thread only submits buffers the worker already decoded. Play returns at once; the sound starts within a frame
    ///     or two, once the worker has opened the file and decoded the first buffer.
    /// </summary>
    public class UOMusic : Sound
    {
        /// <summary>Buffers kept queued in FAudio (about 0.56 s at 44.1 kHz, 1.1 s at 22.05 kHz).</summary>
        private const int SubmittedTarget = 3;

        private readonly bool m_Repeat;
        private MusicStream m_Stream;
        private bool m_Started;
        private uint m_PlayTicks;
        private int m_LoopsLogged;

        /// <param name="open">Opens the MP3 bytes (a pack record stream); called on the music worker for each play.</param>
        /// <param name="source">Where the bytes come from, for the log.</param>
        public UOMusic(int index, string name, bool loop, Func<System.IO.Stream> open, string source) : base(name, index)
        {
            m_Repeat = loop;
            Channels = AudioChannels.Stereo;
            Delay = 0;

            m_Open = open;
            Path = source;
        }

        private readonly Func<System.IO.Stream> m_Open;

        private string Path { get; }

        /// <summary>Time from Play to the first submitted buffer, for the latest play of this track (ms).</summary>
        public double FirstBufferMilliseconds { get; private set; }

        public override bool Play(uint curTime, float volume = 1.0f, float volumeFactor = 0.0f, bool spamCheck = false)
        {
            Stop();

            if (!Mp3Decoder.IsAvailable)
            {
                return false; // logged once by Mp3Decoder.IsAvailable; music stays off
            }

            m_Stream = new MusicStream(m_Open, Path, Name, m_Repeat);
            m_Started = false;
            m_LoopsLogged = 0;
            m_PlayTicks = curTime;
            VolumeFactor = volumeFactor;
            Volume = volume;
            MusicDecodeWorker.Add(m_Stream);

            return true;
        }

        public void Update()
        {
            MusicStream stream = m_Stream;

            if (stream == null)
            {
                return;
            }

            if (stream.IsFailed)
            {
                Log.Warn($"music {Name}: skipped ({stream.Error}) [{Path}]");
                Stop();

                return;
            }

            if (!stream.IsOpened)
            {
                return;
            }

            if (SoundInstance == null || SoundInstance.IsDisposed || Frequency != stream.SampleRate || (int)Channels != stream.Channels)
            {
                DisposeInstance();
                Frequency = stream.SampleRate;
                Channels = (AudioChannels)stream.Channels;
                SoundInstance = new DynamicSoundEffectInstance(Frequency, Channels);
                Volume = Volume; // apply the stored volume to the new instance
            }

            int firstBytes = 0;
            bool submitted = false;

            while (SoundInstance.PendingBufferCount < SubmittedTarget && stream.TryDequeue(out byte[] buffer, out int bytes))
            {
                SoundInstance.SubmitBuffer(buffer, 0, bytes);
                stream.Recycle(buffer);

                if (!submitted)
                {
                    firstBytes = bytes;
                    submitted = true;
                }
            }

            if (stream.Loops != m_LoopsLogged)
            {
                m_LoopsLogged = stream.Loops;
                Log.Trace($"music {Name}: looped ({m_LoopsLogged})");
            }

            if (submitted)
            {
                MusicDecodeWorker.Signal();

                if (!m_Started)
                {
                    m_Started = true;
                    SoundInstance.Play();

                    // Same as the old code: IsPlaying covers the first buffer's duration.
                    DurationTime = m_PlayTicks + SoundInstance.GetSampleDuration(firstBytes).TotalMilliseconds;
                    FirstBufferMilliseconds = Stopwatch.GetElapsedTime(stream.Created).TotalMilliseconds;
                    Log.Trace($"music {Name}: first buffer after {FirstBufferMilliseconds:F1} ms (open {stream.OpenMilliseconds:F1} ms)");
                }
            }
            else if (stream.IsEnded && stream.ReadyCount == 0 && SoundInstance.PendingBufferCount == 0)
            {
                Stop();
            }
        }

        protected override ArraySegment<byte> GetBuffer() => ArraySegment<byte>.Empty;

        protected override void OnBufferNeeded(object sender, EventArgs e)
        {
            // Not subscribed: Update (every frame) keeps FAudio fed from the worker's ready buffers.
        }

        protected override void AfterStop()
        {
            m_Stream?.Cancel();
            m_Stream = null;
            m_Started = false;
        }

        protected override void OnDispose()
        {
            m_Stream?.Cancel();
            m_Stream = null;
        }

        private void DisposeInstance()
        {
            if (SoundInstance != null)
            {
                if (!SoundInstance.IsDisposed)
                {
                    SoundInstance.Stop();
                    SoundInstance.Dispose();
                }

                SoundInstance = null;
            }
        }
    }
}
