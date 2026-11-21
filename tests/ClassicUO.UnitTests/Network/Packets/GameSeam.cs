using System;
using System.IO;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game;

namespace ClassicUO.UnitTests.Network.Packets
{
    /// <summary>
    /// The test seam for handlers that reach the client's globals: installs Client.Game (Client.UseForTests) as a
    /// GameController without a window, device or audio, whose UO carries the pipe's client version, a UOFileManager on an empty
    /// folder (no pack: cliloc lookups return their fallback text; tile data all zeros; two speech keywords) and a GameCursor without SDL cursors, plus a default
    /// Profile. Both (and any gump a handler added to UIManager) are removed again on Dispose, so only the tests that install it see them. Use it only from tests in
    /// SpanBuffersCollection (not run in parallel with other collections): the globals are process wide.
    /// </summary>
    internal sealed class GameSeam : IDisposable
    {
        private static readonly System.Reflection.PropertyInfo _profile =
            typeof(ProfileManager).GetProperty(nameof(ProfileManager.CurrentProfile))!;

        private readonly TileDataLoader _tiles;
        private readonly StaticTiles[] _oldStatics;
        private readonly LandTiles[] _oldLand;

        public GameController Game { get; }

        public GameSeam(bool profile = true)
        {
            string empty = Path.Combine(Path.GetTempPath(), "cuo-gameseam-empty");
            Directory.CreateDirectory(empty);

            var uo = new UltimaOnline();
            var fm = new UOFileManager(PacketPipe.Version, empty, null);
            // tile data of all zeros (no flags, layer 0, weight 0) for every graphic, as Item.ItemData reads it; the loader's
            // arrays are static, so the previous ones come back on Dispose
            _tiles = fm.TileData;
            _oldStatics = _tiles.StaticData;
            _oldLand = _tiles.LandData;
            _tiles.StaticData = new StaticTiles[0x10000];
            _tiles.LandData = new LandTiles[0x4000];
            // two speech keywords, so the speech requests take their keyword path
            typeof(SpeechesLoader).GetField("_speech", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(fm.Speeches, new[] { new SpeechEntry(0x0002, "*bank*"), new SpeechEntry(0x0036, "*guards*") });
            uo.SetForTests(PacketPipe.Version, "7.0.13.0", fm, new GameCursor(new World()));
            Game = GameController.CreateForTests(uo);
            Client.UseForTests(Game);

            _profile.SetValue(null, profile ? new Profile() : null);
        }

        public ItemHold ItemHold => Game.UO.GameCursor.ItemHold;

        public void Dispose()
        {
            // gumps a handler opened (UIManager is process wide)
            ClassicUO.Game.Managers.UIManager.Gumps.Clear();
            Client.UseForTests(null);
            _tiles.StaticData = _oldStatics;
            _tiles.LandData = _oldLand;
            _profile.SetValue(null, null);
        }
    }
}
