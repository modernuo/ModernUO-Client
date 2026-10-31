// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
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
            public bool FromPack;
            public string Language;
        }

        private volatile State _state = new State();

        public ClilocLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        /// <summary>
        /// Read cliloc from the pack's text section when it has one (set before <see cref="Load"/>). CUO_PACK_TEXT=0 starts
        /// it false: Cliloc.* files even with a text section.
        /// </summary>
        public bool UsePack = Environment.GetEnvironmentVariable("CUO_PACK_TEXT") != "0";

        /// <summary>True when the language tables come from the pack.</summary>
        public bool FromPack => _state.FromPack;

        /// <summary>The language in use ("enu" when the one asked for has no strings anywhere).</summary>
        public string Language => _state.Language;

        /// <summary>Tests and measurements: the loaded tables (the language's, Cliloc.enu's when the language isn't English, the pack's overrides record).</summary>
        public ClilocTable LanguageTable => _state.Lang;
        public ClilocTable EnglishTable => _state.Enu;
        public ClilocTable PackOverridesTable => _state.PackOverrides;

        /// <summary>Tests: the strings of a loose Clilocs.txt (null when there is none).</summary>
        public IReadOnlyDictionary<int, string> LooseOverrides => _state.Ours;

        public void Load(string lang)
        {
            _requested = string.IsNullOrEmpty(lang) ? "enu" : lang;
            Load();
        }

        /// <summary>Loads again (Clilocs.txt edited, say): the current strings stay in use until the new set is complete.</summary>
        public void Reload()
        {
            Load();
        }

        public override void Load()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            string requested = string.IsNullOrEmpty(_requested) ? "enu" : _requested;
            State state;

            try
            {
                state = Build(requested);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is ArgumentException || e is IndexOutOfRangeException)
            {
                // a corrupt Cliloc.* file: nothing new to publish
                Log.Error($"Cliloc: loading '{requested}' failed ({e.Message})");
                state = null;
            }

            if (state == null)
            {
                Log.Error(_state.Lang != null ? "Cliloc: keeping the strings loaded before" : "Cliloc: no cliloc strings could be loaded");

                return;
            }

            _state = state;
            Log.Trace($"Cliloc loaded in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0.0} ms, {(GC.GetAllocatedBytesForCurrentThread() - a0) / 1024} KB allocated ({(state.FromPack ? "pack" : "MUL")}, '{state.Language}': {state.Lang.Count} strings{(state.Enu != null ? $" over {state.Enu.Count} English" : "")}{(state.PackOverrides != null ? $", {state.PackOverrides.Count} packed overrides" : "")}{(state.Ours != null ? $", {state.Ours.Count} from {ClilocOverrides.FileName}" : "")})");
        }

        /// <summary>Tests and tools: read this text/cliloc.bin instead of the pack's (still subject to <see cref="UsePack"/>).</summary>
        internal string PackFile;

        private string PackPath => !UsePack ? null : PackFile ?? FileManager.Pack?.PathOf(TextConst.ClilocFile);

        // A complete new state, or null. The pack serves when it is usable; a pack that can't serve the languages (no
        // section, a store that fails to open or resolve, a corrupt language record) makes the loose Cliloc.* files serve,
        // with the language resolved again for them (Cliloc.<lang>, else Cliloc.enu).
        //
        // Complete means: a language other than English has the English base under it, and a Clilocs.txt that exists
        // was read. When strings are already loaded (a reload), a state missing either is not published: the old one
        // stays. On the first load there is nothing to keep, so the partial state serves, with the error logged.
        private State Build(string requested)
        {
            bool keepOld = _state.Lang != null;
            string path = PackPath;
            RecordStore store = null;
            ClilocDirectory dir = null;

            if (path != null && File.Exists(path))
            {
                try
                {
                    store = RecordStore.Open(path, TextConst.MagicCliloc);
                    dir = ClilocStore.Resolve(store);
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
                {
                    Log.Error($"Cliloc: '{path}' is invalid ({e.Message}); reading Cliloc.* files");
                    store?.Dispose();
                    store = null;
                }
            }
            else if (FileManager.Pack != null)
            {
                Log.Trace(UsePack ? "Cliloc: the pack has no cliloc section; reading Cliloc.* files" : "Cliloc: CUO_PACK_TEXT=0; reading Cliloc.* files");
            }

            try
            {
                var state = (store != null ? LanguagesFromPack(store, dir, requested) : null) ?? LanguagesFromFiles(requested, keepOld);

                if (state == null)
                {
                    return null;
                }

                if (store != null && dir.Overrides >= 0)
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
            finally
            {
                store?.Dispose();
            }
        }

        // The pack's language record and, for another language, enu's; null when they can't serve (the files do then).
        private static State LanguagesFromPack(RecordStore store, ClilocDirectory dir, string requested)
        {
            string lang = requested.ToLowerInvariant();

            if (!dir.Languages.ContainsKey(lang))
            {
                Log.Warn($"Cliloc: the pack has no '{lang}'; rolled back to enu");
                lang = "enu";
            }

            var state = new State { FromPack = true, Language = lang, Lang = PackTable(store, dir, lang) };

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
                Log.Error($"Cliloc: the pack has no '{lang}'; reading Cliloc.* files");

                return null;
            }

            var table = ClilocStore.ReadTable(store, record, out string why);

            if (table == null)
            {
                Log.Error($"Cliloc: the pack's '{lang}' record has {why}; reading Cliloc.* files");
            }

            return table;
        }

        // The loose files: Cliloc.<lang>, rolled back to Cliloc.enu when it is missing (as before), and Cliloc.enu under
        // any other language. Null when not even Cliloc.enu exists, or on a reload (keepOld) when the English base under
        // another language is missing.
        private State LanguagesFromFiles(string requested, bool keepOld)
        {
            string lang = requested;
            string path = FileManager.GetUOFilePath($"Cliloc.{lang}");

            if (!File.Exists(path))
            {
                Log.Warn($"'Cliloc.{lang}' not found. Rolled back to Cliloc.enu");
                lang = "enu";
                path = FileManager.GetUOFilePath("Cliloc.enu");
            }

            if (!File.Exists(path))
            {
                Log.Error($"cliloc not found: '{path}'");

                return null;
            }

            var state = new State { Language = lang.ToLowerInvariant() };

            if (!string.Equals(lang, "enu", StringComparison.OrdinalIgnoreCase))
            {
                string enupath = FileManager.GetUOFilePath("Cliloc.enu");

                if (File.Exists(enupath))
                {
                    state.Enu = ReadCliloc(enupath);
                }
                else
                {
                    // the client reads Cliloc.enu under every language: without it, English-only numbers would vanish
                    Log.Error($"Cliloc: '{enupath}' is missing; 'Cliloc.{lang}' needs it under it{(keepOld ? "" : " (serving the language alone)")}");

                    if (keepOld)
                    {
                        return null;
                    }
                }
            }

            state.Lang = ReadCliloc(path);

            return state;
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

        // A Cliloc.* file indexed without creating its strings: each entry's text is the slice ReadUTF8(length) would
        // decode (cut at the first NUL, clipped to the file), and later duplicates win, as before.
        static ClilocTable ReadCliloc(string path)
        {
            var buf = File.ReadAllBytes(path);

            var output = buf[3] == 0x8E /*|| FileManager.Version >= ClientVersion.CV_7010400*/ ? BwtDecompress.Decompress(buf) : buf;

            var reader = new StackDataReader(output);
            reader.ReadInt32LE();
            reader.ReadInt16LE();

            var slices = new Dictionary<int, (int Start, int Length)>();

            while (reader.Remaining > 0)
            {
                var number = reader.ReadInt32LE();
                var flag = reader.ReadUInt8();
                var length = reader.ReadInt16LE();

                slices[number] = Utf8Slice(ref reader, output, length);
            }

            var entries = new (int, int, int)[slices.Count];
            int n = 0;

            foreach (var (id, (start, len)) in slices)
            {
                entries[n++] = (id, start, len);
            }

            return ClilocTable.FromSlices(output, entries);
        }

        /// <summary>
        /// The bytes StackDataReader.ReadUTF8(length) decodes at the reader's position, and the same advance: empty
        /// for length 0 or at the end; else up to <paramref name="length"/> bytes (all that remain for a negative
        /// length), cut at the first NUL; the position moves by the larger of that size (plus the NUL for a negative
        /// length) and <paramref name="length"/>.
        /// </summary>
        internal static (int Start, int Length) Utf8Slice(ref StackDataReader reader, ReadOnlySpan<byte> data, int length)
        {
            int start = reader.Position;

            if (length == 0 || reader.Position + 1 > reader.Length)
            {
                return (start, 0);
            }

            bool fixedLength = length > 0;
            int remaining = reader.Remaining;
            int size = fixedLength ? Math.Min(length, remaining) : remaining;
            int index = data.Slice(start, size).IndexOf((byte)0);
            size = index < 0 ? size : index;
            reader.Skip(Math.Max(size + (!fixedLength && index >= 0 ? 1 : 0), length));

            return (start, size);
        }

        public override void ClearResources()
        {
            _state = new State();
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
        public unsafe string Translate(int clilocNum, string arg = "", bool capitalize = false)
        {
            string baseCliloc = GetString(clilocNum);

            if (arg == null)
            {
                arg = "";
            }

            var roChars = arg.AsSpan();


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

                    if (!int.TryParse(sb.RawChars.Slice(start, count).ToString(), out index))
                    {
                        return $"MegaCliloc: error for {clilocNum}";
                    }

                    --index;

                    var a = index < 0 || index >= totalArgs ? string.Empty.AsSpan() : arg.AsSpan().Slice(locations[index].Item1, locations[index].Item2 - locations[index].Item1);

                    if (a.Length > 1)
                    {
                        if (a[0] == '#')
                        {
                            if (int.TryParse(a.Slice(1).ToString(), out int id1))
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
                        else if (has_arguments && int.TryParse(a.ToString(), out int clil))
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

                baseCliloc = sb.ToString();

                sb.Dispose();

                if (capitalize)
                {
                    baseCliloc = StringHelper.CapitalizeAllWords(baseCliloc);
                }

                return baseCliloc;
            }
        }
    }
}
