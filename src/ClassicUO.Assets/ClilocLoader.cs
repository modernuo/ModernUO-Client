// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using Uo.Pack;
using Uo.Buffers;

namespace ClassicUO.Assets
{
    public sealed class ClilocLoader : UOFileLoader
    {
        private const string MISSING_CLILOC_TEXT = "MegaCliloc: missing {0} [~1_val~] [~2_val~]";
        // the language asked for (Load(lang)); the one in use is State.Language
        private string _requested;

        // What GetString looks up, in order: the shard's own strings from a loose Clilocs.txt, the shard's own strings
        // folded into the pack (text/cliloc.bin's overrides record), the language's table, then Cliloc.enu's (the
        // language overrides English, as before). Each table holds its UTF-8 text and an index sorted by number; a
        // string is created on its first lookup and cached (ClilocTable, thread-safe). A (re)load builds a whole new
        // state and publishes it once, so readers on other threads (plugins, workers) see either the old set or the new
        // one, never a half-built or empty one; a load that fails keeps the old state.
        private sealed class State
        {
            public Dictionary<int, string> Ours;
            public ClilocTable PackOverrides;
            public ClilocTable Lang;
            public ClilocTable Enu;
            public string Language;
            // CUO_SHARED_CLILOC_CHECK: the heap path's tables for the same load, compared at every lookup
            public State Shadow;

            // Translate results for empty arguments, bounded; a new state starts empty, so a reload or language change drops them
            public readonly ConcurrentDictionary<int, string> Plain = new ConcurrentDictionary<int, string>();
            public readonly ConcurrentDictionary<int, string> Capitalized = new ConcurrentDictionary<int, string>();
        }

        // Entries per cache (plain, capitalized): about 0.2 KB each, so at most ~0.4 MB per client when full; a client
        // typically fills a few hundred. Past the cap a call is computed without storing.
        internal const int TranslateCacheLimit = 1024;

        private volatile State _state = new State();
        private int _revision;

        /// <summary>
        /// shared_cliloc: when set (the client sets it from the switch before the first load), the tables are read in place
        /// from shared read-only memory (<see cref="SharedTableCache"/>): a raw record straight from the pack file, a
        /// compressed one from the per-user cache file it was decompressed into once. Null: today's heap path (a decoded
        /// copy of each table per process). A table that can't be mapped falls back to the heap path.
        /// </summary>
        public SharedTableCache Shared { get; set; }

        /// <summary>CUO_SHARED_CLILOC_CHECK: with <see cref="Shared"/> set, also loads the heap path's tables and compares
        /// every lookup (same string or same miss), logging each mismatch and a summary every minute.</summary>
        public bool CheckShared { get; set; }

        private long _checkLookups, _checkMismatches, _checkLogAt;

        /// <summary>Bumps every time a new set of strings is published (a load, a reload, a language change, a clear):
        /// callers that cache a string built from cliloc key it on this.</summary>
        public int Revision => Volatile.Read(ref _revision);

        public ClilocLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        /// <summary>The language in use ("enu" when the one asked for has no strings anywhere).</summary>
        public string Language => _state.Language;

        /// <summary>Tests and measurements: the loaded tables (the language's, Cliloc.enu's when the language isn't English, the pack's overrides record).</summary>
        public ClilocTable LanguageTable => _state.Lang;
        public ClilocTable EnglishTable => _state.Enu;
        public ClilocTable PackOverridesTable => _state.PackOverrides;

        /// <summary>Tests: the strings of a loose Clilocs.txt (null when there is none).</summary>
        public IReadOnlyDictionary<int, string> LooseOverrides => _state.Ours;

