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
/// exactly as ModernUO serves them, see <see cref="ApplyLegacyMapRules"/>.
/// </summary>
public static class ClientLoad
{
    /// <summary>--no-map-diffs: read the base map files only (the README rule "no patch formats"); Trammel of a legacy install is then Felucca.</summary>
    public static bool MapDiffs = true;

    public static UOFileManager Open(ClientVersion version, string uoPath)
    {
        var fm = new UOFileManager(version, uoPath);
        fm.Arts.Load();
        fm.Maps.Load();
        ApplyLegacyMapRules(fm);
        fm.Hues.Load();
        fm.TileData.Load();
        fm.Texmaps.Load();   // applies TexTerr.def
        ApplyArtDef(fm);
        return fm;
    }

    /// <summary>ModernUO's map rules for pre-UOP installs, so the pack holds the world the server walks on:
    /// <list type="bullet">
    /// <item>Felucca/Trammel width from the map0 file size (7168 when it holds 7168x4096, 6144 when 6144x4096).
    /// ClassicUO decides by version (below CV_4011D: 6144), and its parsed "4.0.11c"/"4.0.11d" (4.0.11.2/.3) are
    /// always below that enum (whose low byte is the letter's ASCII code), so it would drop the Mondain's Legacy
    /// columns 6144..7167 that the UOML map0.mul holds and that ModernUO's 7168-wide Felucca serves.</item>
    /// <item>Map diffs: ModernUO applies mapdif/mapdifl below 6.0.0.0 (<c>maps.enableMapDiffPatches</c>) and
    /// stadif/stadifl/stadifi below 7.0.9.0 (<c>maps.enableStaticsDiffPatches</c>), every listed block, and sends the
    /// counts in 0xBF/0x18 (TileMatrixPatch.cs, OutgoingMapPackets.SendMapPatches). Applying the same counts here
    /// through ClassicUO's own ApplyPatches bakes the patched blocks into the pack; Trammel without its mapdif1 would be
    /// Felucca.</item>
    /// <item>Trammel (facet 1) without a map1 file is map0 + the facet-1 diffs (ModernUO <c>maps.enablePre6000Trammel</c>
    /// below 6.0.0.0; ClassicUO's loader already aliases the map1 readers to map0); MapConvert.AvailableFacets.</item>
    /// </list></summary>
    public static void ApplyLegacyMapRules(UOFileManager fm)
    {
        var maps = fm.Maps;
        var map0 = fm.GetUOFilePath("map0.mul");
        if (!fm.IsUOPInstallation && File.Exists(map0))
        {
            long blocks = new FileInfo(map0).Length / MapBlockBytes;
            int width = blocks switch { 7168 / 8 * (4096 / 8) => 7168, 6144 / 8 * (4096 / 8) => 6144, _ => 0 };
            if (width != 0) maps.MapsDefaultSize[0, 0] = maps.MapsDefaultSize[1, 0] = width;
        }

        bool land = MapDiffs && fm.Version < ClientVersion.CV_6000, statics = MapDiffs && fm.Version < ClientVersion.CV_7090;
        if (!land && !statics) return;
        var packet = new byte[4 + 8 * MapLoader.MAPS_COUNT];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(packet, MapLoader.MAPS_COUNT);
        bool any = false;
        for (int i = 0; i < MapLoader.MAPS_COUNT; i++)
        {
            int st = statics ? DiffCount(fm, $"stadifl{i}.mul") : 0, ld = land ? DiffCount(fm, $"mapdifl{i}.mul") : 0;
            if (st == 0 && ld == 0) continue;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4 + 8 * i), st); // statics first, as ModernUO sends
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8 + 8 * i), ld);
            maps.LoadMap(i);
            any = true;
        }

        if (!any) return;
        // Facets with no diffs get counts of 0, and ApplyPatches touches only the block tables of facets with counts.
        var reader = new StackDataReader(packet);
        maps.ApplyPatches(ref reader);
        reader.Release();
        for (int i = 0; i < MapLoader.MAPS_COUNT; i++)
            if (maps.MapPatchCount[i] != 0 || maps.StaticPatchCount[i] != 0)
                Console.WriteLine($"  facet {i}: map diffs applied as ModernUO serves them: {maps.MapPatchCount[i]} land blocks, {maps.StaticPatchCount[i]} static blocks");
    }

    private const int MapBlockBytes = 4 + 64 * 3;

    private static int DiffCount(UOFileManager fm, string file)
    {
        var path = fm.GetUOFilePath(file);
        return File.Exists(path) ? (int)(new FileInfo(path).Length / 4) : 0;
    }

    /// <summary>
    /// TileDataLoader picks the record layout from the version alone (below 7.0.9.0: 32-bit flags, 26-byte land and
    /// 37-byte static records; from 7.0.9.0: 64-bit flags, 30 and 41 bytes). A wrong version would misparse every record
    /// without an error, so the file size must fit the chosen layout: 512 land groups, then whole static groups of 32
    /// (ModernUO decides by file size the same way). Returns an error message, or null.
    /// </summary>
    public static string? CheckTileDataLayout(string tiledataPath, ClientVersion version)
    {
        if (!File.Exists(tiledataPath)) return $"{tiledataPath} not found";
        long len = new FileInfo(tiledataPath).Length;
        bool old = version < ClientVersion.CV_7090;
        static (long Land, long Group) Layout(bool old) => old ? (512L * (4 + 32 * 26), 4 + 32 * 37) : (512L * (4 + 32 * 30), 4 + 32 * 41);
        static bool Fits(long len, bool old) => len > Layout(old).Land && (len - Layout(old).Land) % Layout(old).Group == 0;
        if (Fits(len, old)) return null;
        string other = Fits(len, !old) ? $"; it fits the {(old ? "7.0.9+" : "pre-7.0.9")} layout" : "";
        return $"{tiledataPath} ({len} B) does not fit the {(old ? "pre-7.0.9 (32-bit flags)" : "7.0.9+ (64-bit flags)")} tiledata layout of client version {version}{other}: {LegacyClientVersion.FlagHelp}";
    }

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
