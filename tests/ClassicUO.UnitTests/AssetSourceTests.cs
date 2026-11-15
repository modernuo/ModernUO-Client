using System;
using System.IO;
using ClassicUO.Configuration;
using ClassicUO.Utility;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests
{
    public class AssetSourceTests
    {
        [Fact]
        public void Version_from_settings_wins_over_the_pack()
        {
            var settings = new Settings { ClientVersion = "7.0.15.1" };
            var manifest = new Manifest { SourceClientVersion = "7.0.103.0" };

            Assert.True(AssetSource.TryResolveVersion(settings, manifest, out var version, out var text));
            Assert.Equal("7.0.15.1", text);
            Assert.True(ClientVersionHelper.IsClientVersionValid("7.0.15.1", out var expected));
            Assert.Equal(expected, version);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not a version")]
        public void Version_falls_back_to_the_pack(string configured)
        {
            var settings = new Settings { ClientVersion = configured };
            var manifest = new Manifest { SourceClientVersion = "7.0.103.0" };

            Assert.True(AssetSource.TryResolveVersion(settings, manifest, out var version, out var text));
            Assert.Equal("7.0.103.0", text);
            Assert.True(ClientVersionHelper.IsClientVersionValid("7.0.103.0", out var expected));
            Assert.Equal(expected, version);
        }

        [Fact]
        public void Settings_version_is_sanitized()
        {
            var settings = new Settings { ClientVersion = "7, 0, 15, 1" };

            Assert.True(AssetSource.TryResolveVersion(settings, new Manifest(), out _, out var text));
            Assert.Equal("7.0.15.1", text);
        }

        [Fact]
        public void No_version_anywhere_fails()
        {
            var settings = new Settings { ClientVersion = "" };

            Assert.False(AssetSource.TryResolveVersion(settings, new Manifest { SourceClientVersion = "" }, out _, out _));
        }

        [Fact]
        public void Missing_manifest_reports_the_pack_path()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cuo-assetsource-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                Assert.False(AssetSource.TryReadManifest(dir, out var manifest, out var error));
                Assert.Null(manifest);
                Assert.Contains(dir, error);

                ManifestFileIo.Write(Path.Combine(dir, "manifest.json"), new Manifest { SourceClientVersion = "7.0.103.0" });

                Assert.True(AssetSource.TryReadManifest(dir, out manifest, out error));
                Assert.Null(error);
                Assert.Equal("7.0.103.0", manifest.SourceClientVersion);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Missing_uo_directory_is_optional()
        {
            var settings = new Settings { UltimaOnlineDirectory = Path.Combine(Path.GetTempPath(), "no-such-uo-" + Guid.NewGuid().ToString("N")) };

            Assert.Null(AssetSource.ConfiguredUODirectory(settings));
            Assert.Equal(CUOEnviroment.ExecutablePath, AssetSource.LooseFileDirectory(settings));

            settings.UltimaOnlineDirectory = "";
            Assert.Null(AssetSource.ConfiguredUODirectory(settings));
        }
    }
}