        /// <summary>
        /// The cliloc language codes the pack has, lower-case, each exactly three ASCII letters (the language packet's
        /// wire code; longer pack identifiers are ignored and logged). Cheap: no table is read. A pack without
        /// text/cliloc.bin throws; an invalid one is logged and lists nothing.
        /// </summary>
        public List<string> AvailableLanguages()
        {
            string path = RequirePackPath();
            var list = new List<string>();

            try
            {
                using var store = RecordStore.Open(path, TextConst.MagicCliloc);
                List<string> ignored = null;

                foreach (string code in ClilocStore.Resolve(store).Languages.Keys)
                {
                    if (IsLanguageCode(code))
                    {
                        list.Add(code.ToLowerInvariant());
                    }
                    else
                    {
                        (ignored ??= new List<string>()).Add(code);
                    }
                }

                if (ignored != null)
                {
                    Log.Warn($"Cliloc: the pack's languages {string.Join(", ", ignored)} are ignored: a language code is three letters");
                }
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                Log.Error($"Cliloc: '{path}' is invalid ({e.Message})");
            }

            return list;
        }

        /// <summary>A language the client can select: exactly three ASCII letters (the language packet carries three characters).</summary>
        public static bool IsLanguageCode(string code) =>
            code != null && code.Length == 3 && char.IsAsciiLetter(code[0]) && char.IsAsciiLetter(code[1]) && char.IsAsciiLetter(code[2]);

        public void Load(string lang)
        {
            _requested = string.IsNullOrEmpty(lang) ? "enu" : lang;
            Load();
        }

        public override void Load()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            string requested = string.IsNullOrEmpty(_requested) ? "enu" : _requested;
            string path = PackPath;
            State state;

            if (path == null || !File.Exists(path))
            {
                // the first load can't go on without strings; a reload keeps the ones it has
                var missing = UOFileManager.MissingPackFile(TextConst.ClilocFile, path);

                if (_state.Lang == null)
                {
                    throw missing;
                }

                Log.Error($"Cliloc: {missing.Message}");
                state = null;
            }
            else
            {
                try
                {
                    state = Build(requested, path);
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is ArgumentException || e is IndexOutOfRangeException)
                {
                    Log.Error($"Cliloc: loading '{requested}' failed ({e.Message})");
                    state = null;
                }
            }

            if (state == null)
            {
                Log.Error(_state.Lang != null ? "Cliloc: keeping the strings loaded before" : "Cliloc: no cliloc strings could be loaded");

                return;
            }

            _state = state;
            Interlocked.Increment(ref _revision);
            Log.Trace($"Cliloc loaded in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0.0} ms, {(GC.GetAllocatedBytesForCurrentThread() - a0) / 1024} KB allocated ('{state.Language}': {state.Lang.Count} strings{(state.Enu != null ? $" over {state.Enu.Count} English" : "")}{(state.PackOverrides != null ? $", {state.PackOverrides.Count} packed overrides" : "")}{(state.Ours != null ? $", {state.Ours.Count} from {ClilocOverrides.FileName}" : "")})");
        }

        /// <summary>Tests and tools: read this text/cliloc.bin instead of the pack's.</summary>
        internal string PackFile;

        private string PackPath => PackFile ?? FileManager.Pack?.PathOf(TextConst.ClilocFile);

        private string RequirePackPath()
        {
            string path = PackPath;

            return path != null && File.Exists(path) ? path : throw UOFileManager.MissingPackFile(TextConst.ClilocFile, path);
        }

