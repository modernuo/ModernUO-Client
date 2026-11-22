// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Game.UI.Controls;

namespace ClassicUO.Game.UI.Controls
{
    /// <summary>One argument of a server-gump layout entry: a range of the layout string.</summary>
    internal readonly struct GumpToken
    {
        public readonly int Start;
        public readonly int Length;

        public GumpToken(int start, int length)
        {
            Start = start;
            Length = length;
        }
    }

    /// <summary>
    /// The arguments of one layout entry, as the server-gump controls read them: either ranges of the layout string
    /// (gump_layout_v2) or the token strings of the old tokenizer. Reading an argument allocates nothing.
    /// </summary>
    internal readonly ref struct GumpArgs
    {
        private readonly string _source;
        private readonly ReadOnlySpan<GumpToken> _tokens;
        private readonly List<string> _list;

        public GumpArgs(string source, ReadOnlySpan<GumpToken> tokens)
        {
            _source = source;
            _tokens = tokens;
            _list = null;
        }

        public GumpArgs(List<string> list)
        {
            _source = null;
            _tokens = default;
            _list = list;
        }

        public static implicit operator GumpArgs(List<string> list) => new GumpArgs(list);

        public int Count => _list != null ? _list.Count : _tokens.Length;

        public ReadOnlySpan<char> this[int index]
        {
            get
            {
                if (_list != null)
                {
                    return _list[index];
                }

                GumpToken t = _tokens[index];

                return _source.AsSpan(t.Start, t.Length);
            }
        }

        /// <summary>Argument <paramref name="index"/> equals <paramref name="value"/> (ordinal, as the old string compares).</summary>
        public bool Is(int index, string value) => this[index].SequenceEqual(value);

        /// <summary>Argument <paramref name="index"/> as a new string (only for text a control keeps or a log line).</summary>
        public string GetString(int index) => _list != null ? _list[index] : this[index].ToString();
    }
}

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>The keyword of a server-gump layout entry (its first token, matched ignoring case).</summary>
    internal enum GumpEntryKind : byte
    {
        None,
        Unknown,
        Button,
        ButtonTileArt,
        CheckerTrans,
        CroppedText,
        TilePicAsGumpPic,
        GumpPic,
        GumpPicTiled,
        HtmlGump,
        XmfHtmlGump,
        XmfHtmlGumpColor,
        XmfHtmlTok,
        Page,
        ResizePic,
        Text,
        TextEntryLimited,
        TextEntry,
        TilePicHue,
        TilePic,
        NoClose,
        NoDispose,
        NoMove,
        Group,
        EndGroup,
        Radio,
        Checkbox,
        Tooltip,
        ItemProperty,
        NoResize,
        MasterGump,
        PicInPicHued,
        PicInPicPHued,
        PicInPic,
        Terminator,
        GumpPicHued,
        GumpPicPHued,
        ToggleLimitGumpScale
    }

    /// <summary>One layout entry: its kind and its tokens (<see cref="First"/> .. First + Count in <see cref="GumpLayout.Tokens"/>; token 0 is the keyword).</summary>
    internal readonly struct GumpEntry
    {
        public readonly GumpEntryKind Kind;
        public readonly int First;
        public readonly int Count;

        public GumpEntry(GumpEntryKind kind, int first, int count)
        {
            Kind = kind;
            First = first;
            Count = count;
        }
    }

    /// <summary>
    /// gump_layout_v2 (G3): the parsed form of a server-gump layout (0xB0/0xDD). Immutable after the parse, so a layout
    /// cache (gump_layout_cache) can hand the same instance to every resend of the same layout. Holds the layout string and
    /// token ranges into it; no token strings.
    /// </summary>
    internal sealed class GumpLayout
    {
        public readonly string Source;
        public readonly GumpToken[] Tokens;
        public readonly GumpEntry[] Entries;

        private GumpLayout(string source, GumpToken[] tokens, GumpEntry[] entries)
        {
            Source = source;
            Tokens = tokens;
            Entries = entries;
        }

        public GumpArgs Args(in GumpEntry entry) => new GumpArgs(Source, Tokens.AsSpan(entry.First, entry.Count));

        /// <summary>Approximate managed bytes the instance holds besides the layout string (for the cache's size log).</summary>
        public int HeldBytes => 24 + 16 + Tokens.Length * 8 + 16 + Entries.Length * 12;

        // parse scratch, main thread only (server gumps are opened by packet handlers)
        private static GumpToken[] _tokenScratch = new GumpToken[512];
        private static GumpEntry[] _entryScratch = new GumpEntry[128];

        /// <summary>
        /// Parses <paramref name="layout"/> in one pass with the old two-tokenizer rules (TextFileParser with '{' '}' then
        /// ' ' ',' '@' '@'): an entry is the trimmed text of a '{...}' (or a bare word outside braces) on one line, its tokens
        /// are split on ' ' and ',' with '@...@' kept whole, and an '@' that closes a quoted token opens the next one.
        /// Entries without tokens are kept (Kind None): the old path counts them.
        /// </summary>
        public static GumpLayout Parse(string layout)
        {
            int tokenCount = 0;
            int entryCount = 0;
            int n = layout.Length;
            int pos = 0;

            while (pos < n)
            {
                int eol = layout.IndexOf('\n', pos);

                if (eol < 0)
                {
                    eol = n;
                }

                while (pos < eol && layout[pos] == ' ')
                {
                    pos++;
                }

                while (pos < eol)
                {
                    int start,
                        end;

                    if (layout[pos] == '{')
                    {
                        start = ++pos;

                        while (pos < eol && layout[pos] != '}')
                        {
                            pos++;
                        }

                        end = pos;

                        if (pos < eol)
                        {
                            pos++;
                        }
                    }
                    else
                    {
                        start = pos;

                        while (pos < eol && layout[pos] != ' ' && layout[pos] != '{')
                        {
                            pos++;
                        }

                        end = pos;
                    }

                    while (start < end && char.IsWhiteSpace(layout[start]))
                    {
                        start++;
                    }

                    while (end > start && char.IsWhiteSpace(layout[end - 1]))
                    {
                        end--;
                    }

                    if (end > start)
                    {
                        int first = tokenCount;
                        TokenizeEntry(layout, start, end, ref tokenCount);

                        if (entryCount == _entryScratch.Length)
                        {
                            Array.Resize(ref _entryScratch, entryCount * 2);
                        }

                        int count = tokenCount - first;
                        GumpEntryKind kind = count == 0
                            ? GumpEntryKind.None
                            : KindOf(layout.AsSpan(_tokenScratch[first].Start, _tokenScratch[first].Length));
                        _entryScratch[entryCount++] = new GumpEntry(kind, first, count);
                    }

                    while (pos < eol && layout[pos] == ' ')
                    {
                        pos++;
                    }
                }

                pos = eol + 1;
            }

            return new GumpLayout(
                layout,
                tokenCount == 0 ? Array.Empty<GumpToken>() : _tokenScratch.AsSpan(0, tokenCount).ToArray(),
                entryCount == 0 ? Array.Empty<GumpEntry>() : _entryScratch.AsSpan(0, entryCount).ToArray()
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsArgDelimiter(char c) => c == ' ' || c == ',';

        private static void TokenizeEntry(string s, int pos, int eol, ref int tokenCount)
        {
            while (pos < eol && IsArgDelimiter(s[pos]))
            {
                pos++;
            }

            while (pos < eol)
            {
                int start;

                if (s[pos] == '@')
                {
                    // the closing '@' is not consumed: it opens the next quoted token (TextFileParser with equal quotes)
                    start = ++pos;

                    while (pos < eol && s[pos] != '@')
                    {
                        pos++;
                    }
                }
                else
                {
                    start = pos;

                    while (pos < eol && !IsArgDelimiter(s[pos]) && s[pos] != '@')
                    {
                        pos++;
                    }
                }

                if (pos > start)
                {
                    if (tokenCount == _tokenScratch.Length)
                    {
                        Array.Resize(ref _tokenScratch, tokenCount * 2);
                    }

                    _tokenScratch[tokenCount++] = new GumpToken(start, pos - start);
                }

                while (pos < eol && IsArgDelimiter(s[pos]))
                {
                    pos++;
                }
            }
        }

        /// <summary>The kind of keyword <paramref name="k"/>: ordinal ignore case (the old InvariantCultureIgnoreCase compares are ordinal under InvariantGlobalization).</summary>
        internal static GumpEntryKind KindOf(ReadOnlySpan<char> k)
        {
            switch (k.Length)
            {
                case 1:
                    return k[0] == '\0' ? GumpEntryKind.Terminator : GumpEntryKind.Unknown;
                case 4:
                    if (Eq(k, "page")) return GumpEntryKind.Page;
                    if (Eq(k, "text")) return GumpEntryKind.Text;

                    break;
                case 5:
                    if (Eq(k, "group")) return GumpEntryKind.Group;
                    if (Eq(k, "radio")) return GumpEntryKind.Radio;

                    break;
                case 6:
                    if (Eq(k, "button")) return GumpEntryKind.Button;
                    if (Eq(k, "nomove")) return GumpEntryKind.NoMove;

                    break;
                case 7:
                    if (Eq(k, "gumppic")) return GumpEntryKind.GumpPic;
                    if (Eq(k, "tilepic")) return GumpEntryKind.TilePic;
                    if (Eq(k, "noclose")) return GumpEntryKind.NoClose;
                    if (Eq(k, "tooltip")) return GumpEntryKind.Tooltip;

                    break;
                case 8:
                    if (Eq(k, "htmlgump")) return GumpEntryKind.HtmlGump;
                    if (Eq(k, "endgroup")) return GumpEntryKind.EndGroup;
                    if (Eq(k, "checkbox")) return GumpEntryKind.Checkbox;
                    if (Eq(k, "noresize")) return GumpEntryKind.NoResize;
                    if (Eq(k, "picinpic")) return GumpEntryKind.PicInPic;

                    break;
                case 9:
                    if (Eq(k, "resizepic")) return GumpEntryKind.ResizePic;
                    if (Eq(k, "textentry")) return GumpEntryKind.TextEntry;
                    if (Eq(k, "nodispose")) return GumpEntryKind.NoDispose;

                    break;
                case 10:
                    if (Eq(k, "tilepichue")) return GumpEntryKind.TilePicHue;
                    if (Eq(k, "xmfhtmltok")) return GumpEntryKind.XmfHtmlTok;
                    if (Eq(k, "mastergump")) return GumpEntryKind.MasterGump;

                    break;
                case 11:
                    if (Eq(k, "xmfhtmlgump")) return GumpEntryKind.XmfHtmlGump;
                    if (Eq(k, "croppedtext")) return GumpEntryKind.CroppedText;
                    if (Eq(k, "gumppichued")) return GumpEntryKind.GumpPicHued;

                    break;
                case 12:
                    if (Eq(k, "checkertrans")) return GumpEntryKind.CheckerTrans;
                    if (Eq(k, "gumppictiled")) return GumpEntryKind.GumpPicTiled;
                    if (Eq(k, "itemproperty")) return GumpEntryKind.ItemProperty;
                    if (Eq(k, "picinpichued")) return GumpEntryKind.PicInPicHued;
                    if (Eq(k, "gumppicphued")) return GumpEntryKind.GumpPicPHued;

                    break;
                case 13:
                    if (Eq(k, "buttontileart")) return GumpEntryKind.ButtonTileArt;
                    if (Eq(k, "picinpicphued")) return GumpEntryKind.PicInPicPHued;

                    break;
                case 16:
                    if (Eq(k, "xmfhtmlgumpcolor")) return GumpEntryKind.XmfHtmlGumpColor;
                    if (Eq(k, "tilepicasgumppic")) return GumpEntryKind.TilePicAsGumpPic;
                    if (Eq(k, "textentrylimited")) return GumpEntryKind.TextEntryLimited;

                    break;
                case 20:
                    if (Eq(k, "togglelimitgumpscale")) return GumpEntryKind.ToggleLimitGumpScale;

                    break;
            }

            return GumpEntryKind.Unknown;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Eq(ReadOnlySpan<char> a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }
}
