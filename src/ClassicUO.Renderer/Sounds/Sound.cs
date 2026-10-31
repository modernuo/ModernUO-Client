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

        /// <summary>
        /// Most sound effects playing at once; the oldest silent one, else the oldest, is stopped to make room. ClassicUO
        /// had no limit, and this is a guard against pathological shards, not a mixing policy: each id is one
        /// UOSound with one FAudio voice and a replay delay of half its length, so the count is the number of DISTINCT ids
        /// sounding at once. Crowded combat sends two sounds per swing (ModernUO BaseWeapon: attacker's hit/miss and
        /// defender's hurt, distinct per weapon and creature type) plus spells and creature idle/anger/death sounds, and the
        /// median stock sound lasts 1.25 s, so 30-60 distinct ids at once is plausible in a large fight: Codex round 2
        /// showed 32 could cut audible sounds there. Neither FNA nor FAudio limits voices (FNA never throws
        /// InstancePlayLimitException; FAudio keeps source voices in a list), and a voice costs its native PCM copy (bounded
        /// by <see cref="MaxPlayingBytes"/>) plus mixing time. 256 is far above a crowded scene and still bounds the
        /// mixer's work; the sound cache's 1,024 ids bound it too.
        /// </summary>
        public const int MaxPlayingSounds = 256;

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

        /// <summary><see cref="GetSoundToPlay(SoundCache, int, LinkedList{UOSound}, bool)"/> on this <see cref="Cache"/>.</summary>
        public UOSound GetSoundToPlay(int index, LinkedList<UOSound> playing, bool audible) => GetSoundToPlay(Cache, index, playing, audible);

        /// <summary>
        /// The sound to play for <paramref name="index"/>, before <see cref="LimitPlaying"/>. When the cache cannot admit
        /// it because playing sounds hold its budget, an AUDIBLE request stops silent sounds in <paramref name="playing"/>
        /// (oldest first, removed from the list) to make room, then asks again (Codex round 3, P2: background play could
        /// pin the whole budget with silent sounds, and the limiter, which would stop them, ran only after the cache had
        /// already refused). Whether that helps, and how many to stop, is decided from the source's size alone
        /// (<see cref="SoundCache.CouldAdmit"/>): nothing is stopped when stopping every silent sound would not be enough.
        /// A silent request never stops anything.
        /// </summary>
        public static UOSound GetSoundToPlay(SoundCache cache, int index, LinkedList<UOSound> playing, bool audible)
        {
            if (index < 0 || index >= MAX_SOUND_DATA_INDEX_COUNT)
            {
                return null;
            }

            UOSound sound = cache.Get(index);

            if (sound != null || !audible)
            {
                return sound;
            }

            HashSet<UOSound> silent = null;

            for (LinkedListNode<UOSound> n = playing.First; n != null; n = n.Next)
            {
                if (!n.Value.IsAudible)
                {
                    (silent ??= new HashSet<UOSound>()).Add(n.Value);
                }
            }

            if (silent == null || !cache.CouldAdmit(index, silent.Contains))
            {
                return null;
            }

            for (LinkedListNode<UOSound> n = playing.First; n != null && !cache.CouldAdmit(index);)
            {
                LinkedListNode<UOSound> after = n.Next;

                if (silent.Contains(n.Value))
                {
                    playing.Remove(n);
                    n.Value.Stop();
                }

                n = after;
            }

            return cache.Get(index);
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
        /// stops and removes sounds until fewer than <paramref name="maxCount"/> remain and their PCM plus
        /// <paramref name="next"/>'s fits in <paramref name="maxBytes"/>: silent ones first (<see cref="IO.Audio.Sound.IsAudible"/>),
        /// oldest first, then the oldest audible ones. Returns how many were stopped.
        ///
        /// A silent <paramref name="next"/> (<paramref name="audible"/> false: sound off, window in the background) never
        /// stops another sound (Codex round 2, P2: it used to cut audible ones to play silence): when it does not fit
        /// as is, the list is left untouched and -1 is returned, and the caller does not play it.
        /// </summary>
        public static int LimitPlaying(LinkedList<UOSound> playing, UOSound next, int maxCount = MaxPlayingSounds, long maxBytes = MaxPlayingBytes, bool audible = true)
        {
            long bytes = next.PcmBytes;
            int count = 0;

            for (LinkedListNode<UOSound> n = playing.First; n != null; n = n.Next)
            {
                if (n.Value != next)
                {
                    bytes += n.Value.PcmBytes;
                    count++;
                }
            }

            if (!audible && (count >= maxCount || bytes > maxBytes))
            {
                return -1;
            }

            while (playing.Remove(next))
            {
            }

            int stopped = 0;

            for (int pass = 0; pass < 2 && (playing.Count >= maxCount || bytes > maxBytes); pass++)
            {
                // pass 0: silent sounds only, oldest first; pass 1: any, oldest first
                LinkedListNode<UOSound> n = playing.First;

                while (n != null && (playing.Count >= maxCount || bytes > maxBytes))
                {
                    LinkedListNode<UOSound> after = n.Next;

                    if (pass == 1 || !n.Value.IsAudible)
                    {
                        playing.Remove(n);
                        bytes -= n.Value.PcmBytes;
                        n.Value.Stop();
                        stopped++;
                    }

                    n = after;
                }
            }

            return stopped;
        }
    }

    /// <summary>
    /// The sound effects played so far (Codex audit P1: ClassicUO kept every played id's PCM for the session, and a pack
    /// whose ids alias one large record could make each id hold its own copy).
    /// - One PCM array per source: ids that share a pack record (Sound.def aliases) share the array, which nothing writes.
    /// - <see cref="Bytes"/> (the distinct arrays, including those of sounds still playing from before a
    ///   <see cref="Clear"/>) stays within <see cref="Budget"/>, and at most <see cref="MaxIds"/> ids hold a sound. The
    ///   least recently played id that is not playing is evicted (its sound disposed, its array dropped when no other
    ///   cached id uses it). A sound that cannot fit because everything else is playing is not played.
    /// - Nothing is read before it is known to fit: the loader names every source's PCM size first (a pack record's from
    ///   its table, a loose WAV's from its header, remembered), so a refused sound costs a scan, never a payload read, and
    ///   evicts nothing; it plays once enough of the playing sounds have finished (Codex round 2, P2).
    /// Main thread only, like the rest of the audio code.
    /// </summary>
    public sealed class SoundCache
    {
        private sealed class Pcm
        {
            public byte[] Data;
            public string Name;
            public int Users;
            public int Mark; // scratch for Fits: the pass that last counted this array
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
        // sounds that were playing when Clear ran: no longer served, still counted until they finish (then disposed)
        private readonly List<Entry> _retired = new List<Entry>();
        private int _mark;

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

        /// <summary>Bytes of PCM held: each distinct array once, retired playing sounds included.</summary>
        public long Bytes { get; private set; }

        /// <summary>Ids cached (served by <see cref="Get"/>).</summary>
        public int Count => _byId.Count;

        /// <summary>Sounds dropped by <see cref="Clear"/> while playing, still counted until they finish.</summary>
        public int Retired => _retired.Count;

        /// <summary>Distinct PCM arrays held by cached ids.</summary>
        public int Arrays => _byKey.Count;

        public long Evictions { get; private set; }
        public long Refusals { get; private set; }

        // ids holding a sound: the cached ones and the retired ones still playing
        private int Ids => _byId.Count + _retired.Count;

        public UOSound Get(int id)
        {
            SweepRetired();

            if (_byId.TryGetValue(id, out Entry hit))
            {
                _lru.Remove(hit.Node);
                _lru.AddLast(hit.Node);

                return hit.Sound;
            }

            // the size is known before any read: a pack record's from its table, a loose file's from its header
            if (!_loader.TryGetSoundSource(id, out long key, out long size))
            {
                return null;
            }

            if (!_byKey.TryGetValue(key, out Pcm pcm))
            {
                size = Math.Clamp(size, 0, int.MaxValue);

                // admission is decided from the size alone, and nothing is evicted yet: a request refused below (here,
                // or by the read because the source grew) leaves the cache as it was (Codex round 3, P2)
                if (!CanTrim(size, null))
                {
                    Refuse(id, size);

                    return null;
                }

                long t0 = Stopwatch.GetTimestamp();

                // read at most the admitted size: a source that grew since its size was taken is not read (no allocation
                // beyond the cap) and not played this time; the loader remembers the new size for the next request
                if (!_loader.TryGetSound(id, (int)size, out byte[] data, out string name, out long got, out bool changed))
                {
                    if (changed)
                    {
                        Refuse(id, size, "its source changed size since it was looked at");
                    }

                    return null;
                }

                // the source changed between the two calls (a loose file was replaced, or became unreadable and the
                // pack's record answered): it may already be cached, else it is budgeted at its real size, at most the
                // admitted one, so this Trim succeeds. Idle sounds are evicted only now, once the read succeeded: the
                // PCM briefly sits beside them (at most the admitted size).
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

        /// <summary>
        /// Whether <see cref="Get"/> could admit <paramref name="id"/> if the sounds <paramref name="released"/> accepts
        /// were no longer playing, decided from the source's size (<see cref="SoundsLoader.TryGetSoundSource"/>) without
        /// reading or evicting anything. True for a cached id; false for an id without a source.
        /// </summary>
        public bool CouldAdmit(int id, Func<UOSound, bool> released = null)
        {
            SweepRetired();

            if (_byId.ContainsKey(id))
            {
                return true;
            }

            if (!_loader.TryGetSoundSource(id, out long key, out long size))
            {
                return false;
            }

            long bytes = _byKey.ContainsKey(key) ? 0 : Math.Max(size, 0);

            return Bytes + bytes <= Budget && Ids + 1 <= MaxIds || Fits(bytes, 1, null, released);
        }

        // Trim's answer without evicting anything
        private bool CanTrim(long extraBytes, Entry keep)
        {
            int extraIds = keep == null ? 1 : 0;

            return Bytes + extraBytes <= Budget && Ids + extraIds <= MaxIds || Fits(extraBytes, extraIds, keep);
        }

        // Evicts the least recently played idle ids (never keep) until extraBytes more fit in the budget and the id count
        // is within MaxIds. False, with nothing evicted, when that is impossible because the rest are playing.
        private bool Trim(long extraBytes, Entry keep)
        {
            int extraIds = keep == null ? 1 : 0;

            if (Bytes + extraBytes <= Budget && Ids + extraIds <= MaxIds)
            {
                return true;
            }

            if (!Fits(extraBytes, extraIds, keep))
            {
                return false;
            }

            LinkedListNode<Entry> n = _lru.First;

            while ((Bytes + extraBytes > Budget || Ids + extraIds > MaxIds) && n != null)
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

            return Bytes + extraBytes <= Budget && Ids + extraIds <= MaxIds;
        }

        // Whether evicting every idle id would make room: the arrays of busy ids, keep and retired sounds stay. So a
        // refusal evicts nothing and costs this scan only. Sounds released accepts count as idle (CouldAdmit: as if stopped).
        private bool Fits(long extraBytes, int extraIds, Entry keep, Func<UOSound, bool> released = null)
        {
            int mark = ++_mark;
            long held = 0;
            int ids = 0;

            foreach (Entry r in _retired)
            {
                if (released != null && released(r.Sound))
                {
                    continue;
                }

                ids++;

                if (r.Pcm.Mark != mark)
                {
                    r.Pcm.Mark = mark;
                    held += r.Pcm.Data.Length;
                }
            }

            foreach (Entry e in _lru)
            {
                if (e == keep || _isBusy(e.Sound) && (released == null || !released(e.Sound)))
                {
                    ids++;

                    if (e.Pcm.Mark != mark)
                    {
                        e.Pcm.Mark = mark;
                        held += e.Pcm.Data.Length;
                    }
                }
            }

            return held + extraBytes <= Budget && ids + extraIds <= MaxIds;
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

        private void Refuse(int id, long bytes, string why = null)
        {
            if (++Refusals <= 10)
            {
                Log.Warn(why == null
                    ? $"sound {id}: {bytes} bytes do not fit the sound cache ({Bytes} of {Budget} bytes held by playing sounds); not played"
                    : $"sound {id}: {why} ({bytes} bytes admitted); not played this time");
            }
        }

        // retired sounds that finished: disposed, their arrays no longer counted
        private void SweepRetired()
        {
            for (int i = _retired.Count - 1; i >= 0; i--)
            {
                Entry r = _retired[i];

                if (!_isBusy(r.Sound))
                {
                    _retired.RemoveAt(i);
                    r.Sound.Dispose();

                    if (--r.Pcm.Users == 0)
                    {
                        Bytes -= r.Pcm.Data.Length;
                    }
                }
            }
        }

        /// <summary>
        /// Drops every id (a reload: the sources may have changed). Sounds that are not playing are disposed. Playing ones
        /// play to the end (a reload never cuts a sound off) and stay counted in <see cref="Bytes"/> and the id limit until
        /// they finish (Codex round 2, P3: they used to be forgotten, so the new cache could fill its budget beside them).
        /// </summary>
        public void Clear()
        {
            foreach (Entry e in _lru)
            {
                if (_isBusy(e.Sound))
                {
                    _retired.Add(e);
                }
                else
                {
                    e.Sound.Dispose();

                    if (--e.Pcm.Users == 0)
                    {
                        Bytes -= e.Pcm.Data.Length;
                    }
                }
            }

            _lru.Clear();
            _byId.Clear();
            _byKey.Clear();
        }
    }
}
