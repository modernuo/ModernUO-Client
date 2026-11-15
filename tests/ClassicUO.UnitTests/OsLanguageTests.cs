using System;
using System.IO;
using System.Text.Json;
using ClassicUO.Configuration;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests
{
    public class OsLanguageTests
    {
        private static readonly string[] Stock = { "enu", "fra", "deu", "esp", "jpn", "kor", "cht", "chs" };

        private static PreferredLocale L(string lang, string country = null) => new(lang, country);

        private static string Pick(string profile, string[] available, PreferredLocale[] preferred, Func<PreferredLocale, string> exact = null) =>
            OsLanguage.Choose(profile, available, preferred, exact, out _);

        [Fact]
        public void FirstAvailablePreferredLanguageWins()
        {
            Pick(null, Stock, new[] { L("de"), L("en", "US") }).Should().Be("DEU");
            Pick(null, Stock, new[] { L("nl"), L("en") }).Should().Be("ENU");
        }

        [Fact]
        public void NothingMatchingOrNoListIsEnglish()
        {
            Pick(null, Stock, new[] { L("nl") }).Should().Be("ENU");
            Pick(null, Stock, null).Should().Be("ENU");
            Pick(null, Stock, Array.Empty<PreferredLocale>()).Should().Be("ENU");
            Pick(null, Stock, new[] { L(null, null), L("", "") }).Should().Be("ENU");
        }

        [Fact]
        public void SpanishIsEspNotEsn()
        {
            Pick(null, Stock, new[] { L("es", "ES") }, p => "ESN").Should().Be("ESP");
            Pick(null, Stock, new[] { L("es") }).Should().Be("ESP");
        }

        [Theory]
        [InlineData("zh", "TW", "CHT")]
        [InlineData("zh", "HK", "CHT")]
        [InlineData("zh", "MO", "CHT")]
        [InlineData("zh", "Hant", "CHT")]
        [InlineData("zh_Hant", null, "CHT")]
        [InlineData("zh-Hant-TW", null, "CHT")]
        [InlineData("zh", null, "CHS")]
        [InlineData("zh", "CN", "CHS")]
        [InlineData("zh", "Hans", "CHS")]
        [InlineData("zh_CN", null, "CHS")]
        public void ChineseByScriptOrCountry(string lang, string country, string expected)
        {
            Pick(null, Stock, new[] { L(lang, country) }).Should().Be(expected);
        }

        [Fact]
        public void WindowsExactCodeBreaksATieAndOnlyWhenAvailable()
        {
            Pick(null, Stock, new[] { L("zh", "TW") }, p => "CHS").Should().Be("CHS");
            Pick(null, Stock, new[] { L("zh", "TW") }, p => "XXX").Should().Be("CHT");
            Pick(null, Stock, new[] { L("zh", "TW") }, p => null).Should().Be("CHT");
            Pick(null, Stock, new[] { L("zh", "TW") }, p => throw new InvalidOperationException()).Should().Be("CHT");
            // not consulted when only one language matches
            Pick(null, Stock, new[] { L("de", "DE") }, p => throw new Exception("not needed")).Should().Be("DEU");
        }

        [Fact]
        public void EnglishVariantsPreferEnuWithoutAnExactCode()
        {
            string[] set = { "enu", "eng", "deu" };
            Pick(null, set, new[] { L("en", "GB") }).Should().Be("ENU");
            Pick(null, set, new[] { L("en", "GB") }, p => "ENG").Should().Be("ENG");
            Pick(null, new[] { "eng", "deu" }, new[] { L("en") }).Should().Be("ENG");
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("EN")]
        [InlineData("en_US")]
        public void LanguageTagIsCaseInsensitiveAndSplitOnDashOrUnderscore(string tag)
        {
            Pick(null, Stock, new[] { L(tag) }).Should().Be("ENU");
            Pick(null, new[] { "ENU", "DEU" }, new[] { L("DE-de") }).Should().Be("DEU");
        }

        [Fact]
        public void ServerExtraLanguagesAreMatchedAndMissingOnesSkipped()
        {
            string[] withPtb = { "enu", "deu", "ptb" };
            Pick(null, withPtb, new[] { L("pt", "BR") }).Should().Be("PTB");
            Pick(null, Stock, new[] { L("pt", "BR"), L("de") }).Should().Be("DEU");
            Pick(null, Stock, new[] { L("pt", "BR") }).Should().Be("ENU");
            Pick(null, new[] { "enu", "rus" }, new[] { L("ru", "RU") }).Should().Be("RUS");
        }

        [Fact]
        public void ThreeLetterLanguagesAreSkipped()
        {
            Pick(null, Stock, new[] { L("fil", "PH"), L("ja") }).Should().Be("JPN");
            Pick(null, new[] { "enu", "fil" }, new[] { L("fil") }).Should().Be("ENU");
        }

        [Fact]
        public void ProfileLanguageWinsWhenAvailable()
        {
            Pick("fra", Stock, new[] { L("de") }).Should().Be("FRA");
            Pick(" KOR ", Stock, new[] { L("de") }).Should().Be("KOR");
            string code = OsLanguage.Choose("FRA", Stock, new[] { L("de") }, null, out string why);
            code.Should().Be("FRA");
            why.Should().Contain("profile");
        }

        [Fact]
        public void UnavailableOrPlaceholderProfileLanguageFallsToTheOs()
        {
            Pick("RUS", Stock, new[] { L("de") }).Should().Be("DEU");
            Pick("IVL", Stock, new[] { L("de") }).Should().Be("DEU");
            Pick("ZZZ", Stock, new[] { L("ja") }).Should().Be("JPN");
            Pick("", Stock, new[] { L("ko") }).Should().Be("KOR");
            OsLanguage.Choose("RUS", Stock, new[] { L("nl") }, null, out string why).Should().Be("ENU");
            why.Should().Contain("RUS").And.Contain("not available");
        }

        [Fact]
        public void SetWithoutEnuStillAnswersFromWhatExistsOrEnu()
        {
            string[] noEnu = { "deu", "fra" };
            Pick(null, noEnu, new[] { L("de") }).Should().Be("DEU");
            // nothing matches: ENU, which the loader then rolls back from as it always did
            Pick(null, noEnu, new[] { L("nl") }).Should().Be("ENU");
            Pick(null, Array.Empty<string>(), new[] { L("de") }).Should().Be("ENU");
            Pick(null, null, new[] { L("de") }).Should().Be("ENU");
        }

        [Fact]
        public void AvailableCodesAreCaseInsensitiveAndOutputIsUpperCase()
        {
            Pick(null, new[] { "Enu", "dEu" }, new[] { L("de") }).Should().Be("DEU");
        }

        [Fact]
        public void WhyNamesTheLocale()
        {
            OsLanguage.Choose(null, Stock, new[] { L("nl"), L("de", "AT") }, null, out string why);
            why.Should().Contain("de_AT");
        }
        // Codex audit finding 1: the configured preference is saved unchanged, the effective language never is.
        [Theory]
        [InlineData("RUS", "DEU", "RUS")] // unavailable preference: DEU this run, RUS kept for the next
        [InlineData("", "DEU", "")]       // unset: the OS's choice is not saved
        [InlineData("fra", "FRA", "fra")] // available preference: saved as configured
        public void SaveWritesTheConfiguredLanguageNotTheEffectiveOne(string configured, string effective, string saved)
        {
            string file = Path.Combine(Path.GetTempPath(), "cuo-oslang-" + Guid.NewGuid().ToString("N") + ".json");
            string oldPath = Settings.CustomSettingsFilepath;
            string oldConfigured = Settings.ConfiguredLanguage;

            try
            {
                Settings.CustomSettingsFilepath = file;
                var settings = new Settings { Language = configured };
                Settings.ApplyLanguage(settings, Stock, (profile, available) => OsLanguage.Choose(profile, available, new[] { L("de") }, null, out _));
                settings.Language.Should().Be(effective);

                settings.Save();

                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                doc.RootElement.GetProperty("lang").GetString().Should().Be(saved);
            }
            finally
            {
                Settings.CustomSettingsFilepath = oldPath;
                Settings.ConfiguredLanguage = oldConfigured;
                File.Delete(file);
            }
        }

        // Codex audit finding 2: every language with CJK text uses the Unicode fonts, CHS included.
        [Theory]
        [InlineData("CHT", true)]
        [InlineData("chs", true)]
        [InlineData(" CHS ", true)]
        [InlineData("KOR", true)]
        [InlineData("jpn", true)]
        [InlineData("ENU", false)]
        [InlineData("DEU", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void NeedsUnicodeFontsCoversAllCjkLanguages(string code, bool expected)
        {
            OsLanguage.NeedsUnicodeFonts(code).Should().Be(expected);
        }

        // Codex audit finding 2: what zh_CN selects is one of them.
        [Fact]
        public void SimplifiedChineseFromTheOsUsesTheUnicodeFonts()
        {
            OsLanguage.NeedsUnicodeFonts(Pick(null, Stock, new[] { L("zh", "CN") })).Should().BeTrue();
        }

        // Codex audit finding 4: only three-letter codes are selectable (the language packet carries three characters).
        [Fact]
        public void CodesOtherThanThreeLettersAreNotSelectable()
        {
            Pick(null, new[] { "enu", "ptbr" }, new[] { L("pt", "BR") }).Should().Be("ENU");
            Pick("PTBR", new[] { "enu", "ptbr", "deu" }, new[] { L("de") }).Should().Be("DEU");
            Pick(null, new[] { "enu", "de", "d1u" }, new[] { L("de") }).Should().Be("ENU");
        }
    }
}
