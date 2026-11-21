// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using ClassicUO.Assets;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    /// <summary>
    /// shared_cliloc: cliloc tables read in place from memory shared between instances (<see cref="SharedTableCache"/>).
    /// Read once at startup, before the first cliloc load; in developer builds CUO_SHARED_CLILOC=1|0 overrides the setting
    /// and CUO_SHARED_CLILOC_CHECK=1 compares every lookup with the heap path's.
    /// </summary>
    internal static class SharedClilocSetup
    {
        /// <summary>Tests: the switch (null: the setting and the override).</summary>
        internal static bool? OnOverride;

        /// <summary>The cache the client holds for its lifetime (mappings are never released while tables may be read).</summary>
        internal static SharedTableCache Cache { get; private set; }

        public static bool On
        {
            get
            {
                if (OnOverride.HasValue)
                {
                    return OnOverride.Value;
                }
#if CUO_DEVTOOLS
                switch (Environment.GetEnvironmentVariable("CUO_SHARED_CLILOC"))
                {
                    case "1": return true;
                    case "0": return false;
                }
#endif
                return Configuration.Settings.GlobalSettings?.SharedCliloc ?? false;
            }
        }

        public static void Enable(UOFileManager files)
        {
            if (!On || files.Pack == null)
            {
                Log.Trace("[shared-cliloc] shared_cliloc=off");

                return;
            }

            try
            {
                string key = SharedTableCache.PackKeyOf(Path.Combine(files.Pack.Root, "manifest.json"));
                var cache = new SharedTableCache(SharedTableCache.DefaultRoot(), key);

                // a folder that can't be used still maps raw records straight from the pack
                cache.Open();
                Cache = cache;
                files.Clilocs.Shared = cache;
#if CUO_DEVTOOLS
                files.Clilocs.CheckShared = Environment.GetEnvironmentVariable("CUO_SHARED_CLILOC_CHECK") == "1";
#endif
                Log.Trace($"[shared-cliloc] shared_cliloc=on: cache '{cache.Folder}' ({(cache.IsOpen ? "in use" : "unusable")}), " +
                          $"stale folders deleted {cache.StaleFoldersDeleted}, in use elsewhere {cache.StaleFoldersInUse}");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.Warn($"[shared-cliloc] shared_cliloc=on but the cache can't be set up ({e.Message}); cliloc stays on the heap");
            }
        }

        public static void LogLoaded(UOFileManager files)
        {
            var c = Cache;

            if (c == null)
            {
                return;
            }

            var cl = files.Clilocs;
            Log.Trace($"[shared-cliloc] '{cl.Language}': lang mapped={cl.LanguageTable?.IsMapped} enu mapped={cl.EnglishTable?.IsMapped} " +
                      $"overrides mapped={cl.PackOverridesTable?.IsMapped}; built {c.Built} reused {c.Reused} rebuilt {c.Rebuilt} " +
                      $"direct {c.DirectMaps} fallbacks {c.Fallbacks}");
        }
    }
}
