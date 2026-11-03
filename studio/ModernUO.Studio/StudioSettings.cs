using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModernUO.Studio
{
    /// <summary>studio.json next to the executable: recent packs and window size. Never holds shard or account data.</summary>
    internal sealed class StudioSettings
    {
        public List<string> RecentPacks { get; set; } = new();
        public string ExportDirectory { get; set; } = "";
        public int WindowWidth { get; set; } = 1600;
        public int WindowHeight { get; set; } = 900;
        public float UiScale { get; set; } = 1f;

        private static string FilePath => Path.Combine(AppContext.BaseDirectory, "studio.json");

        public static StudioSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    return JsonSerializer.Deserialize(File.ReadAllText(FilePath), StudioSettingsJson.Default.StudioSettings) ?? new StudioSettings();
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                StudioLog.Warn($"studio.json could not be read, using defaults: {ex.Message}");
            }

            return new StudioSettings();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, StudioSettingsJson.Default.StudioSettings));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StudioLog.Warn($"studio.json could not be saved: {ex.Message}");
            }
        }

        public void AddRecent(string pack)
        {
            pack = Path.GetFullPath(pack);
            RecentPacks.RemoveAll(p => string.Equals(p, pack, StringComparison.OrdinalIgnoreCase));
            RecentPacks.Insert(0, pack);

            if (RecentPacks.Count > 10)
            {
                RecentPacks.RemoveRange(10, RecentPacks.Count - 10);
            }
        }

        public string ResolveExportDirectory()
        {
            string dir = string.IsNullOrWhiteSpace(ExportDirectory) ? Path.Combine(AppContext.BaseDirectory, "export") : ExportDirectory;
            Directory.CreateDirectory(dir);

            return dir;
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(StudioSettings))]
    internal partial class StudioSettingsJson : JsonSerializerContext
    {
    }
}
