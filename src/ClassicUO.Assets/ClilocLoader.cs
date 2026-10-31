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
        private string _cliloc;
        private string _lang;

        // What GetString looks up, in order: the shard's own strings (Clilocs.txt), the language's table, then
        // Cliloc.enu's (the language file overrides English, as before). Each table holds its UTF-8 text and an index
        // sorted by number; a string is created on its first lookup and cached (ClilocTable, thread-safe). The state is
        // replaced as a whole on (re)load, so readers on other threads (plugins, workers) see one consistent set.
        private sealed class State
        {
            public Dictionary<int, string> Ours;
            public ClilocTable Lang;
            public ClilocTable Enu;
            public bool FromPack;
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

        /// <summary>True when cliloc comes from the pack.</summary>
        public bool FromPack => _state.FromPack;

        /// <summary>Tests and measurements: the loaded tables (the language's, and Cliloc.enu's when the language isn't English).</summary>
        public ClilocTable LanguageTable => _state.Lang;
        public ClilocTable EnglishTable => _state.Enu;

        public void Load(string lang)
        {
            if (string.IsNullOrEmpty(lang))
            {
                lang = "enu";
            }

            _lang = lang;
            _cliloc = $"Cliloc.{lang}";
            Log.Trace($"searching for: '{_cliloc}'");

            if (!PackHasLanguage(lang) && !File.Exists(FileManager.GetUOFilePath(_cliloc)))
            {
                Log.Warn($"'{_cliloc}' not found. Rolled back to Cliloc.enu");

                _cliloc = "Cliloc.enu";
                _lang = "enu";
            }

            Load();
        }

        public void Reload()
        {
            _state = new State();
            Load();
        }

        public override void Load()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long a0 = GC.GetAllocatedBytesForCurrentThread();

            if (string.IsNullOrEmpty(_cliloc))
            {
                _cliloc = "Cliloc.enu";
                _lang = "enu";
            }

            var state = LoadPack() ?? LoadMul();

            if (state == null)
            {
                return;
            }

            state.Ours = ReadOurs();
            _state = state;
            Log.Trace($"Cliloc loaded in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0.0} ms, {(GC.GetAllocatedBytesForCurrentThread() - a0) / 1024} KB allocated ({(state.FromPack ? "pack" : "MUL")}, '{_lang}': {state.Lang.Count} strings{(state.Enu != null ? $" over {state.Enu.Count} English" : "")})");
        }

        private string PackPath => UsePack ? FileManager.Pack?.PathOf(TextConst.ClilocFile) : null;

        private bool PackHasLanguage(string lang)
        {
            string path = PackPath;

            if (path == null || !File.Exists(path))
            {
                return false;
            }

            try
            {
                using var store = RecordStore.Open(path, TextConst.MagicCliloc);

                return FindLanguage(store, lang) >= 0;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static int FindLanguage(RecordStore store, string lang)
        {
            for (int i = 0; i < store.Slots.Length; i++)
            {
                if (store.Slots[i].Record == 0)
                {
                    continue;
                }

                ref readonly var r = ref store.Records[store.Slots[i].Record - 1];

                if (string.Equals(ClilocTable.UnpackLanguage(r.Meta0, r.Meta1), lang, StringComparison.OrdinalIgnoreCase))
                {
                    return (int)store.Slots[i].Record - 1;
                }
            }

            return -1;
        }

        // The pack's text/cliloc.bin: the language's record and, for another language, enu's. Null when the pack has no
        // cliloc section or it can't be used (then the Cliloc.* files serve, as before).
        private State LoadPack()
        {
            string path = PackPath;

            if (path == null || !File.Exists(path))
            {
                if (FileManager.Pack != null)
                {
                    Log.Trace(UsePack ? "Cliloc: the pack has no cliloc section; reading Cliloc.* files" : "Cliloc: CUO_PACK_TEXT=0; reading Cliloc.* files");
                }

                return null;
            }

            try
            {
                using var store = RecordStore.Open(path, TextConst.MagicCliloc);
                string lang = _lang.ToLowerInvariant();
                int rec = FindLanguage(store, lang);

                if (rec < 0)
                {
                    Log.Warn($"Cliloc: the pack has no '{lang}'; rolled back to enu");
                    lang = "enu";
                    rec = FindLanguage(store, lang);
                }

                var state = new State { FromPack = true, Lang = ReadTable(store, rec, lang) };

                if (state.Lang == null)
                {
                    return null;
                }

                if (lang != "enu")
                {
                    // the client reads Cliloc.enu under every language: a language file overrides it id by id
                    state.Enu = ReadTable(store, FindLanguage(store, "enu"), "enu");

                    if (state.Enu == null)
                    {
                        return null;
                    }
                }

                return state;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                Log.Error($"Cliloc: '{path}' is invalid ({e.Message}); reading Cliloc.* files");

                return null;
            }
        }

        private static ClilocTable ReadTable(RecordStore store, int record, string lang)
        {
            if (record < 0)
            {
                Log.Error($"Cliloc: the pack has no '{lang}'; reading Cliloc.* files");

                return null;
            }

            ref readonly var r = ref store.Records[record];

            if (r.RawBytes < ClilocTable.HeaderBytes || r.RawBytes > ClilocTable.MaxRawBytes ||
                (r.Codec == StoreConst.CodecBrotli && r.StoredBytes > System.IO.Compression.BrotliEncoder.GetMaxCompressedLength((int)r.RawBytes)))
            {
                Log.Error($"Cliloc: the pack's '{lang}' record has an impossible size ({r.RawBytes} bytes); reading Cliloc.* files");

                return null;
            }

            var data = new byte[r.RawBytes];
            store.Read(r, data);
            var table = ClilocTable.TryParse(data);

            if (table == null)
            {
                Log.Error($"Cliloc: the pack's '{lang}' record is malformed; reading Cliloc.* files");
            }

            return table;
        }

        private State LoadMul()
        {
            string path = FileManager.GetUOFilePath(_cliloc);

            if (!File.Exists(path))
            {
                Log.Error($"cliloc not found: '{path}'");
                return null;
            }

            var state = new State();

            if (string.Compare(_cliloc, "cliloc.enu", StringComparison.InvariantCultureIgnoreCase) != 0)
            {
                string enupath = FileManager.GetUOFilePath("Cliloc.enu");
                state.Enu = ReadCliloc(enupath);
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
        /// The file is read after Cliloc.enu and after the language file, and this dictionary is
        /// last-write-wins - which is how the language file already overrides the English one. So
        /// the same mechanism adds new numbers and corrects existing ones, and neither needs the
        /// compressed original touched.
        ///
        /// Plain text rather than the client's own format on purpose: a line somebody can read,
        /// diff and edit beats a BWT-compressed blob for a file that will hold a few dozen
        /// entries. Anything above 3,100,000 is free - the client's own stop at 3,011,032.
        ///
        ///     # Clilocs.txt, in the client folder
        ///     3100001    Breed
        ///     3100002    Send to kennel
        /// </summary>
        private Dictionary<int, string> ReadOurs()
        {
            string path = FileManager.GetUOFilePath("Clilocs.txt");

            if (!File.Exists(path))
            {
                return null;
            }

            var added = 0;
            var ours = new Dictionary<int, string>();

            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    string trimmed = line.Trim();

                    if (trimmed.Length == 0 || trimmed[0] == '#')
                    {
                        continue;
                    }

                    // A tab or the first run of spaces separates the number from the text, so the
                    // text itself may contain anything including tabs after that point.
                    int at = trimmed.IndexOfAny(new[] { '\t', ' ' });

                    if (at <= 0)
                    {
                        continue;
                    }

                    if (!int.TryParse(trimmed.AsSpan(0, at), out int number))
                    {
                        continue;
                    }

                    ours[number] = string.Intern(trimmed[(at + 1)..].TrimStart());
                    added++;
                }
            }
            catch (IOException e)
            {
                Log.Warn($"could not read {path}: {e.Message}");
                return ours;
            }

            if (added > 0)
            {
                Log.Trace($"{added} cliloc string(s) of our own from {Path.GetFileName(path)}");
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

        /// <summary>The string of <paramref name="clilocNum"/>: the shard's own, the language's, else English. Thread-safe.</summary>
        public bool TryGetString(int clilocNum, out string text)
        {
            var state = _state;

            if (state.Ours != null && state.Ours.TryGetValue(clilocNum, out text))
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
