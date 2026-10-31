using ClassicUO.Assets;
using ClassicUO.IO.Audio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ClassicUO.Renderer.Sounds
{
    public sealed class Sound
    {
        const int MAX_SOUND_DATA_INDEX_COUNT = 0xFFFF;

        private readonly IO.Audio.Sound[] _musics = new IO.Audio.Sound[MAX_SOUND_DATA_INDEX_COUNT];
        private readonly IO.Audio.Sound[] _sounds = new IO.Audio.Sound[MAX_SOUND_DATA_INDEX_COUNT];
        private readonly SoundsLoader _soundsLoader;

        public Sound(SoundsLoader soundsLoader)
        {
            _soundsLoader = soundsLoader;
        }

        public void Reload()
        {
            _soundsLoader.LoadOurs();
            Array.Clear(_sounds);
        }

        public IO.Audio.Sound GetSound(int index)
        {
            if (index >= 0 && index < MAX_SOUND_DATA_INDEX_COUNT)
            {
                ref IO.Audio.Sound sound = ref _sounds[index];

                if (sound == null)
                {
                    // first play of this id: one positional read of the pack record (or a loose WAV); cached after
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

                    if (_soundsLoader.TryGetSound(index, out ArraySegment<byte> data, out string name))
                    {
                        sound = new UOSound(name, index, data);
                        ClassicUO.Utility.Logging.Log.Trace(
                            $"sound {index} ({name}): first read {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0.000} ms, {data.Count} bytes");
                    }
                }

                return sound;
            }

            return null;
        }

        public IO.Audio.Sound GetMusic(int index)
        {
            if (index >= 0 && index < MAX_SOUND_DATA_INDEX_COUNT)
            {
                ref IO.Audio.Sound music = ref _musics[index];

                // the MP3 bytes come from the pack's music record, opened on the music worker
                if (music == null && _soundsLoader.TryGetMusic(index, out string name, out bool loop, out Func<Stream> open))
                {
                    music = new UOMusic(index, name, loop, open, $"pack music {index}");
                }

                return music;
            }

            return null;
        }
    }
}