        // A complete new state, or null. A store that fails to open or resolve throws (Load logs it); a language record
        // that is corrupt serves nothing (null).
        //
        // Complete means: a language other than English has the English base under it, and a Clilocs.txt that exists
        // was read. When strings are already loaded (a reload), a state missing the Clilocs.txt is not published: the
        // old one stays. On the first load there is nothing to keep, so the languages serve, with the error logged.
        private State Build(string requested, string path)
        {
            bool keepOld = _state.Lang != null;
            using var store = RecordStore.Open(path, TextConst.MagicCliloc);
            var dir = ClilocStore.Resolve(store);
            var shared = Shared;
            TableReader read = shared == null ? HeapTable : (s, rec, lang) => SharedTable(shared, path, s, rec, lang);
            var state = LanguagesFromPack(store, dir, requested, read);

            if (state != null && shared != null && CheckShared)
            {
                state.Shadow = LanguagesFromPack(store, dir, requested, HeapTable);

                if (state.Shadow != null && dir.Overrides >= 0)
                {
                    state.Shadow.PackOverrides = ClilocStore.ReadTable(store, dir.Overrides, out _);
                }
            }

            if (state == null)
            {
                return null;
            }

            if (dir.Overrides >= 0)
            {
                // the shard's own strings in the pack: a bad record is absent, the languages still serve
                state.PackOverrides = read(store, dir.Overrides, null);
                string why = "a failed read or a malformed payload";

                if (state.PackOverrides == null)
                {
                    Log.Error($"Cliloc: the pack's overrides record has {why}; it is ignored");
                }
            }

            state.Ours = ReadOurs(out bool oursFailed);

            if (state.Shadow != null)
            {
                state.Shadow.Ours = state.Ours;
                state.Shadow.Language = state.Language;
            }

            if (oursFailed && keepOld)
            {
                return null;
            }

            return state;
        }

        // The pack's language record and, for another language, enu's; null when they can't serve.
        // A record's table (lang null: the overrides record), or null with the error logged.
        private delegate ClilocTable TableReader(RecordStore store, int record, string lang);

        private static ClilocTable HeapTable(RecordStore store, int record, string lang)
        {
            var table = ClilocStore.ReadTable(store, record, out string why);

            if (table == null && lang != null)
            {
                Log.Error($"Cliloc: the pack's '{lang}' record has {why}");
            }

            return table;
        }

        // shared_cliloc: the record read in place from shared memory; the heap path when it can't be mapped
        private static unsafe ClilocTable SharedTable(SharedTableCache shared, string path, RecordStore store, int record, string lang)
        {
            ref readonly var r = ref store.Records[record];

            if (r.RawBytes >= ClilocTable.HeaderBytes && r.RawBytes <= ClilocTable.MaxRawBytes &&
                r.StoredBytes <= PackCodec.MaxStoredBytes(r.Codec, r.RawBytes))
            {
                SharedTableCache.Mapping m;
                int at;

                if (r.Codec == StoreConst.CodecRaw)
                {
                    m = shared.MapFileRange(path, (long)r.Offset, r.RawBytes);
                    at = 0;
                }
                else
                {
                    var source = new SharedTableCache.CacheSource
                    {
                        FileBytes = (ulong)store.FileLength, Offset = r.Offset, StoredBytes = r.StoredBytes, RawBytes = r.RawBytes, Codec = r.Codec
                    };
                    var rec = r;
                    m = shared.GetOrBuild($"cliloc-{lang ?? "overrides"}.bin", source, (int)r.RawBytes, dst => store.Read(rec, dst));
                    at = SharedTableCache.CacheHeader.Size;
                }

                if (m != null)
                {
                    var table = ClilocTable.TryParseMapped(m.Pointer + at, (int)r.RawBytes, m);

                    if (table != null)
                    {
                        // first touch off the lookup path: pages another instance has are mapped in here, on a worker
                        System.Threading.ThreadPool.UnsafeQueueUserWorkItem(static mm => mm.Prefault(), m, preferLocal: false);

                        return table;
                    }

                    Log.Error($"Cliloc: the shared copy of the pack's '{lang ?? "overrides"}' record is malformed; reading it to the heap");
                }
            }

            return HeapTable(store, record, lang);
        }

        private static State LanguagesFromPack(RecordStore store, ClilocDirectory dir, string requested, TableReader read)
        {
            string lang = requested.ToLowerInvariant();

            if (!dir.Languages.ContainsKey(lang))
            {
                Log.Warn($"Cliloc: the pack has no '{lang}'; rolled back to enu");
                lang = "enu";
            }

            var state = new State { Language = lang, Lang = PackTable(store, dir, lang, read) };

            if (state.Lang == null)
            {
                return null;
            }

            if (lang != "enu")
            {
                // the client reads Cliloc.enu under every language: a language file overrides it id by id
                state.Enu = PackTable(store, dir, "enu", read);

                if (state.Enu == null)
                {
                    return null;
                }
            }

            return state;
        }

