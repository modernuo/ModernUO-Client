// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.IO;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework.Graphics;
using SDL3;
using System;
using System.Diagnostics;
using System.IO;

namespace ClassicUO
{
    sealed class UltimaOnline
    {
        public Renderer.Animations.Animations Animations { get; private set; }
        public Renderer.Arts.Art Arts { get; private set; }
        public Renderer.Gumps.Gump Gumps { get; private set; }
        public Renderer.Texmaps.Texmap Texmaps { get; private set; }
        public Renderer.Lights.Light Lights { get; private set; }
        public Renderer.MultiMaps.MultiMap MultiMaps { get; private set; }
        public Renderer.Sounds.Sound Sounds { get; private set; }
        public Renderer.FontGlyphAtlas FontGlyphAtlas { get; private set; }
        public World World { get; private set; }
        public GameCursor GameCursor { get; private set; }

        public ClientVersion Version { get; private set; }

        /// <summary>
        /// The version text the client runs as and sends to the server: settings clientversion (sanitized) when valid,
        /// else the pack manifest's. Not written back to settings.json, so a different pack changes it.
        /// </summary>
        public string VersionText { get; private set; }
        public ClientFlags Protocol { get; set; }
        public string ClientPath { get; private set; }
        public UOFileManager FileManager { get; private set; }
#if CUO_DEVTOOLS
        public FontLayoutCheck FontLayoutCheck { get; } = new FontLayoutCheck();
        public Game.UI.Gumps.GumpResendCheck GumpResendCheck { get; } = new Game.UI.Gumps.GumpResendCheck();
#endif


        public UltimaOnline()
        {

        }

        /// <summary>Test seam (see Client.UseForTests): the fields Load sets that packet handlers read, without loading anything.</summary>
        internal void SetForTests(ClientVersion version, string versionText, UOFileManager fileManager, GameCursor cursor)
        {
            Version = version;
            VersionText = versionText;
            FileManager = fileManager;
            GameCursor = cursor;
        }

        /// <summary>
        /// Test seam: the renderer's asset front ends on the real loaders of <see cref="FileManager"/>, without a graphics device.
        /// An asset the loaders have no pixels for is missing (no texture), as in a pack without it; one with pixels would need a device,
        /// so the test passes the gump and art front ends it built and makes the sprites it needs resident (SetResidentForTests on each).
        /// </summary>
        internal void SetRendererForTests(Renderer.Gumps.Gump gumps, Renderer.Arts.Art arts = null)
        {
            Arts = arts ?? new Renderer.Arts.Art(FileManager.Arts, FileManager.Hues, null);
            Gumps = gumps;
            MultiMaps = new Renderer.MultiMaps.MultiMap(FileManager.MultiMaps, null);
            Animations = new Renderer.Animations.Animations(FileManager.Animations, null);
            FontGlyphAtlas = new Renderer.FontGlyphAtlas(FileManager.Fonts, null);
        }

