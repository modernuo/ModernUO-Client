// SPDX-License-Identifier: BSD-2-Clause

using Microsoft.Xna.Framework.Audio;
using System;
using static System.String;

namespace ClassicUO.IO.Audio
{
    public abstract class Sound : IComparable<Sound>, IDisposable
    {
        private uint _lastPlayedTime;
        private string m_Name;
        private float m_volume = 1.0f;
        private float m_volumeFactor;


        protected Sound(string name, int index)
        {
            Name = name;
            Index = index;
        }

        public string Name
        {
            get => m_Name;
            private set
            {
                if (!IsNullOrEmpty(value))
                {
                    m_Name = value.Replace(".mp3", "");
                }
                else
                {
                    m_Name = Empty;
                }
            }
        }

        public int Index { get; }
        public double DurationTime { get; protected set; }

        public float Volume
        {
            get => m_volume;
            set
            {
                if (value < 0.0f)
                {
                    value = 0f;
                }
                else if (value > 1f)
                {
                    value = 1f;
                }

                m_volume = value;

                float instanceVolume = Math.Max(value - VolumeFactor, 0.0f);

                if (SoundInstance != null && !SoundInstance.IsDisposed)
                {
                    SoundInstance.Volume = instanceVolume;
                }
            }
        }

        public float VolumeFactor
        {
            get => m_volumeFactor;
            set
            {
                m_volumeFactor = value;
                Volume = m_volume;
            }
        }

        public bool IsPlaying(uint curTime) => SoundInstance != null && SoundInstance.State == SoundState.Playing && DurationTime > curTime;

        /// <summary>
        /// True while FNA holds a buffer of this sound (its instance is playing or paused): the renderer's sound cache
        /// never evicts such a sound, and AudioManager counts it against its limits.
        /// </summary>
        public bool IsActive => SoundInstance != null && !SoundInstance.IsDisposed && SoundInstance.State != SoundState.Stopped;

        /// <summary>False while <see cref="Play"/> would refuse because of the per-sound replay delay.</summary>
        public bool CanPlayAt(uint curTime) => _lastPlayedTime <= curTime;

        public int CompareTo(Sound other)
        {
            return other == null ? -1 : Index.CompareTo(other.Index);
        }

        public void Dispose()
        {
            OnDispose();

            if (SoundInstance != null)
            {
                SoundInstance.BufferNeeded -= OnBufferNeeded;

                if (!SoundInstance.IsDisposed)
                {
                    SoundInstance.Stop();
                    SoundInstance.Dispose();
                }

                SoundInstance = null;
            }
        }

        protected DynamicSoundEffectInstance SoundInstance;
        protected AudioChannels Channels = AudioChannels.Mono;
        protected uint Delay = 250;

        protected int Frequency = 22050;

        protected abstract ArraySegment<byte> GetBuffer();
        protected abstract void OnBufferNeeded(object sender, EventArgs e);

        protected virtual void AfterStop()
        {
        }

        protected virtual void OnDispose()
        {
        }

        protected virtual void BeforePlay()
        {
        }

        /// <summary>
        ///     Plays the effect.
        /// </summary>
        /// <param name="asEffect">Set to false for music, true for sound effects.</param>
        public virtual bool Play(uint curTime, float volume = 1.0f, float volumeFactor = 0.0f, bool spamCheck = false)
        {
            if (_lastPlayedTime > curTime)
            {
                return false;
            }

            BeforePlay();

            if (SoundInstance != null && !SoundInstance.IsDisposed)
            {
                SoundInstance.Stop();
            }
            else
            {
                SoundInstance = new DynamicSoundEffectInstance(Frequency, Channels);
            }


            var buffer = GetBuffer();

            if (buffer.Count > 0 && CanSubmit(buffer.Array, buffer.Offset, buffer.Count, Channels))
            {
                _lastPlayedTime = curTime + Delay;

                SoundInstance.BufferNeeded += OnBufferNeeded;
                SoundInstance.SubmitBuffer(buffer.Array, buffer.Offset, buffer.Count);
                VolumeFactor = volumeFactor;
                Volume = volume;

                DurationTime = curTime + SoundInstance.GetSampleDuration(buffer.Count).TotalMilliseconds;

                SoundInstance.Play();

                return true;
            }

            return false;
        }

        /// <summary>
        /// DynamicSoundEffectInstance.SubmitBuffer's argument rules (it throws ArgumentException otherwise): the whole
        /// array's length, the offset and the count are multiples of the block alignment (channels x 2 bytes for 16-bit),
        /// the array and count are non-empty, and offset + count lies inside the array. A buffer that fails is not played.
        /// </summary>
        public static bool CanSubmit(byte[] array, int offset, int count, AudioChannels channels)
        {
            int align = 2 * (int) channels;

            return array != null && array.Length > 0 && array.Length % align == 0
                && (uint) offset < (uint) array.Length && offset % align == 0
                && count > 0 && (uint) (offset + count) <= (uint) array.Length && count % align == 0;
        }

        public void Stop()
        {
            if (SoundInstance != null)
            {
                SoundInstance.BufferNeeded -= OnBufferNeeded;
                SoundInstance.Stop();
            }

            AfterStop();
        }
    }
}