        private static ClilocTable PackTable(RecordStore store, ClilocDirectory dir, string lang, TableReader read)
        {
            if (!dir.Languages.TryGetValue(lang, out int record))
            {
                Log.Error($"Cliloc: the pack has no '{lang}'");

                return null;
            }

            return read(store, record, lang);
        }

        /// <summary>
        /// Strings of the shard's own, from a plain text file, read last so they win.
        ///
        /// A context menu entry, a gump caption and an item's name are all cliloc numbers, and a
        /// shard is limited to whatever the client already happens to say. There is no number for
        /// "Send to kennel" because Origin never needed one, so anything a shard adds is either
        /// approximated with a string that nearly fits or shown as a raw number.
        ///
        /// The converter folds the file into the pack (text/cliloc.bin's overrides record), so a
        /// shard ships it with the pack; a loose Clilocs.txt in the client folder still wins over
        /// the packed one, for customising and for quick edits while developing. Both win over
        /// the language and Cliloc.enu, so the same mechanism adds new numbers and corrects
        /// existing ones, and neither needs the compressed original touched.
        ///
        /// Plain text rather than the client's own format on purpose: a line somebody can read,
        /// diff and edit beats a BWT-compressed blob for a file that will hold a few dozen
        /// entries. Anything above 3,100,000 is free - the client's own stop at 3,011,032.
        ///
        ///     # Clilocs.txt, in the client folder
        ///     3100001    Breed
        ///     3100002    Send to kennel
        /// </summary>
        // Null with failed false when there is no Clilocs.txt (it is optional); null with failed true when one exists but
        // can't be read.
        private Dictionary<int, string> ReadOurs(out bool failed)
        {
            failed = false;
            string path = FileManager.GetUOFilePath(ClilocOverrides.FileName);

            Dictionary<int, string> ours;

            try
            {
                // read directly: File.Exists is false for an access error too, which must not read as "absent"
                ours = ClilocOverrides.Parse(File.ReadLines(path));
            }
            catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
            {
                return null; // optional, and not there
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.Error($"Cliloc: could not read {path} ({e.Message})");
                failed = true;

                return null;
            }

            if (ours.Count > 0)
            {
                Log.Trace($"{ours.Count} cliloc string(s) of our own from {Path.GetFileName(path)}");
            }

            return ours;
        }

        public override void ClearResources()
        {
            _state = new State();
            Interlocked.Increment(ref _revision);
        }

        /// <summary>
        /// The string of <paramref name="clilocNum"/>: a loose Clilocs.txt's, the pack's overrides record's, the
        /// language's, else English. Thread-safe.
        /// </summary>
        public bool TryGetString(int clilocNum, out string text)
        {
            var state = _state;
            bool found = Lookup(state, clilocNum, out text);
#if CUO_DEVTOOLS
            if (TracePath != null)
            {
                TraceId(clilocNum);
            }

            if (state.Shadow != null)
            {
                Check(state, clilocNum, found, text);
            }
#endif
            return found;
        }

#if CUO_DEVTOOLS
        // CUO_SHARED_CLILOC_CHECK: the heap path's answer for the same id must be the same string or the same miss
        private void Check(State state, int clilocNum, bool found, string text)
        {
            bool heapFound = Lookup(state.Shadow, clilocNum, out string heap);
            long n = Interlocked.Increment(ref _checkLookups);

            if (heapFound != found || !string.Equals(heap, text, StringComparison.Ordinal))
            {
                long bad = Interlocked.Increment(ref _checkMismatches);

                if (bad <= 50)
                {
                    Log.Warn($"[shared-cliloc-check] mismatch id {clilocNum} ('{state.Language}'): shared {(found ? $"'{text}'" : "miss")}, heap {(heapFound ? $"'{heap}'" : "miss")}");
                }
            }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long at = Volatile.Read(ref _checkLogAt);

            if (n == 1 || now - at > System.Diagnostics.Stopwatch.Frequency * 60)
            {
                if (Interlocked.CompareExchange(ref _checkLogAt, now, at) == at)
                {
                    Log.Info($"[shared-cliloc-check] shared_cliloc=on lookups {n} mismatches {Volatile.Read(ref _checkMismatches)} lang '{state.Language}' mapped {DescribeMapping(state)}");
                }
            }
        }

