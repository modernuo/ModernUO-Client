using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Utility;

namespace Uo.Converter;

/// <summary>
/// The subset of UOFileManager.Load the converter needs. The full Load also initialises animations, which needs the
/// native zlib that ClassicUO ships with its client (not available to a plain console app), and loaders we don't use.
/// Order matches UOFileManager.Load; ReadArtDefFile is private there so it is ported verbatim below.
/// Verdata.mul is unsupported by design (shard owners merge it into the base files with their own tools; ClassicUO
/// itself only reads it below 5.0.0a) and is never loaded. Map diffs (mapdif*/stadif*) are applied for legacy clients
/// exactly as ModernUO serves them, see <see cref="FacetTable"/>.
/// </summary>
public static class ClientLoad
{
    /// <summary>--no-map-diffs: read the base map files only (the README rule "no patch formats"); Trammel of a legacy install is then Felucca.</summary>
    public static bool MapDiffs = true;

    public static UOFileManager Open(ClientVersion version, string uoPath)
    {
        var fm = new UOFileManager(version, uoPath);
        fm.Arts.Load();
        // fm.Maps.Load() is not called: the converter reads maps itself (FacetSources/FacetTable) and needs only the facet
        // sizes. The frozen loader's Load opens all five diff files whenever mapdifl exists, so a land-only diff set
        // would throw there (Codex finding 2), and its version rule cuts Felucca to 6144 for every parsed 4.0.11x.
        ApplyLegacyMapRules(fm);
        fm.Hues.Load();
        fm.TileData.Load();
        fm.Texmaps.Load();   // applies TexTerr.def
        ApplyArtDef(fm);
        return fm;
    }

    /// <summary>Felucca/Trammel width from the map0 file size (7168 when it holds 7168x4096, 6144 when 6144x4096), for
    /// MUL maps. ClassicUO decides by version (below CV_4011D: 6144) and would drop the Mondain's Legacy columns
    /// 6144..7167 that the UOML map0.mul holds and that ModernUO's 7168-wide Felucca serves (owner, 2026-10-31: the
    /// width comes from the file size).
    /// Map diffs and Trammel's source are handled by <see cref="FacetTable"/>, not here.</summary>
    public static void ApplyLegacyMapRules(UOFileManager fm)
    {
        var maps = fm.Maps;
        var src = FacetSources.Resolve(fm, 0);
        if (src == null || src.MapIsUop) return;
        long blocks = new FileInfo(src.Map).Length / MapBlockBytes;
        int width = blocks switch { 7168 / 8 * (4096 / 8) => 7168, 6144 / 8 * (4096 / 8) => 6144, _ => 0 };
        if (width != 0) maps.MapsDefaultSize[0, 0] = maps.MapsDefaultSize[1, 0] = width;
    }

    private const int MapBlockBytes = 4 + 64 * 3;

    /// <summary>
    /// TileDataLoader picks the record layout from the version alone (below 7.0.9.0: 32-bit flags, 26-byte land and
    /// 37-byte static records; from 7.0.9.0: 64-bit flags, 30 and 41 bytes). A wrong version would misparse every record
    /// without an error, so the file size must fit the chosen layout: 512 land groups, then whole static groups of 32
    /// (ModernUO decides by file size the same way). Returns an error message, or null.
    /// <para>The size check is evidence, not proof. Some sizes fit both layouts (e.g. 1,383,184 B: 804 old or 676 new
    /// static groups), and there the size says nothing about the layout. Such a file is refused unless the version was
    /// given explicitly with <c>--client-version</c> (<paramref name="versionExplicit"/>), in which case the caller's
    /// version decides and <see cref="TileDataSizeIsAmbiguous"/> lets the caller warn. The stock sizes (1,036,288 and
    /// 1,644,544 B old, 3,188,736 B new) fit exactly one layout.</para>
    /// </summary>
    public static string? CheckTileDataLayout(string tiledataPath, ClientVersion version, bool versionExplicit = false)
    {
        if (!File.Exists(tiledataPath)) return $"{tiledataPath} not found";
        long len = new FileInfo(tiledataPath).Length;
        bool old = version < ClientVersion.CV_7090;
        if (Fits(len, old) && Fits(len, !old))
            return versionExplicit ? null
                : $"{tiledataPath} ({len} B) fits both the pre-7.0.9 (32-bit flags) and the 7.0.9+ (64-bit flags) tiledata layouts, so its size cannot confirm the layout that client version {version} selects ({(old ? "pre-7.0.9" : "7.0.9+")}); " +
                  "give the version explicitly with --client-version to accept that layout";
        if (Fits(len, old)) return null;
        string other = Fits(len, !old) ? $"; it fits the {(old ? "7.0.9+" : "pre-7.0.9")} layout" : "";
        return $"{tiledataPath} ({len} B) does not fit the {(old ? "pre-7.0.9 (32-bit flags)" : "7.0.9+ (64-bit flags)")} tiledata layout of client version {version}{other}: {LegacyClientVersion.FlagHelp}";
    }

    /// <summary>True when a tiledata.mul of this size fits both record layouts (see <see cref="CheckTileDataLayout"/>).</summary>
    public static bool TileDataSizeIsAmbiguous(long length) => Fits(length, true) && Fits(length, false);

    private static (long Land, long Group) Layout(bool old) => old ? (512L * (4 + 32 * 26), 4 + 32 * 37) : (512L * (4 + 32 * 30), 4 + 32 * 41);
    private static bool Fits(long len, bool old) => len > Layout(old).Land && (len - Layout(old).Land) % Layout(old).Group == 0;

    // Port of UOFileManager.ReadArtDefFile.
    private static void ApplyArtDef(UOFileManager fm)
    {
        string pathdef = fm.GetUOFilePath("art.def");
        if (!File.Exists(pathdef)) return;
        var arts = fm.Arts;
        var td = fm.TileData;
        using var reader = new DefReader(pathdef, 1);
        while (reader.Next())
        {
            int index = reader.ReadInt();
            if (index < 0 || index >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT + td.StaticData.Length) continue;
            int[] group = reader.ReadGroup();
            if (group == null) continue;
            for (int i = 0; i < group.Length; i++)
            {
                int checkIndex = group[i];
                if (checkIndex < 0 || checkIndex >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT + td.StaticData.Length) continue;
                if (index < arts.File.Entries.Length && checkIndex < arts.File.Entries.Length)
                {
                    ref UOFileIndex currentEntry = ref arts.File.GetValidRefEntry(index);
                    ref UOFileIndex checkEntry = ref arts.File.GetValidRefEntry(checkIndex);
                    if (currentEntry.Equals(UOFileIndex.Invalid) && !checkEntry.Equals(UOFileIndex.Invalid))
                        arts.File.Entries[index] = arts.File.Entries[checkIndex];
                }
                if (index < ArtLoader.MAX_LAND_DATA_INDEX_COUNT && checkIndex < ArtLoader.MAX_LAND_DATA_INDEX_COUNT &&
                    checkIndex < td.LandData.Length && index < td.LandData.Length &&
                    !td.LandData[checkIndex].Equals(default) && td.LandData[index].Equals(default))
                {
                    td.LandData[index] = td.LandData[checkIndex];
                    break;
                }
                if (index >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT && checkIndex >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT &&
                    index < td.StaticData.Length && checkIndex < td.StaticData.Length &&
                    td.StaticData[index].Equals(default) && !td.StaticData[checkIndex].Equals(default))
                {
                    td.StaticData[index] = td.StaticData[checkIndex];
                    break;
                }
            }
        }
    }
}
