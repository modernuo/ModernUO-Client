using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Utility;

namespace Uo.Converter;

/// <summary>
/// The subset of UOFileManager.Load the converter needs. The full Load also initialises animations, which needs the
/// native zlib that ClassicUO ships with its client (not available to a plain console app), and loaders we don't use.
/// Order matches UOFileManager.Load; ReadArtDefFile is private there so it is ported verbatim below.
/// Verdata: only forced for clients &lt; 5.0.0a or when verdata.mul has patches; modern clients ship none.
/// </summary>
public static class ClientLoad
{
    public static UOFileManager Open(ClientVersion version, string uoPath)
    {
        var fm = new UOFileManager(version, uoPath);
        fm.Arts.Load();
        fm.Maps.Load();
        fm.Hues.Load();
        fm.TileData.Load();
        fm.Texmaps.Load();   // applies TexTerr.def
        fm.Verdata.Load();
        ApplyArtDef(fm);
        return fm;
    }

    public static bool HasVerdataPatches(UOFileManager fm) => fm.Verdata.File != null && fm.Verdata.File.Length != 0 && fm.Verdata.Patches.Length != 0;

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
