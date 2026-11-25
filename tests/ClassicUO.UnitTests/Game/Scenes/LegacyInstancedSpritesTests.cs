using System;
using ClassicUO.Configuration;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using FluentAssertions;
using Microsoft.Xna.Framework;
using Xunit;

namespace ClassicUO.UnitTests.Game.Scenes
{
    /// <summary>legacy_instanced_sprites: the switch, the world scope and the checker's comparison.</summary>
    public sealed class LegacyInstancedSpritesTests
    {
        private static Settings Parse(string json) =>
            System.Text.Json.JsonSerializer.Deserialize(json, SettingsJsonContext.RealDefault.Settings);

        [Fact]
        public void SwitchDefaultsOff()
        {
            new Settings().LegacyInstancedSprites.Should().BeFalse();
            LegacyInstancedSprites.Enabled.Should().BeFalse();
        }

        [Fact]
        public void OldSettingsFileWithoutKeyLoads()
        {
            Parse("{}").LegacyInstancedSprites.Should().BeFalse();
            Parse("{\"legacy_instanced_sprites\":true}").LegacyInstancedSprites.Should().BeTrue();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OverrideWins(bool on)
        {
            LegacyInstancedSprites.OverrideForTests = on;

            try
            {
                LegacyInstancedSprites.Enabled.Should().Be(on);
                LegacyInstancedSprites.RequestFor(true).Should().Be(on);
            }
            finally
            {
                LegacyInstancedSprites.OverrideForTests = null;
            }
        }

        [Fact]
        public void OutsideTheWorldListsTheRequestIsOff()
        {
            LegacyInstancedSprites.OverrideForTests = true;

            try
            {
                LegacyInstancedSprites.RequestFor(false).Should().BeFalse();
            }
            finally
            {
                LegacyInstancedSprites.OverrideForTests = null;
            }
        }

        [Fact]
        public void CheckerCountsMatchesAndMismatches()
        {
            var lines = new System.Collections.Generic.List<string>();
            UltimaBatcher2D.ResetCheck();
            UltimaBatcher2D.InstanceMismatchLog = lines.Add;

            try
            {
                var color = new Vector3(5f, 1f, 0.5f);
                var tex = new Point(64, 64);

                LegacySpriteInstance.TryFromSprite(0f, 0f, 16f, 16f, 10f, 20f, 16f, 16f, color, 0f, 0f, 0f, 1f, 0.25f, 0, out LegacySpriteInstance inst)
                    .Should().BeTrue();

                UltimaBatcher2D.CheckSprite(in inst, 0f, 0f, 16f, 16f, 10f, 20f, 16f, 16f, color, 0f, 0f, 0f, 1f, 0.25f, 0, tex.X, tex.Y);
                UltimaBatcher2D.InstanceChecked.Should().Be(1);
                UltimaBatcher2D.InstanceMismatches.Should().Be(0);

                // same instance, other hue in the quad: a mismatch is counted and logged
                UltimaBatcher2D.CheckSprite(in inst, 0f, 0f, 16f, 16f, 10f, 20f, 16f, 16f, new Vector3(6f, 1f, 0.5f), 0f, 0f, 0f, 1f, 0.25f, 0, tex.X, tex.Y);
                UltimaBatcher2D.InstanceChecked.Should().Be(2);
                UltimaBatcher2D.InstanceMismatches.Should().Be(1);
                lines.Should().HaveCount(1);

                // the log is capped
                for (int i = 0; i < 40; i++)
                {
                    UltimaBatcher2D.CheckSprite(in inst, 0f, 0f, 16f, 16f, 10f, 20f, 16f, 16f, new Vector3(6f, 1f, 0.5f), 0f, 0f, 0f, 1f, 0.25f, 0, tex.X, tex.Y);
                }

                UltimaBatcher2D.InstanceMismatches.Should().Be(41);
                lines.Count.Should().Be(20);
            }
            finally
            {
                UltimaBatcher2D.InstanceMismatchLog = null;
                UltimaBatcher2D.ResetCheck();
            }
        }
    }
}
