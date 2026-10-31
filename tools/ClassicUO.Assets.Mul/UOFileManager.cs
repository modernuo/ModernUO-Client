// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using ClassicUO.Utility.Platforms;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassicUO.Assets
{
    public sealed class UOFileManager : IDisposable
    {
        private readonly UOFilesOverrideMap _overrideMap;

        public UOFileManager(ClientVersion clientVersion, string uoPath) : this(clientVersion, uoPath, new UOFilesOverrideMap())
        {
        }
        
        public UOFileManager(ClientVersion clientVersion, string uoPath, UOFilesOverrideMap overrideMap)
        {
            Version = clientVersion;
            BasePath = uoPath;

            _overrideMap = overrideMap;
            IsUOPInstallation = Version >= ClientVersion.CV_7000 && File.Exists(GetUOFilePath("MainMisc.uop"));

            Animations = new AnimationsLoader(this);
            AnimData = new AnimDataLoader(this);
            Arts = new ArtLoader(this);
            Maps = new MapLoader(this);
            Hues = new HuesLoader(this);
            TileData = new TileDataLoader(this);
            Multis = new MultiLoader(this);
            Texmaps = new TexmapsLoader(this);
            Lights = new LightsLoader(this);
            Verdata = new VerdataLoader(this);
            TileArt = new TileArtLoader(this);
            Gumps = new GumpsLoader(this);
            Fonts = new FontsLoader(this);
            Clilocs = new ClilocLoader(this);
            Speeches = new SpeechesLoader(this);
            Sounds = new SoundsLoader(this);
        }

        public GumpsLoader Gumps { get; }
        public FontsLoader Fonts { get; }
        public ClilocLoader Clilocs { get; }
        public SpeechesLoader Speeches { get; }
        public SoundsLoader Sounds { get; }

        public ClientVersion Version { get; }
        public string BasePath { get; }
        public bool IsUOPInstallation { get; private set; }

        public AnimationsLoader Animations { get; }
        public AnimDataLoader AnimData { get; }
        public ArtLoader Arts { get; }
        public MapLoader Maps { get; set; }
        public HuesLoader Hues { get; }
        public TileDataLoader TileData { get; }
        public MultiLoader Multis { get; }
        public TexmapsLoader Texmaps { get; }
        public LightsLoader Lights { get; }
        public VerdataLoader Verdata { get; }
        public TileArtLoader TileArt { get; }



        public void Dispose()
        {
            Animations.Dispose();
            AnimData.Dispose();
            Arts.Dispose();
            Maps.Dispose();
            Hues.Dispose();
            TileData.Dispose();
            Multis.Dispose();
            Texmaps.Dispose();
            Lights.Dispose();
            Verdata.Dispose();
            TileArt.Dispose();
            Gumps.Dispose();
            Fonts.Dispose();
            Clilocs.Dispose();
            Speeches.Dispose();
            Sounds.Dispose();
        }

        public string GetUOFilePath(string file)
        {
            if (!_overrideMap.TryGetValue(file.ToLowerInvariant(), out string uoFilePath))
            {
                uoFilePath = Path.Combine(BasePath, file);
            }

            //If the file with the given name doesn't exist, check for it with alternative casing if not on windows
            if (!PlatformHelper.IsWindows && !File.Exists(uoFilePath))
            {
                FileInfo finfo = new FileInfo(uoFilePath);
                var dir = Path.GetFullPath(finfo.DirectoryName ?? BasePath);

                if (Directory.Exists(dir))
                {
                    var files = Directory.GetFiles(dir);
                    var matches = 0;

                    foreach (var f in files)
                    {
                        if (string.Equals(f, uoFilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            matches++;
                            uoFilePath = f;
                        }
                    }

                    if (matches > 1)
                    {
                        Log.Warn($"Multiple files with ambiguous case found for {file}, using {Path.GetFileName(uoFilePath)}. Check your data directory for duplicate files.");
                    }
                }
            }

            return uoFilePath;
        }

        public void Load(string mapsLayouts = "")
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            
            Maps.MapsLayouts = mapsLayouts;

            Animations.Load();
            AnimData.Load();
            Arts.Load();
            Maps.Load();
            Hues.Load();
            TileData.Load();
            Multis.Load();
            Texmaps.Load();
            Lights.Load();
            TileArt.Load();
            Verdata.Load();

            ReadArtDefFile();

            Log.Trace($"Files loaded in: {stopwatch.ElapsedMilliseconds} ms!");
            stopwatch.Stop();
        }

        private void ReadArtDefFile()
        {
            string pathdef = GetUOFilePath("art.def");

            if (!File.Exists(pathdef))
            {
                return;
            }

            using (var reader = new DefReader(pathdef, 1))
            {
                while (reader.Next())
                {
                    int index = reader.ReadInt();

                    if (index < 0 || index >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT + TileData.StaticData.Length)
                    {
                        continue;
                    }

                    int[] group = reader.ReadGroup();

                    if (group == null)
                    {
                        continue;
                    }

                    for (int i = 0; i < group.Length; i++)
                    {
                        int checkIndex = group[i];

                        if (checkIndex < 0 || checkIndex >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT + TileData.StaticData.Length)
                        {
                            continue;
                        }

                        if (index < Arts.File.Entries.Length && checkIndex < Arts.File.Entries.Length)
                        {
                            ref UOFileIndex currentEntry = ref Arts.File.GetValidRefEntry(index);
                            ref UOFileIndex checkEntry = ref Arts.File.GetValidRefEntry(checkIndex);

                            if (currentEntry.Equals(UOFileIndex.Invalid) && !checkEntry.Equals(UOFileIndex.Invalid))
                            {
                                Arts.File.Entries[index] = Arts.File.Entries[checkIndex];
                            }
                        }

                        if (index < ArtLoader.MAX_LAND_DATA_INDEX_COUNT &&
                            checkIndex < ArtLoader.MAX_LAND_DATA_INDEX_COUNT &&
                            checkIndex < TileData.LandData.Length &&
                            index < TileData.LandData.Length &&
                            !TileData.LandData[checkIndex].Equals(default) &&
                            TileData.LandData[index].Equals(default))
                        {
                            TileData.LandData[index] = TileData.LandData[checkIndex];

                            break;
                        }

                        if (index >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT && checkIndex >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT &&
                            index < TileData.StaticData.Length && checkIndex < TileData.StaticData.Length &&
                            TileData.StaticData[index].Equals(default) && !TileData.StaticData[checkIndex].Equals(default))
                        {
                            TileData.StaticData[index] = TileData.StaticData[checkIndex];

                            break;
                        }
                    }
                }
            }
        }
    }
}