        // CUO_CLILOC_ID_TRACE=<file>: every looked-up id appended as an i32 (the shared-cliloc benchmark's realistic id stream)
        private static readonly string TracePath = Environment.GetEnvironmentVariable("CUO_CLILOC_ID_TRACE");
        private static readonly object TraceGate = new object();
        private static FileStream _trace;
        private static int _traced;

        private static void TraceId(int id)
        {
            lock (TraceGate)
            {
                try
                {
                    _trace ??= new FileStream(TracePath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096);
                    Span<byte> b = stackalloc byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b, id);
                    _trace.Write(b);

                    if (++_traced % 256 == 0)
                    {
                        _trace.Flush();
                    }
                }
                catch (IOException)
                {
                }
            }
        }

        private static string DescribeMapping(State s) =>
            $"lang={s.Lang?.IsMapped} enu={s.Enu?.IsMapped} overrides={s.PackOverrides?.IsMapped}";

        /// <summary>Tests: (lookups, mismatches) the shared-cliloc checker has counted.</summary>
        internal (long Lookups, long Mismatches) CheckCounts => (Volatile.Read(ref _checkLookups), Volatile.Read(ref _checkMismatches));
#endif

        private static bool Lookup(State state, int clilocNum, out string text)
        {
            if (state.Ours != null && state.Ours.TryGetValue(clilocNum, out text))
            {
                return true;
            }

            if (state.PackOverrides != null && state.PackOverrides.TryGetString(clilocNum, out text))
            {
                return true;
            }

            if (state.Lang != null && state.Lang.TryGetString(clilocNum, out text))
            {
                return true;
            }

            if (state.Enu != null && state.Enu.TryGetString(clilocNum, out text))
            {
                return true;
            }

            text = null;

            return false;
        }

        [return: NotNull]
        public string GetString(int clilocNum, string fallback = null)
        {
            TryGetString(clilocNum, out string text);

            return text ?? fallback ?? string.Format(MISSING_CLILOC_TEXT, clilocNum);
        }

        [return: NotNull]
        public string GetString(int clilocNum, bool camelcase, string fallback = "")
        {
            string text = GetString(clilocNum, fallback);

            if (camelcase)
            {
                return StringHelper.CapitalizeAllWords(text);
            }

            return text;
        }

        [return: NotNull]
        public string Translate(int clilocNum, string arg = "", bool capitalize = false)
        {
            if (!string.IsNullOrEmpty(arg))
            {
                return TranslateUncached(clilocNum, arg, capitalize);
            }

            // Empty arguments make the result a function of the number alone (and the loaded strings, which the state
            // owns), so repeated lookups (name overheads, tooltips) reuse one string.
            var cache = capitalize ? _state.Capitalized : _state.Plain;

            if (cache.TryGetValue(clilocNum, out string cached))
            {
                return cached;
            }

            string result = TranslateUncached(clilocNum, "", capitalize);

            if (cache.Count < TranslateCacheLimit)
            {
                cache[clilocNum] = result;
            }

            return result;
        }

        [return: NotNull]
        private string TranslateUncached(int clilocNum, string arg, bool capitalize)
        {
            ValueStringBuilder sb = TranslateToBuilder(clilocNum, arg == null ? ReadOnlySpan<char>.Empty : arg.AsSpan(), capitalize);
            string result = sb.ToString();
            sb.Dispose();

            return result;
        }

        // The translated text in a pooled builder (caller disposes), for callers that copy it on instead of keeping a
        // string per line (MegaCliloc property lists).
        public ValueStringBuilder TranslateToBuilder(int clilocNum, ReadOnlySpan<char> arg, bool capitalize)
        {
            string baseCliloc = GetString(clilocNum);

            var roChars = arg;


            // get count of valid args
            int i = 0;
            int totalArgs = 0;
            int trueStart = -1;

            for (; i < roChars.Length; ++i)
            {
                if (roChars[i] != '\t')
                {
                    if (trueStart == -1)
                    {
                        trueStart = i;
                    }
                }
                else if (trueStart >= 0)
                {
                    ++totalArgs;
                }
            }

            if (trueStart == -1)
            {
                trueStart = 0;
            }

            // store index locations
            Span<(int, int)> locations = stackalloc (int, int)[++totalArgs];
            i = trueStart;
            for (int j = 0; i < roChars.Length; ++i)
            {
                if (roChars[i] == '\t')
                {
                    locations[j].Item1 = trueStart;
                    locations[j].Item2 = i;

                    trueStart = i + 1;

                    ++j;
                }
            }

            bool has_arguments = totalArgs - 1 > 0;

            locations[totalArgs - 1].Item1 = trueStart;
            locations[totalArgs - 1].Item2 = i;

            ValueStringBuilder sb = new ValueStringBuilder(baseCliloc.AsSpan());
            {
                int index, pos = 0;

                while (pos < sb.Length)
                {
                    int poss = pos;
                    pos = sb.RawChars.Slice(pos, sb.Length - pos).IndexOf('~');

                    if (pos == -1)
                    {
                        break;
                    }

                    pos += poss;

                    int pos2 = sb.RawChars.Slice(pos + 1, sb.Length - (pos + 1)).IndexOf('~');

                    if (pos2 == -1) //non valid arg
                    {
                        break;
                    }

                    pos2 += pos + 1;

                    index = sb.RawChars.Slice(pos + 1, pos2 - (pos + 1)).IndexOf('_');

                    if (index == -1)
                    {
                        //there is no underscore inside the bounds, so we use all the part to get the number of argument
                        index = pos2;
                    }
                    else
                    {
                        index += pos + 1;
                    }

                    int start = pos + 1;
                    int max = index - start;
                    int count = 0;

                    for (; count < max; count++)
                    {
                        if (!char.IsNumber(sb.RawChars[start + count]))
                        {
                            break;
                        }
                    }

                    if (!int.TryParse(sb.RawChars.Slice(start, count), out index))
                    {
                        sb.Dispose();

                        return new ValueStringBuilder($"MegaCliloc: error for {clilocNum}".AsSpan());
                    }

                    --index;

                    var a = index < 0 || index >= totalArgs ? string.Empty.AsSpan() : arg.Slice(locations[index].Item1, locations[index].Item2 - locations[index].Item1);

                    if (a.Length > 1)
                    {
                        if (a[0] == '#')
                        {
                            if (int.TryParse(a.Slice(1), out int id1))
                            {
                                var ss = GetString(id1);

                                if (string.IsNullOrEmpty(ss))
                                {
                                    a = string.Empty.AsSpan();
                                }
                                else
                                {
                                    a = ss.AsSpan();
                                }
                            }
                        }
                        else if (has_arguments && int.TryParse(a, out int clil))
                        {
                            if (TryGetString(clil, out string value) && !string.IsNullOrEmpty(value))
                            {
                                a = value.AsSpan();
                            }
                        }
                    }

                    sb.Remove(pos, pos2 - pos + 1);
                    sb.Insert(pos, a);

                    if (index >= 0 && index < totalArgs)
                    {
                        pos += a.Length /*locations[index].Y - locations[index].X*/;
                    }
                }

                if (capitalize)
                {
                    StringHelper.CapitalizeAllWords(sb.RawChars.Slice(0, sb.Length));
                }

                return sb;
            }
        }
    }
}
