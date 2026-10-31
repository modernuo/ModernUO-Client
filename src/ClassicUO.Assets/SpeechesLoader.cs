// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Uo.Pack;

namespace ClassicUO.Assets
{
    public sealed class SpeechesLoader : UOFileLoader
    {
        private SpeechEntry[] _speech;

        public SpeechesLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        /// <summary>
        /// Read the keyword table from the pack's text/speech.bin when it has one (set before <see cref="Load"/>).
        /// CUO_PACK_TEXT=0 starts it false: speech.mul even with a text section.
        /// </summary>
        public bool UsePack = Environment.GetEnvironmentVariable("CUO_PACK_TEXT") != "0";

        /// <summary>True when the keyword table came from the pack.</summary>
        public bool FromPack { get; private set; }

        /// <summary>The keyword entries, in file order (tests).</summary>
        public SpeechEntry[] Entries => _speech;

        public override void Load()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            string packPath = UsePack ? FileManager.Pack?.PathOf(TextConst.SpeechFile) : null;
            FromPack = false;

            if (packPath != null && File.Exists(packPath))
            {
                try
                {
                    var texts = SpeechFile.Read(packPath);
                    var entries = new SpeechEntry[texts.Length];

                    for (int i = 0; i < texts.Length; i++)
                    {
                        entries[i] = new SpeechEntry(texts[i].Id, texts[i].Text);
                    }

                    _speech = entries;
                    FromPack = true;
                    Log.Trace($"Speech: pack, {entries.Length} entries in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0.0} ms");

                    return;
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
                {
                    Log.Error($"Speech: '{packPath}' is invalid ({e.Message}); reading speech.mul");
                }
            }
            else if (FileManager.Pack != null)
            {
                Log.Trace(UsePack ? "Speech: the pack has no speech table; reading speech.mul" : "Speech: CUO_PACK_TEXT=0; reading speech.mul");
            }

            LoadMul();
        }

        private void LoadMul()
        {
            string path = FileManager.GetUOFilePath("speech.mul");

            if (!File.Exists(path))
            {
                _speech = Array.Empty<SpeechEntry>();

                return;
            }

            var file = new UOFileMul(path);
            var entries = new List<SpeechEntry>();

            var buf = new byte[256];
            while (file.Position < file.Length)
            {
                file.Read(buf.AsSpan(0, sizeof(ushort) * 2));
                var id = BinaryPrimitives.ReadUInt16BigEndian(buf);
                var length = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(sizeof(ushort)));

                if (length > 0)
                {
                    if (length > buf.Length)
                        buf = new byte[length];

                    file.Read(buf.AsSpan(0, length));
                    var text = string.Intern(Encoding.UTF8.GetString(buf.AsSpan(0, length)));

                    entries.Add(new SpeechEntry(id, text));
                }
            }

            _speech = entries.ToArray();
            file.Dispose();
        }

        public bool IsMatch(string input, in SpeechEntry entry)
        {
            string[] split = entry.Keywords;
            //string[] words = input.Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < split.Length; i++)
            {
                if (split[i].Length > input.Length || split[i].Length == 0)
                {
                    continue;
                }

                if (!entry.CheckStart)
                {
                    if (input.IndexOf(split[i], 0, split[i].Length, StringComparison.InvariantCultureIgnoreCase) == -1)
                    {
                        continue;
                    }
                }

                if (!entry.CheckEnd)
                {
                    if (input.IndexOf(split[i], input.Length - split[i].Length, StringComparison.InvariantCultureIgnoreCase) == -1)
                    {
                        continue;
                    }
                }

                int idx = input.IndexOf(split[i], StringComparison.InvariantCultureIgnoreCase);
                while (idx >= 0)
                {
                    // "bank" or " bank" or "bank " or " bank " or "!bank" or "bank!"
                    if ((idx - 1 < 0 || char.IsWhiteSpace(input[idx - 1]) || !char.IsLetter(input[idx - 1])) &&
                        (idx + split[i].Length >= input.Length || char.IsWhiteSpace(input[idx + split[i].Length]) || !char.IsLetter(input[idx + split[i].Length]) ))
                    {
                        return true;
                    }

                    idx = input.IndexOf(split[i], idx + 1, StringComparison.InvariantCultureIgnoreCase);
                }
            }

            return false;
        }

        public List<SpeechEntry> GetKeywords(string text)
        {
            List<SpeechEntry> list = new List<SpeechEntry>();

            if (FileManager.Version < ClientVersion.CV_305D)
            {
                return list;
            }

            text = text.TrimStart(' ').TrimEnd(' ');

            for (int i = 0; i < _speech.Length; i++)
            {
                SpeechEntry entry = _speech[i];

                if (IsMatch(text, in entry))
                {
                    list.Add(entry);
                }
            }

            list.Sort();

            return list;
        }
    }

    public readonly struct SpeechEntry : IComparable<SpeechEntry>
    {
        public SpeechEntry(int id, string keyword)
        {
            KeywordID = (short) id;

            Keywords = keyword.Split
            (
                new[]
                {
                    '*'
                },
                StringSplitOptions.RemoveEmptyEntries
            );

            CheckStart = keyword.Length > 0 && keyword[0] == '*';
            CheckEnd = keyword.Length > 0 && keyword[keyword.Length - 1] == '*';
        }

        public string[] Keywords { get; }

        public short KeywordID { get; }

        public bool CheckStart { get; }

        public bool CheckEnd { get; }

        public int CompareTo(SpeechEntry obj)
        {
            if (KeywordID < obj.KeywordID)
            {
                return -1;
            }

            return KeywordID > obj.KeywordID ? 1 : 0;
        }
    }
}