        public unsafe void Load(GameController game)
        {
            LoadUOFiles();

            const int TEXTURE_WIDTH = 512;
            const int TEXTURE_HEIGHT = 1024;
            const int LIGHTS_TEXTURE_WIDTH = 32;
            const int LIGHTS_TEXTURE_HEIGHT = 63;

            var hueSamplers = new Texture2D[2];
            hueSamplers[0] = new Texture2D(game.GraphicsDevice, TEXTURE_WIDTH, TEXTURE_HEIGHT);
            hueSamplers[1] = new Texture2D(game.GraphicsDevice, LIGHTS_TEXTURE_WIDTH, LIGHTS_TEXTURE_HEIGHT);

            var buffer = new uint[Math.Max(
                LIGHTS_TEXTURE_WIDTH * LIGHTS_TEXTURE_HEIGHT,
                TEXTURE_WIDTH * TEXTURE_HEIGHT
            )];

            fixed (uint* ptr = buffer)
            {
                FileManager.Hues.CreateShaderColors(buffer);

                hueSamplers[0].SetDataPointerEXT(
                    0,
                    null,
                    (IntPtr)ptr,
                    TEXTURE_WIDTH * TEXTURE_HEIGHT * sizeof(uint)
                );
                ClassicUO.Renderer.TextureUploads.Changed();

                LightColors.CreateLightTextures(buffer, LIGHTS_TEXTURE_HEIGHT);
                hueSamplers[1].SetDataPointerEXT(
                    0,
                    null,
                    (IntPtr)ptr,
                    LIGHTS_TEXTURE_WIDTH * LIGHTS_TEXTURE_HEIGHT * sizeof(uint)
                );
                ClassicUO.Renderer.TextureUploads.Changed();
            }

            game.GraphicsDevice.Textures[1] = hueSamplers[0];
            game.GraphicsDevice.Textures[2] = hueSamplers[1];

            Animations = new Renderer.Animations.Animations(FileManager.Animations, game.GraphicsDevice);
            ClassicUO.Game.AnimAtlasLruSetup.Enable(Animations);
            Arts = new Renderer.Arts.Art(FileManager.Arts, FileManager.Hues, game.GraphicsDevice);
#if CUO_DEVTOOLS
            Uo.Buffers.PoolAudit.Log = static s => Log.Warn(s);
#endif
            Gumps = new Renderer.Gumps.Gump(FileManager.Gumps, game.GraphicsDevice, GumpDecodeBudgetEnabled());
            var poolCaps = ClassicUO.Game.LargePoolCaps.Apply(GumpDecodeBudgetEnabled());
            Log.Trace($"[bufpools] caps: general {poolCaps.GeneralBytes >> 20} MiB ({poolCaps.ScratchThreads} scratch threads), region {poolCaps.RegionCount} buffers / {poolCaps.RegionBytes >> 20} MiB, page {poolCaps.PageCount} chunks / {poolCaps.PageBytes >> 20} MiB");
            Texmaps = new Renderer.Texmaps.Texmap(FileManager.Texmaps, game.GraphicsDevice);
            Lights = new Renderer.Lights.Light(FileManager.Lights, game.GraphicsDevice);
            MultiMaps = new Renderer.MultiMaps.MultiMap(FileManager.MultiMaps, game.GraphicsDevice);
            Sounds = new Renderer.Sounds.Sound(FileManager.Sounds);
            FontGlyphAtlas = new Renderer.FontGlyphAtlas(FileManager.Fonts, game.GraphicsDevice);
            ClassicUO.Game.AtlasLruSetup.Enable(this);
            ClassicUO.Game.TextStringCache.Enable();

            LightColors.LoadLights();

            World = new World();
            GameCursor = new GameCursor(World, game.DpiScale);
        }

        // switch gump_decode_budget, read once at startup (the gump decoder is built with or without the budget)
        internal static bool GumpDecodeBudgetEnabled()
        {
#if CUO_DEVTOOLS
            switch (Environment.GetEnvironmentVariable("CUO_GUMP_DECODE_BUDGET"))
            {
                case "1": return true;
                case "0": return false;
            }
#endif
            return Settings.GlobalSettings.GumpDecodeBudget;
        }

        public void Unload()
        {
            FileManager.Dispose();
            World?.Map?.Destroy();
            World?.ReleasePools();
        }


