using System.Text;
using System.Text.Json;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>A light classes JSON value the converter cannot use. The message says which entry and why.</summary>
public sealed class LightClassJsonException(string message) : Exception(message);

/// <summary>
/// light_alpha_fade: the light shape classes as text (docs/dev/pack-format.md, "lightclass.bin"):
/// <c>{ "lightClasses": 1, "surface": [26, 27, ...], "radial": [1, 2, ...] }</c>. Ids are light ids 0..255 (the tiledata light
/// id); an id in neither list is unlisted (the client falls back to the emitter's Wall/Window flags). Unknown keys, ids out of
/// range and an id in both lists are refused. <c>--export-light-classes</c> writes the standard shapes' seed.
/// </summary>
public static class LightClassJson
{
    public const int FormatVersion = 1;

    public static byte[] Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new LightClassJsonException($"{path}: {e.Message}"); }
        return Parse(text);
    }

    public static byte[] Parse(string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { throw new LightClassJsonException($"not JSON: {e.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new LightClassJsonException("the top level must be an object");
            List<int>? surface = null, radial = null;
            bool version = false;
            foreach (var p in root.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "lightClasses":
                        if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out int v) || v != FormatVersion)
                            throw new LightClassJsonException($"\"lightClasses\" must be {FormatVersion}");
                        version = true;
                        break;
                    case "surface": surface = Ids(p.Value, "surface"); break;
                    case "radial": radial = Ids(p.Value, "radial"); break;
                    default: throw new LightClassJsonException($"unknown key \"{p.Name}\"");
                }
            }
            if (!version) throw new LightClassJsonException("missing \"lightClasses\": 1");
            try { return LightClassFile.FromLists(surface ?? [], radial ?? []); }
            catch (ArgumentException e) { throw new LightClassJsonException(e.Message); }
        }
    }

    private static List<int> Ids(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Array) throw new LightClassJsonException($"\"{key}\" must be an array of light ids");
        var ids = new List<int>();
        foreach (var x in e.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out int id)) throw new LightClassJsonException($"\"{key}\": {x} is not a light id");
            ids.Add(id);
        }
        return ids;
    }

    public static string Export(ReadOnlySpan<byte> classes)
    {
        var (s, r) = LightClassFile.ToLists(classes);
        var sb = new StringBuilder();
        sb.Append("{\n  \"lightClasses\": ").Append(FormatVersion).Append(",\n");
        sb.Append("  \"surface\": [").Append(string.Join(", ", s)).Append("],\n");
        sb.Append("  \"radial\": [").Append(string.Join(", ", r)).Append("]\n}\n");
        return sb.ToString();
    }

    public static void Save(ReadOnlySpan<byte> classes, string path) => ReplacingFile.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(Export(classes)));
}
