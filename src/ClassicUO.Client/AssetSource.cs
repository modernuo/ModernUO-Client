// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System.IO;
using Uo.Pack;

namespace ClassicUO
{
    /// <summary>
    /// Where the client's assets come from. The pack is required; the UO directory is optional and only feeds the
    /// remaining loose-file reads (files_override, the CUO_PACK_GUMPS=0 / CUO_PACK_TEXT=0 fallbacks).
    /// </summary>
    internal static class AssetSource
    {
        public static string PackPath(Settings settings) =>
            PackAssets.Resolve(settings.PackDirectory, CUOEnviroment.ExecutablePath, ConfiguredUODirectory(settings));

        /// <summary>The configured UO directory if it exists, else null.</summary>
        public static string ConfiguredUODirectory(Settings settings)
        {
            string dir = settings.UltimaOnlineDirectory;

            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;
        }

        /// <summary>
        /// The base folder for loose-file reads: the configured UO directory, else the client's folder (the old default,
        /// so files placed next to cuo still load).
        /// </summary>
        public static string LooseFileDirectory(Settings settings) => ConfiguredUODirectory(settings) ?? CUOEnviroment.ExecutablePath;

        public static bool TryReadManifest(string packPath, out Manifest manifest, out string error)
        {
            manifest = null;
            string manifestPath = Path.Combine(packPath, "manifest.json");

            if (!File.Exists(manifestPath))
            {
                error = $"No asset pack at '{packPath}' (manifest.json missing). Convert your UO client with Uo.Converter and set \"pack\" in settings.json or pass -pack <dir>.";

                return false;
            }

            try
            {
                manifest = ManifestFileIo.Read(manifestPath);
                error = null;

                return true;
            }
            catch (System.Exception ex)
            {
                error = $"The asset pack manifest '{manifestPath}' cannot be read: {ex.Message}";

                return false;
            }
        }

        /// <summary>
        /// The client version: clientversion in settings if valid (a shard may need a specific protocol version), else
        /// the version the pack was converted from. client.exe is no longer read.
        /// </summary>
        public static bool TryResolveVersion(Settings settings, Manifest manifest, out ClientVersion version, out string versionText)
        {
            versionText = settings.ClientVersion;

            if (!string.IsNullOrWhiteSpace(versionText))
            {
                versionText = versionText.Replace(",", ".").Replace(" ", "").ToLower();

                if (ClientVersionHelper.IsClientVersionValid(versionText, out version))
                {
                    return true;
                }

                Log.Warn($"Client version [{versionText}] in settings is invalid, using the pack's version");
            }

            versionText = manifest?.SourceClientVersion;

            return ClientVersionHelper.IsClientVersionValid(versionText, out version);
        }
    }
}
