// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using ClassicUO.Utility.Platforms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ClassicUO.Assets
{
    public sealed class UOFileManager : IDisposable
    {
        private readonly UOFilesOverrideMap _overrideMap;

        public UOFileManager(ClientVersion clientVersion, string uoPath, PackAssets pack) : this(clientVersion, uoPath, new UOFilesOverrideMap(), pack)
        {
        }

        public UOFileManager(ClientVersion clientVersion, string uoPath, UOFilesOverrideMap overrideMap, PackAssets pack)
        {
            Version = clientVersion;
            BasePath = uoPath;
            Pack = pack;

            _overrideMap = overrideMap;
            IsUOPInstallation = Version >= ClientVersion.CV_7000 && File.Exists(GetUOFilePath("MainMisc.uop"));

            Animations = new AnimationsLoader(this);
            AnimData = new AnimDataLoader(this);
            Arts = new ArtLoader(this);
            Maps = new MapLoader(this);
            Clilocs = new ClilocLoader(this);
            Gumps = new GumpsLoader(this);
            Fonts = new FontsLoader(this);
            Hues = new HuesLoader(this);
            TileData = new TileDataLoader(this);
            Multis = new MultiLoader(this);
            Skills = new SkillsLoader(this);
            Texmaps = new TexmapsLoader(this);
            Speeches = new SpeechesLoader(this);
            Lights = new LightsLoader(this);
            Sounds = new SoundsLoader(this);
            MultiMaps = new MultiMapLoader(this);
            Professions = new ProfessionLoader(this);
            TileArt = new TileArtLoader(this);
            StringDictionary = new StringDictionaryLoader(this);
        }

        public ClientVersion Version { get; }
        public string BasePath { get; }
        public bool IsUOPInstallation { get; private set; }
        public PackAssets Pack { get; }

        public AnimationsLoader Animations { get; }
        public AnimDataLoader AnimData { get; }
        public ArtLoader Arts { get; }
        public MapLoader Maps { get; }
        public ClilocLoader Clilocs { get; }
        public GumpsLoader Gumps { get; }
        public FontsLoader Fonts { get; }
        public HuesLoader Hues { get; }
        public TileDataLoader TileData { get; }
        public MultiLoader Multis { get; }
        public SkillsLoader Skills { get; }
        public TexmapsLoader Texmaps { get; }
        public SpeechesLoader Speeches { get; }
        public LightsLoader Lights { get; }
        public SoundsLoader Sounds { get; }
        public MultiMapLoader MultiMaps { get; }
        public ProfessionLoader Professions { get; }
        public TileArtLoader TileArt { get; }
        public StringDictionaryLoader StringDictionary { get; }



        public void Dispose()
        {
            Animations.Dispose();
            AnimData.Dispose();
            Arts.Dispose();
            Maps.Dispose();
            Clilocs.Dispose();
            Gumps.Dispose();
            Fonts.Dispose();
            Hues.Dispose();
            TileData.Dispose();
            Multis.Dispose();
            Skills.Dispose();
            Texmaps.Dispose();
            Speeches.Dispose();
            Lights.Dispose();
            Sounds.Dispose();
            MultiMaps.Dispose();
            Pack.Dispose();
            Professions.Dispose();
            TileArt.Dispose();
            StringDictionary.Dispose();
        }

        /// <summary>The file names files_override maps (lower-case), each resolved by <see cref="GetUOFilePath"/>.</summary>
        public IEnumerable<string> OverriddenFiles => _overrideMap.Keys;

        /// <summary>Tests: run the alternative-casing lookup on any OS (default: off on Windows).</summary>
        internal bool CaseInsensitiveFallback = !PlatformHelper.IsWindows;

        public string GetUOFilePath(string file)
        {
            if (!_overrideMap.TryGetValue(file.ToLowerInvariant(), out string uoFilePath))
            {
                uoFilePath = Path.Combine(BasePath, file);
            }

            //If the file with the given name doesn't exist, check for it with alternative casing if not on windows
            if (CaseInsensitiveFallback && !File.Exists(uoFilePath))
            {
                try
                {
                    // absolute, so the comparison below sees the same form as the directory entries (a relative base such as ./UO never matched)
                    string full = Path.GetFullPath(uoFilePath);
                    var dir = Path.GetDirectoryName(full) ?? Path.GetFullPath(BasePath);

                    if (Directory.Exists(dir))
                    {
                        var matches = 0;
                        string found = null;

                        foreach (var f in Directory.GetFiles(dir))
                        {
                            if (string.Equals(f, full, StringComparison.OrdinalIgnoreCase))
                            {
                                matches++;
                                found = f;
                            }
                        }

                        if (found != null)
                        {
                            uoFilePath = found;
                        }

                        if (matches > 1)
                        {
                            Log.Warn($"Multiple files with ambiguous case found for {file}, using {Path.GetFileName(uoFilePath)}. Check your data directory for duplicate files.");
                        }
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException || e is UnauthorizedAccessException)
                {
                    // an empty or invalid path (a bad files_override target): no alternative casing to find; the caller sees a file that does not exist
                    Log.Trace($"No case-insensitive lookup for '{file}' ({e.Message})");
                }
            }

            return uoFilePath;
        }

        public void Load(string lang)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            long alloc0 = GC.GetTotalAllocatedBytes(true);
            var timings = new System.Text.StringBuilder();

            void Timed(string name, Action load)
            {
                long t = Stopwatch.GetTimestamp();
                long a = GC.GetAllocatedBytesForCurrentThread();
                load();
                timings.Append($" {name} {Stopwatch.GetElapsedTime(t).TotalMilliseconds:0.0}ms/{(GC.GetAllocatedBytesForCurrentThread() - a) / 1024}KB");
            }

            Timed("anims", Animations.Load);
            Timed("animdata", AnimData.Load);
            Timed("art", Arts.Load);
            Timed("maps", Maps.Load);
            Timed("cliloc", () => Clilocs.Load(lang));
            Timed("gumps", Gumps.Load);
            Timed("fonts", Fonts.Load);
            Timed("hues", Hues.Load);
            Timed("tiledata", TileData.Load);
            Timed("multis", Multis.Load);
            Timed("skills", Skills.Load);
            Timed("professions", Professions.Load);
            Timed("texmaps", Texmaps.Load);
            Timed("speech", Speeches.Load);
            Timed("lights", Lights.Load);
            Timed("sounds", Sounds.Load);
            Timed("multimaps", MultiMaps.Load);
            Timed("tileart", TileArt.Load);
            Timed("stringdict", StringDictionary.Load);

            Log.Trace($"Files loaded in: {stopwatch.ElapsedMilliseconds} ms!");
            double ms = stopwatch.Elapsed.TotalMilliseconds;
            long allocated = GC.GetTotalAllocatedBytes(true) - alloc0;
            // CUO_LOAD_MEM=1: a full collection first, so the heap size is what the loaders keep (measurements only)
            long heap = GC.GetTotalMemory(Environment.GetEnvironmentVariable("CUO_LOAD_MEM") == "1");
            Log.Trace($"[load] {ms:0.0} ms, {allocated / 1024} KB allocated, GC heap {heap / 1024} KB after:{timings}");
            stopwatch.Stop();
        }
    }
}
