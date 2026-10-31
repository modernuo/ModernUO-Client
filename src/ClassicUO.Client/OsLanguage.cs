using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using ClassicUO.Utility.Logging;
using SDL3;

namespace ClassicUO
{
    /// <summary>One entry of the OS's preferred languages: ISO-639 language and an ISO-3166 country (or a script such as "Hant"), either may be null.</summary>
    internal readonly record struct PreferredLocale(string Language, string Country)
    {
        public override string ToString() => string.IsNullOrEmpty(Country) ? Language ?? "" : $"{Language}_{Country}";
    }

    /// <summary>
    /// Picks the client's language from the OS. UO language codes are the Windows three-letter language names (ENU, DEU,
    /// CHT...): the first two letters are the ISO-639-1 language. The user's preferred languages are matched against the
    /// cliloc languages that exist (the pack's records or the Cliloc.* files), so a custom server's extra languages work
    /// and no table needs maintaining. <see cref="Choose"/> is pure; <see cref="Preferred"/> (SDL) and
    /// <see cref="WindowsExactCode"/> (GetLocaleInfoEx) are the thin adapters.
    /// </summary>
    internal static class OsLanguage
    {
        public const string Default = "ENU";

        /// <summary>
        /// True for a language code that names no language: the invariant culture's "IVL" (what the OS language reads
        /// as under invariant globalization; earlier runs may have saved it in settings.json) or ZZZ (no language).
        /// </summary>
        public static bool IsNoLanguage(string code) =>
            string.Equals(code?.Trim(), "IVL", StringComparison.OrdinalIgnoreCase) || string.Equals(code?.Trim(), "ZZZ", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True for the languages whose text needs the Unicode fonts (the ASCII fonts have no CJK glyphs): Chinese
        /// (Traditional and Simplified), Korean, Japanese. Every control that picks a font by language asks this.
        /// </summary>
        public static bool NeedsUnicodeFonts(string code)
        {
            string c = code?.Trim();

            return string.Equals(c, "CHT", StringComparison.OrdinalIgnoreCase) || string.Equals(c, "CHS", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(c, "KOR", StringComparison.OrdinalIgnoreCase) || string.Equals(c, "JPN", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The language to use, upper-case, always one of <paramref name="available"/> (or ENU when nothing matches).
        /// A profile language that is available wins. Otherwise each preferred locale in order: its language subtag
        /// (2 letters; "fil" etc. are skipped) against the first two letters of the available codes; one hit is used;
        /// several are broken by the country: the exact code from <paramref name="exactCode"/> (Windows) if it is among
        /// them, else Chinese by Traditional (script Hant, or TW/HK/MO) vs Simplified, else English as ENU, else the
        /// alphabetically first. Nothing matched: ENU.
        /// </summary>
        public static string Choose(string profile, IReadOnlyCollection<string> available, IReadOnlyList<PreferredLocale> preferred, Func<PreferredLocale, string> exactCode, out string why)
        {
            var codes = new SortedSet<string>(StringComparer.Ordinal);

            if (available != null)
            {
                foreach (string a in available)
                {
                    // only three-letter codes: the language packet carries three characters (as discovery does)
                    if (a != null && ClassicUO.Assets.ClilocLoader.IsLanguageCode(a.Trim()))
                    {
                        codes.Add(a.Trim().ToUpperInvariant());
                    }
                }
            }

            string wanted = string.IsNullOrWhiteSpace(profile) || IsNoLanguage(profile) ? null : profile.Trim().ToUpperInvariant();

            if (wanted != null && codes.Contains(wanted))
            {
                why = $"profile language '{wanted}'";

                return wanted;
            }

            string profileNote = wanted != null ? $"profile language '{wanted}' is not available; " : "";

            if (preferred != null)
            {
                foreach (PreferredLocale p in preferred)
                {
                    Split(p, out string lang, out bool traditional);

                    if (lang == null)
                    {
                        continue;
                    }

                    var hits = new List<string>();

                    foreach (string c in codes)
                    {
                        if (c.StartsWith(CodePrefix(lang), StringComparison.OrdinalIgnoreCase))
                        {
                            hits.Add(c);
                        }
                    }

                    if (hits.Count == 0)
                    {
                        continue;
                    }

                    string pick = hits[0];
                    string how = "only match";

                    if (hits.Count > 1)
                    {
                        string exact = null;

                        try
                        {
                            exact = exactCode?.Invoke(p)?.ToUpperInvariant();
                        }
                        catch (Exception e)
                        {
                            Log.Trace($"language: exact code lookup failed ({e.Message})");
                        }

                        if (exact != null && hits.Contains(exact))
                        {
                            pick = exact;
                            how = "exact OS code";
                        }
                        else if (lang == "zh")
                        {
                            pick = (hits.Contains(traditional ? "CHT" : "CHS") ? (traditional ? "CHT" : "CHS") : null) ?? hits[0];
                            how = traditional ? "Traditional Chinese" : "Simplified Chinese";
                        }
                        else if (lang == "en")
                        {
                            pick = hits.Contains("ENU") ? "ENU" : hits[0];
                            how = "English default";
                        }
                        else
                        {
                            how = "first of several";
                        }
                    }

                    why = $"{profileNote}OS locale '{p}' ({how})";

                    return pick;
                }
            }

            why = $"{profileNote}no OS locale matches an available language; default";

            return Default;
        }

        // The first two letters of the UO codes of a language: the ISO-639 code, except for the two Windows names that
        // were not built from it: Japanese is JPN (not "JAx") and Chinese is CHT/CHS (not "ZHx").
        private static string CodePrefix(string lang) => lang switch
        {
            "ja" => "jp",
            "zh" => "ch",
            _ => lang
        };

        // The language subtag (lower-case, exactly 2 letters, else null), and whether the locale is Traditional Chinese.
        // A tag such as "zh-Hant-TW" or "zh_TW" may sit in the language field; the country field holds a region or script.
        private static void Split(PreferredLocale p, out string lang, out bool traditional)
        {
            lang = null;
            traditional = false;

            if (string.IsNullOrWhiteSpace(p.Language))
            {
                return;
            }

            string[] parts = p.Language.Split('-', '_');

            if (parts[0].Length != 2 || !char.IsAsciiLetter(parts[0][0]) || !char.IsAsciiLetter(parts[0][1]))
            {
                return;
            }

            lang = parts[0].ToLowerInvariant();

            var rest = new List<string>();

            for (int i = 1; i < parts.Length; i++)
            {
                rest.Add(parts[i]);
            }

            if (!string.IsNullOrWhiteSpace(p.Country))
            {
                rest.AddRange(p.Country.Split('-', '_'));
            }

            foreach (string r in rest)
            {
                if (r.Equals("Hant", StringComparison.OrdinalIgnoreCase) || r.Equals("TW", StringComparison.OrdinalIgnoreCase) || r.Equals("HK", StringComparison.OrdinalIgnoreCase) || r.Equals("MO", StringComparison.OrdinalIgnoreCase))
                {
                    traditional = true;
                }
            }
        }

        // ---- adapters ----

        /// <summary>SDL3's preferred locales, in order (SDL_HINT_PREFERRED_LOCALES overrides them). Empty when SDL gives none.</summary>
        public static List<PreferredLocale> Preferred()
        {
            var list = new List<PreferredLocale>();

            try
            {
                IntPtr arr = SDL.SDL_GetPreferredLocales(out int count);

                if (arr == IntPtr.Zero)
                {
                    return list;
                }

                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr loc = Marshal.ReadIntPtr(arr, i * IntPtr.Size);

                        if (loc == IntPtr.Zero)
                        {
                            continue;
                        }

                        IntPtr lang = Marshal.ReadIntPtr(loc, 0);
                        IntPtr country = Marshal.ReadIntPtr(loc, IntPtr.Size);
                        list.Add(new PreferredLocale(
                            lang == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(lang),
                            country == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(country)));
                    }
                }
                finally
                {
                    SDL.SDL_free(arr);
                }
            }
            catch (Exception e)
            {
                Log.Warn($"language: cannot read the OS preferred languages ({e.Message})");
            }

            return list;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetLocaleInfoEx")]
        private static extern int GetLocaleInfoEx(string localeName, uint lcType, StringBuilder data, int size);

        private const uint LOCALE_SABBREVLANGNAME = 0x00000003;

        /// <summary>Windows only: the OS's own three-letter code for the locale (GetLocaleInfoEx), or null. es-ES gives ESN here, not UO's ESP: only a tie-breaker.</summary>
        public static string WindowsExactCode(PreferredLocale p)
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(p.Language))
            {
                return null;
            }

            string name = p.Language.Replace('_', '-');

            if (!string.IsNullOrWhiteSpace(p.Country))
            {
                name += "-" + p.Country.Replace('_', '-');
            }

            var sb = new StringBuilder(16);

            return GetLocaleInfoEx(name, LOCALE_SABBREVLANGNAME, sb, sb.Capacity) > 0 ? sb.ToString() : null;
        }

        /// <summary>The whole selection: reads the OS list, chooses, logs one Info line, returns the code.</summary>
        public static string Select(string profile, IReadOnlyCollection<string> available)
        {
            List<PreferredLocale> preferred = Preferred();
            string code = Choose(profile, available, preferred, WindowsExactCode, out string why);
            Log.Info($"language: {code} - {why} (OS preferred: {(preferred.Count == 0 ? "none" : string.Join(", ", preferred))}; available: {(available == null ? "none" : string.Join(", ", available))})");

            return code;
        }
    }
}