        private void LoadUOFiles()
        {
            // The pack is required; the UO directory is optional and only serves loose-file reads (AssetSource).
            string clientPath = AssetSource.LooseFileDirectory(Settings.GlobalSettings);
            Log.Trace($"Loose-file folder: {clientPath}");

            Log.Trace("Loading files...");

            string packPath = AssetSource.PackPath(Settings.GlobalSettings);

            if (!AssetSource.TryReadManifest(packPath, out var packManifest, out string packError))
            {
                Log.Error(packError);
                Client.ShowErrorMessage(packError);

                throw new FileNotFoundException(packError);
            }

            if (!AssetSource.TryResolveVersion(Settings.GlobalSettings, packManifest, out ClientVersion clientVersion, out string clientVersionText))
            {
                Log.Error("Invalid client version: " + clientVersionText);
                Client.ShowErrorMessage(string.Format(ResGumps.ImpossibleToDefineTheClientVersion0, clientVersionText));

                throw new InvalidClientVersion($"Invalid client version: '{clientVersionText}'");
            }

            Version = clientVersion;
            VersionText = clientVersionText;
            ClientPath = clientPath;

            Protocol = ClientFlags.CF_T2A;

            if (Version >= ClientVersion.CV_200)
            {
                Protocol |= ClientFlags.CF_RE;
            }

            if (Version >= ClientVersion.CV_300)
            {
                Protocol |= ClientFlags.CF_TD;
            }

            if (Version >= ClientVersion.CV_308)
            {
                Protocol |= ClientFlags.CF_LBR;
            }

            if (Version >= ClientVersion.CV_308Z)
            {
                Protocol |= ClientFlags.CF_AOS;
            }

            if (Version >= ClientVersion.CV_405A)
            {
                Protocol |= ClientFlags.CF_SE;
            }

            if (Version >= ClientVersion.CV_60144)
            {
                Protocol |= ClientFlags.CF_SA;
            }

            Log.Trace($"Client path: '{clientPath}'");
            Log.Trace($"Client version: {clientVersion}");
            Log.Trace($"Protocol: {Protocol}");

            var filesOverride = new UOFilesOverrideMap(Settings.GlobalSettings.OverrideFile);
            filesOverride.Load();
            PackAssets pack;

            try
            {
                pack = PackAssets.Open(packPath);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Log.Error(ex.Message);
                Client.ShowErrorMessage(ex.Message);

                throw;
            }

            FileManager = new UOFileManager(clientVersion, clientPath, filesOverride, pack);

            // The language, from the profile if it is available, else the OS's: the pack (or folder) is open, no text is
            // loaded yet, and SDL is up (SDL_GetPreferredLocales also works before init).
            // The configured preference is kept apart for saving (Settings.ConfiguredLanguage).
            Settings.ApplyLanguage(Settings.GlobalSettings, FileManager.Clilocs.AvailableLanguages(), OsLanguage.Select);
            ClassicUO.Game.SharedClilocSetup.Enable(FileManager);
            FileManager.Load(Settings.GlobalSettings.Language);
            ClassicUO.Game.Scenes.LightEmitters.Reset();
            ClassicUO.Game.SharedClilocSetup.LogLoaded(FileManager);
            ClassicUO.Game.Map.Chunk.StaticTileData = FileManager.TileData.StaticData;

            StaticFilters.Load(FileManager.TileData);
            BuffTable.Load();
            ChairTable.Load();
        }
    }


    internal static class Client
    {
        public static GameController Game { get; private set; }

        /// <summary>
        /// Test seam: installs a GameController made by <see cref="GameController.CreateForTests"/> (no window, device or audio)
        /// so packet handlers that reach Client.Game run in unit tests. Run never calls it.
        /// </summary>
        internal static void UseForTests(GameController game) => Game = game;


        public static void Run()
        {
            Debug.Assert(Game == null);

            Log.Trace("Running game...");

            Microsoft.Xna.Framework.FNALoggerEXT.LogError = GpuDeviceLost.OnFnaError;
            Microsoft.Xna.Framework.FNALoggerEXT.LogInfo = FnaInfoLog.Info;

            using (Game = new GameController())
            {
                // https://github.com/FNA-XNA/FNA/wiki/7:-FNA-Environment-Variables#fna_graphics_enable_highdpi
                CUOEnviroment.IsHighDPI = Environment.GetEnvironmentVariable("FNA_GRAPHICS_ENABLE_HIGHDPI") == "1";

                if (CUOEnviroment.IsHighDPI)
                {
                    Log.Trace("HIGH DPI - ENABLED");
                }

                Game.Run();
            }

            Log.Trace("Exiting game...");
        }

        public static void ShowErrorMessage(string msg)
        {
            SDL.SDL_ShowSimpleMessageBox(SDL.SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR, "ERROR", msg, IntPtr.Zero);
        }
    }
}
