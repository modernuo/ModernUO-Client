#region license

// Copyright (c) 2024, andreakarasho
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 1. Redistributions of source code must retain the above copyright
//    notice, this list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright
//    notice, this list of conditions and the following disclaimer in the
//    documentation and/or other materials provided with the distribution.
// 3. All advertising materials mentioning features or use of this software
//    must display the following acknowledgement:
//    This product includes software developed by andreakarasho - https://github.com/andreakarasho
// 4. Neither the name of the copyright holder nor the
//    names of its contributors may be used to endorse or promote products
//    derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS ''AS IS'' AND ANY
// EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#endregion

using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using ClassicUO.Utility.Platforms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ClassicUO.Assets
{
    public static class UOFileManager
    {
        public static string GetUOFilePath(string file)
        {
            if (!UOFilesOverrideMap.Instance.TryGetValue(file.ToLowerInvariant(), out string uoFilePath))
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

        public static ClientVersion Version;
        public static string BasePath = "";
#if ENABLE_UOP
        public static bool IsUOPInstallation;
#endif

        public static void Load(ClientVersion version, string basePath, string lang)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            Version = version;
            BasePath = basePath;

            UOFilesOverrideMap.Instance.Load(); // need to load this first so that it manages can perform the file overrides if needed

#if ENABLE_UOP
            IsUOPInstallation = Version >= ClientVersion.CV_7000 && File.Exists(GetUOFilePath("MainMisc.uop"));
#endif

            List<Task> tasks = new List<Task>
            {
                AnimationsLoader.Instance.Load(),
                AnimDataLoader.Instance.Load(),
                ArtLoader.Instance.Load(),
                MapLoader.Instance.Load(),
                ClilocLoader.Instance.Load(lang),
                GumpsLoader.Instance.Load(),
                FontsLoader.Instance.Load(),
                HuesLoader.Instance.Load(),
                TileDataLoader.Instance.Load(),
                MultiLoader.Instance.Load(),
                SkillsLoader.Instance.Load().ContinueWith(t => ProfessionLoader.Instance.Load()),
                TexmapsLoader.Instance.Load(),
                SpeechesLoader.Instance.Load(),
                LightsLoader.Instance.Load(),
            };

            if (!Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(10)))
            {
                Log.Panic("Loading files timeout.");
            }

            Read_Art_def();

            Log.Trace($"Files loaded in: {stopwatch.ElapsedMilliseconds} ms!");
            stopwatch.Stop();
        }

        public static void MapLoaderReLoad(MapLoader newloader)
        {
            MapLoader.Instance?.Dispose();
            MapLoader.Instance = newloader;
        }

        private static void Read_Art_def()
        {
            string pathdef = GetUOFilePath("art.def");

            if (File.Exists(pathdef))
            {
                TileDataLoader tiledataLoader =  TileDataLoader.Instance;
                ArtLoader artLoader = ArtLoader.Instance;

                using DefReader reader = new DefReader(pathdef, 1);
                while (reader.Next())
                {
                    int index = reader.ReadInt();

                    if (index < 0 || index >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT + tiledataLoader.StaticData.Length)
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

                        if (checkIndex < 0 || checkIndex >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT + tiledataLoader.StaticData.Length)
                        {
                            continue;
                        }

                        if (artLoader.IsValidIndex(index) && artLoader.IsValidIndex(checkIndex))
                        {
                            ref UOFileIndex currentEntry = ref artLoader.GetValidRefEntry(index);
                            ref UOFileIndex checkEntry = ref artLoader.GetValidRefEntry(checkIndex);

                            if (currentEntry.IsInvalid() && !checkEntry.IsInvalid())
                            {
                                artLoader.PatchEntry((uint)index, checkEntry);
                            }
                        }

                        if (index < ArtLoader.MAX_LAND_DATA_INDEX_COUNT &&
                            checkIndex < ArtLoader.MAX_LAND_DATA_INDEX_COUNT &&
                            checkIndex < tiledataLoader.LandData.Length &&
                            index < tiledataLoader.LandData.Length &&
                            !tiledataLoader.LandData[checkIndex].Equals(default) &&
                            tiledataLoader.LandData[index].Equals(default))
                        {
                            tiledataLoader.LandData[index] = tiledataLoader.LandData[checkIndex];

                            break;
                        }

                        if (index >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT && checkIndex >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT &&
                            index < tiledataLoader.StaticData.Length && checkIndex < tiledataLoader.StaticData.Length &&
                            tiledataLoader.StaticData[index].Equals(default) && !tiledataLoader.StaticData[checkIndex].Equals(default))
                        {
                            tiledataLoader.StaticData[index] = tiledataLoader.StaticData[checkIndex];

                            break;
                        }
                    }
                }
            }
        }
    }
}
