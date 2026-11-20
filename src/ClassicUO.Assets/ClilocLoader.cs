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

            // Translate results for empty arguments, bounded; a new state starts empty, so a reload or language change drops them
            public readonly ConcurrentDictionary<int, string> Plain = new ConcurrentDictionary<int, string>();
            public readonly ConcurrentDictionary<int, string> Capitalized = new ConcurrentDictionary<int, string>();
        }

        // Entries per cache (plain, capitalized): about 0.2 KB each, so at most ~0.4 MB per client when full; a client
        // typically fills a few hundred. Past the cap a call is computed without storing.
        internal const int TranslateCacheLimit = 1024;

        private volatile State _state = new State();
        private int _revision;

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
            var state = LanguagesFromPack(store, dir, requested);

            if (state == null)
            {
                return null;
            }

            if (dir.Overrides >= 0)
            {
                // the shard's own strings in the pack: a bad record is absent, the languages still serve
                state.PackOverrides = ClilocStore.ReadTable(store, dir.Overrides, out string why);

                if (state.PackOverrides == null)
                {
                    Log.Error($"Cliloc: the pack's overrides record has {why}; it is ignored");
                }
            }

            state.Ours = ReadOurs(out bool oursFailed);

            if (oursFailed && keepOld)
            {
                return null;
            }

            return state;
        }

        // The pack's language record and, for another language, enu's; null when they can't serve.
        private static State LanguagesFromPack(RecordStore store, ClilocDirectory dir, string requested)
        {
            string lang = requested.ToLowerInvariant();

            if (!dir.Languages.ContainsKey(lang))
            {
                Log.Warn($"Cliloc: the pack has no '{lang}'; rolled back to enu");
                lang = "enu";
            }

            var state = new State { Language = lang, Lang = PackTable(store, dir, lang) };

            if (state.Lang == null)
            {
                return null;
            }

            if (lang != "enu")
            {
                // the client reads Cliloc.enu under every language: a language file overrides it id by id
                state.Enu = PackTable(store, dir, "enu");

                if (state.Enu == null)
                {
                    return null;
                }
            }

            return state;
        }

        private static ClilocTable PackTable(RecordStore store, ClilocDirectory dir, string lang)
        {
            if (!dir.Languages.TryGetValue(lang, out int record))
            {
                Log.Error($"Cliloc: the pack has no '{lang}'");

                return null;
            }

            var table = ClilocStore.ReadTable(store, record, out string why);

            if (table == null)
            {
                Log.Error($"Cliloc: the pack's '{lang}' record has {why}");
            }

            return table;
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
