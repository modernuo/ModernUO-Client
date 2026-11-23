using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;
using ClassicUO.Configuration;
using Xunit;

namespace ClassicUO.UnitTests.Configuration
{
    public class GraphicsTiersTests
    {
        private static readonly QualityTier[] Tiers = { QualityTier.Low, QualityTier.Medium, QualityTier.High };

        [Fact]
        public void EveryRowIsAnExistingBoolSetting()
        {
            var keys = new Dictionary<string, JsonPropertyInfo>();

            foreach (JsonPropertyInfo p in SettingsJsonContext.RealDefault.Settings.Properties)
            {
                keys[p.Name] = p;
            }

            var seen = new HashSet<string>();

            foreach (GraphicsTiers.Row row in GraphicsTiers.Rows)
            {
                Assert.True(seen.Add(row.Key), $"duplicate row {row.Key}");
                Assert.True(keys.TryGetValue(row.Key, out JsonPropertyInfo prop), $"{row.Key} is not a settings.json key");
                Assert.Equal(typeof(bool), prop.PropertyType);

                // the row's accessors read and write that very property
                var settings = new Settings();
                row.Set(settings, true);
                Assert.True((bool)prop.Get(settings), row.Key);
                row.Set(settings, false);
                Assert.False((bool)prop.Get(settings), row.Key);
            }
        }

        [Fact]
        public void PresetsAreMonotonic()
        {
            foreach (GraphicsTiers.Row row in GraphicsTiers.Rows)
            {
                switch (row.Kind)
                {
                    case GraphicsTiers.RowKind.Feature:
                        // on at a tier means on at every higher tier: Low's features are a subset of Medium's, Medium's of High's
                        Assert.True(!row.ValueAt(QualityTier.Low) || row.ValueAt(QualityTier.Medium), row.Key);
                        Assert.True(!row.ValueAt(QualityTier.Medium) || row.ValueAt(QualityTier.High), row.Key);
                        break;
                    case GraphicsTiers.RowKind.Reducer:
                        Assert.True(row.ValueAt(QualityTier.Low), row.Key);
                        Assert.False(row.ValueAt(QualityTier.Medium), row.Key);
                        Assert.False(row.ValueAt(QualityTier.High), row.Key);
                        break;
                    case GraphicsTiers.RowKind.Always:
                        foreach (QualityTier t in Tiers)
                        {
                            Assert.True(row.ValueAt(t), row.Key);
                        }

                        break;
                }
            }
        }

        [Fact]
        public void LedgerRowsAreInTheTable()
        {
            var table = new Dictionary<string, GraphicsTiers.Row>();

            foreach (GraphicsTiers.Row row in GraphicsTiers.Rows)
            {
                table[row.Key] = row;
            }

            Assert.Equal(QualityTier.High, table["view_margin"].From);
            Assert.Equal(QualityTier.High, table["light_occlusion"].From);
            Assert.Equal(QualityTier.High, table["light_batch"].From);
            Assert.Equal(GraphicsTiers.RowKind.Reducer, table["light_range"].Kind);
            Assert.False(table.ContainsKey("light_alpha_fade"));
        }

        [Fact]
        public void PresetsDiffer()
        {
            foreach (QualityTier a in Tiers)
            {
                foreach (QualityTier b in Tiers)
                {
                    if (a == b)
                    {
                        continue;
                    }

                    var settings = new Settings();
                    GraphicsTiers.Apply(settings, a);
                    Assert.False(GraphicsTiers.Matches(settings, b), $"{a} and {b} write the same rows");
                }
            }
        }

        [Fact]
        public void ApplyThenDetectRoundTrips()
        {
            foreach (QualityTier tier in Tiers)
            {
                var settings = new Settings();
                GraphicsTiers.Apply(settings, tier);
                Assert.Equal(tier, GraphicsTiers.Detect(settings));
            }
        }

        [Fact]
        public void AnyChangedRowIsCustom()
        {
            foreach (QualityTier tier in Tiers)
            {
                foreach (GraphicsTiers.Row row in GraphicsTiers.Rows)
                {
                    var settings = new Settings();
                    GraphicsTiers.Apply(settings, tier);
                    row.Set(settings, !row.Get(settings));

                    QualityTier? detected = GraphicsTiers.Detect(settings);

                    // flipping one row may land exactly on a neighbouring preset (Low to Medium differs in light_range only)
                    Assert.True(detected != tier, $"{tier} with {row.Key} flipped still reads {tier}");

                    if (detected is { } other)
                    {
                        Assert.True(GraphicsTiers.Matches(settings, other));
                    }
                }
            }
        }

        [Fact]
        public void ANewInstallIsMedium()
        {
            var settings = new Settings();
            Assert.Equal(QualityTier.Medium, GraphicsTiers.Detect(settings));

            foreach (GraphicsTiers.Row row in GraphicsTiers.Rows)
            {
                Assert.Equal(row.ValueAt(QualityTier.Medium), row.Get(settings));
            }
        }

        private static Settings Parse(string json) =>
            System.Text.Json.JsonSerializer.Deserialize(json, SettingsJsonContext.RealDefault.Settings);

        [Fact]
        public void ASavedFileWithExplicitValuesLoadsUnchanged()
        {
            Settings high = Parse("{\"chunk_lights\":true,\"light_range\":false,\"view_margin\":true,\"light_occlusion\":true,\"light_batch\":true}");
            Assert.Equal(QualityTier.High, GraphicsTiers.Detect(high));

            Settings custom = Parse("{\"chunk_lights\":false,\"light_range\":true,\"view_margin\":true}");
            Assert.True(custom.ViewMargin);
            Assert.True(custom.LightRange);
            Assert.False(custom.ChunkLights);
            Assert.False(custom.LightOcclusion);
            Assert.Null(GraphicsTiers.Detect(custom));
        }

        [Fact]
        public void ASavedFileWithoutTierKeysKeepsThemOff()
        {
            Settings old = Parse("{\"language\":\"\"}");
            foreach (GraphicsTiers.Row row in GraphicsTiers.Rows)
            {
                Assert.False(row.Get(old), row.Key);
            }
        }

        [Fact]
        public void PickingAPresetOverwritesASavedFile()
        {
            Settings custom = Parse("{\"light_range\":true,\"view_margin\":true}");
            GraphicsTiers.Apply(custom, QualityTier.Medium);
            Assert.Equal(QualityTier.Medium, GraphicsTiers.Detect(custom));
            GraphicsTiers.Apply(custom, QualityTier.High);
            Assert.Equal(QualityTier.High, GraphicsTiers.Detect(custom));
        }

        [Fact]
        public void UntieredSettingsAreLeftAlone()
        {
            var settings = new Settings { LightAlphaFade = true, LightBudget = 77, LightRangeTiles = 5 };
            GraphicsTiers.Apply(settings, QualityTier.Low);
            Assert.True(settings.LightAlphaFade);
            Assert.Equal(77, settings.LightBudget);
            Assert.Equal(5, settings.LightRangeTiles);
        }

        [Theory]
        [InlineData("low", true, 0)]
        [InlineData("Medium", true, 1)]
        [InlineData("HIGH", true, 2)]
        [InlineData("custom", false, 0)]
        [InlineData("7", false, 0)]
        [InlineData("", false, 0)]
        public void ParsesPresetNames(string text, bool ok, int expected)
        {
            Assert.Equal(ok, GraphicsTiers.TryParse(text, out QualityTier tier));

            if (ok)
            {
                Assert.Equal((QualityTier)expected, tier);
            }
        }
    }
}
