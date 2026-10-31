using ClassicUO.Assets;
using ClassicUO.IO.Audio;
using ClassicUO.Utility.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ClassicUO.Renderer.Sounds
{
    public sealed class Sound
    {
        const int MAX_SOUND_DATA_INDEX_COUNT = 0xFFFF;

        /// <summary>
        /// Most PCM the sound cache holds (distinct arrays: aliases of one record share one). 64 MiB = AudioConst.MaxSoundBytes,
        /// so the largest sound the format allows still fits alone. Stock 7.0.103 has 160.4 MiB of PCM in 1,655 records,
        /// median 55 KB, p99 447 KB; only three are over 1 MiB (8.2, 9.8 and 20.2 MiB). A long session that plays 300
        /// distinct typical sounds holds about 16 MiB; bench-000050's whole plan played 7 ids.
        /// </summary>
        public const long SoundCacheBudget = 64L << 20;

        /// <summary>Most sound ids cached at once (each may hold an FAudio voice once played).</summary>
        public const int SoundCacheMaxIds = 1024;

        /// <summary>Most sound effects playing at once; the oldest is stopped to make room (ClassicUO had no limit).</summary>
        public const int MaxPlayingSounds = 32;

        /// <summary>Most PCM submitted to FNA at once (FNA copies each play's buffer into native memory until it ends).</summary>
        public const long MaxPlayingBytes = 64L << 20;

        private readonly IO.Audio.Sound[] _musics = new IO.Audio.Sound[MAX_SOUND_DATA_INDEX_COUNT];
        private readonly SoundsLoader _soundsLoader;

        public Sound(SoundsLoader soundsLoader)
        {
            _soundsLoader = soundsLoader;
            Cache = new SoundCache(soundsLoader, SoundCacheBudget, SoundCacheMaxIds);
        }

        /// <summary>The sound effects played so far, within <see cref="SoundCacheBudget"/>.</summary>
        public SoundCache Cache { get; }

        public void Reload()
        {
            _soundsLoader.LoadOurs();
            Cache.Clear();
        }

        public IO.Audio.Sound GetSound(int index)
        {
            if (index >= 0 && index < MAX_SOUND_DATA_INDEX_COUNT)
            {
                return Cache.Get(index);
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

        /// <summary>
        /// Makes room to play <paramref name="next"/>: drops it from <paramref name="playing"/> (a replay restarts it), then
        /// stops and removes the oldest sounds until fewer than <paramref name="maxCount"/> remain and their PCM plus
        /// <paramref name="next"/>'s fits in <paramref name="maxBytes"/>. Returns how many were stopped.
        /// </summary>
        public static int LimitPlaying(LinkedList<UOSound> playing, UOSound next, int maxCount = MaxPlayingSounds, long maxBytes = MaxPlayingBytes)
        {
            while (playing.Remove(next))
            {
            }

            long bytes = next.PcmBytes;

            for (LinkedListNode<UOSound> n = playing.First; n != null; n = n.Next)
            {
                bytes += n.Value.PcmBytes;
            }

            int stopped = 0;

            while (playing.First != null && (playing.Count >= maxCount || bytes > maxBytes))
            {
                UOSound oldest = playing.First.Value;
                playing.RemoveFirst();
                bytes -= oldest.PcmBytes;
                oldest.Stop();
                stopped++;
            }

            return stopped;
        }
    }

    /// <summary>
    /// The sound effects played so far (Codex audit P1: ClassicUO kept every played id's PCM for the session, and a pack
    /// whose ids alias one large record could make each id hold its own copy).
    /// - One PCM array per source: ids that share a pack record (Sound.def aliases) share the array, which nothing writes.
    /// - <see cref="Bytes"/> (the distinct arrays) stays within <see cref="Budget"/>, and at most <see cref="MaxIds"/> ids are
    ///   cached. The least recently played id that is not playing is evicted (its sound disposed, its array dropped
    ///   when no other cached id uses it). A sound that cannot fit because everything else is playing is not played.
    /// Main thread only, like the rest of the audio code.
    /// </summary>
    public sealed class SoundCache
    {
        private sealed class Pcm
        {
            public byte[] Data;
            public string Name;
            public int Users;
        }

        private sealed class Entry
        {
            public int Id;
            public long Key;
            public Pcm Pcm;
            public UOSound Sound;
            public LinkedListNode<Entry> Node;
        }

        private readonly SoundsLoader _loader;
        private readonly Func<UOSound, bool> _isBusy;
        private readonly Dictionary<int, Entry> _byId = new Dictionary<int, Entry>();
        private readonly Dictionary<long, Pcm> _byKey = new Dictionary<long, Pcm>();
        private readonly LinkedList<Entry> _lru = new LinkedList<Entry>(); // first = least recently played

        /// <param name="isBusy">Whether a sound may not be evicted; default: FNA holds a buffer of it (<see cref="IO.Audio.Sound.IsActive"/>).</param>
        public SoundCache(SoundsLoader loader, long budget, int maxIds, Func<UOSound, bool> isBusy = null)
        {
            _loader = loader;
            Budget = budget;
            MaxIds = maxIds;
            _isBusy = isBusy ?? (s => s.IsActive);
        }

        public long Budget { get; }
        public int MaxIds { get; }

        /// <summary>Bytes of PCM held: each distinct array once.</summary>
        public long Bytes { get; private set; }

        /// <summary>Ids cached.</summary>
        public int Count => _byId.Count;

        /// <summary>Distinct PCM arrays held.</summary>
        public int Arrays => _byKey.Count;

        public long Evictions { get; private set; }
        public long Refusals { get; private set; }

        public UOSound Get(int id)
        {
            if (_byId.TryGetValue(id, out Entry hit))
            {
                _lru.Remove(hit.Node);
                _lru.AddLast(hit.Node);

                return hit.Sound;
            }

            if (!_loader.TryGetSoundSource(id, out long key, out long size))
            {
                return null;
            }

            if (!_byKey.TryGetValue(key, out Pcm pcm))
            {
                // a pack record's size is known: make room first, and do not read what cannot be held
                if (size > 0 && !Trim(size, null))
                {
                    Refuse(id, size);

                    return null;
                }

                long t0 = Stopwatch.GetTimestamp();

                if (!_loader.TryGetSound(id, out byte[] data, out string name, out long got))
                {
                    return null;
                }

                // a loose file the loader refused falls back to the pack's record, which may already be cached
                if (got == key || !_byKey.TryGetValue(got, out pcm))
                {
                    if (!Trim(data.Length, null))
                    {
                        Refuse(id, data.Length);

                        return null;
                    }

                    pcm = new Pcm { Data = data, Name = name };
                    _byKey[got] = pcm;
                    Bytes += data.Length;
                    Log.Trace($"sound {id} ({name}): first read {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0.000} ms, {data.Length} bytes");
                }

                key = got;
            }

            var e = new Entry { Id = id, Key = key, Pcm = pcm, Sound = new UOSound(pcm.Name, id, pcm.Data) };
            pcm.Users++;
            e.Node = _lru.AddLast(e);
            _byId[id] = e;

            if (!Trim(0, e))
            {
                Remove(e);
                Refuse(id, pcm.Data.Length);

                return null;
            }

            return e.Sound;
        }

        // Evicts the least recently played idle ids (never keep) until extraBytes more fit in the budget and the id count
        // is within MaxIds. False when that is impossible because the rest are playing.
        private bool Trim(long extraBytes, Entry keep)
        {
            int extraIds = keep == null ? 1 : 0;
            LinkedListNode<Entry> n = _lru.First;

            while ((Bytes + extraBytes > Budget || _byId.Count + extraIds > MaxIds) && n != null)
            {
                LinkedListNode<Entry> next = n.Next;

                if (n.Value != keep && !_isBusy(n.Value.Sound))
                {
                    Remove(n.Value);
                    n.Value.Sound.Dispose();
                    Evictions++;
                }

                n = next;
            }

            return Bytes + extraBytes <= Budget && _byId.Count + extraIds <= MaxIds;
        }

        private void Remove(Entry e)
        {
            _lru.Remove(e.Node);
            _byId.Remove(e.Id);

            if (--e.Pcm.Users == 0)
            {
                _byKey.Remove(e.Key);
                Bytes -= e.Pcm.Data.Length;
            }
        }

        private void Refuse(int id, long bytes)
        {
            if (++Refusals <= 10)
            {
                Log.Warn($"sound {id}: {bytes} bytes do not fit the sound cache ({Bytes} of {Budget} bytes held by playing sounds); not played");
            }
        }

        /// <summary>Drops every id; sounds that are not playing are disposed (playing ones finish and are collected).</summary>
        public void Clear()
        {
            foreach (Entry e in _lru)
            {
                if (!_isBusy(e.Sound))
                {
                    e.Sound.Dispose();
                }
            }

            _lru.Clear();
            _byId.Clear();
            _byKey.Clear();
            Bytes = 0;
        }
    }
}
