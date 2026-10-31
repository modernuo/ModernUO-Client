// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Utility.Logging;
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

        /// <summary>
        /// False when the instance's volume (<see cref="Volume"/> less <see cref="VolumeFactor"/>, as the setter applies it)
        /// is 0: such a sound gives way to audible ones in AudioManager's playing limits.
        /// </summary>
        public bool IsAudible => m_volume - m_volumeFactor > 0f;

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

            UnsubscribeBufferNeeded();

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
            // FNA's volume setter throws on NaN, after the buffer was submitted (Codex round 3, P3): refused before any
            // playback state changes
            if (!float.IsFinite(volume) || !float.IsFinite(volumeFactor))
            {
                Log.Warn($"{Name}: volume {volume} or factor {volumeFactor} is not a number; not played");

                return false;
            }

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

                SubscribeBufferNeeded();
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

        // The instance BufferNeeded is subscribed on (null: none). A replay used to add a subscription each time, and
        // AudioManager's limiter drops a replayed sound from its list without calling Stop (FNA's own Stop keeps the
        // event), so N overlapping replays left N subscriptions and one cleanup removed one (Codex round 2, P2). The
        // subscription is now owned here, independent of any list: at most one, whatever plays, stops or replays.
        private DynamicSoundEffectInstance _subscribedTo;

        /// <summary>Subscribes <see cref="OnBufferNeeded"/> to the current instance, once (idempotent).</summary>
        internal void SubscribeBufferNeeded()
        {
            if (_subscribedTo == SoundInstance)
            {
                return;
            }

            UnsubscribeBufferNeeded();

            if (SoundInstance != null)
            {
                SoundInstance.BufferNeeded += OnBufferNeeded;
                _subscribedTo = SoundInstance;
            }
        }

        /// <summary>Removes the subscription, if any (idempotent).</summary>
        internal void UnsubscribeBufferNeeded()
        {
            if (_subscribedTo != null)
            {
                _subscribedTo.BufferNeeded -= OnBufferNeeded;
                _subscribedTo = null;
            }
        }

        public void Stop()
        {
            UnsubscribeBufferNeeded();

            if (SoundInstance != null)
            {
                SoundInstance.Stop();
            }

            AfterStop();
        }
    }
}
