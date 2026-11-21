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

        private const int GlyphWidth = 6, GlyphHeight = 8;

        private static readonly Lazy<(string Ascii, string Unicode)> FontStores = new(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), $"cuo-gameseam-fonts-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);

            var ascii = new Uo.Pack.AsciiGlyph[224];
            var texels = new ushort[GlyphWidth * GlyphHeight];
            Array.Fill(texels, (ushort)0x7FFF);

            for (int c = 0; c < 224; c++)
            {
                ascii[c] = new Uo.Pack.AsciiGlyph(GlyphWidth, GlyphHeight, texels);
            }

            byte[] asciiFont = Uo.Pack.AsciiFontCodec.Encode(ascii);
            const int asciiFonts = 10;
            var aw = new Uo.Pack.RecordStoreWriter(asciiFonts);

            for (int f = 0; f < asciiFonts; f++)
            {
                aw.Slots[f] = new Uo.Pack.StoreSlot { Record = (uint)aw.Add(asciiFont, (uint)asciiFont.Length, Uo.Pack.StoreConst.CodecRaw, 224, 0) + 1 };
            }

            string asciiPath = Path.Combine(dir, "fonts.bin");
            aw.Write(asciiPath, Uo.Pack.TextConst.MagicFontsAscii, 0);

            var glyphs = new Uo.Pack.UnicodeGlyph[256];
            byte[] bits = new byte[Uo.Pack.UnicodeGlyph.BitBytes(GlyphWidth, GlyphHeight)];
            Array.Fill(bits, (byte)0xFC);

            for (int c = 32; c < 127; c++)
            {
                glyphs[c] = new Uo.Pack.UnicodeGlyph(0, 0, GlyphWidth, GlyphHeight, bits);
            }

            byte[] page = Uo.Pack.UnicodePageCodec.Encode(glyphs);
            var uw = new Uo.Pack.RecordStoreWriter(Uo.Pack.TextConst.UnicodeSlots);

            for (int f = 0; f < Uo.Pack.TextConst.UnicodeFonts; f++)
            {
                uw.Slots[f * Uo.Pack.TextConst.PagesPerFont] =
                    new Uo.Pack.StoreSlot { Record = (uint)uw.Add(page, (uint)page.Length, Uo.Pack.StoreConst.CodecRaw, 0, 0, 1) + 1 };
            }

            string uniPath = Path.Combine(dir, "unifont.bin");
            uw.Write(uniPath, Uo.Pack.TextConst.MagicFontsUnicode, (1u << Uo.Pack.TextConst.UnicodeFonts) - 1); // flags: which fonts exist

            return (asciiPath, uniPath);
        });

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
            // fonts: every printable ASCII glyph 6x8 in each ASCII and unicode font (a tiny store read by the real loader), so
            // text controls measure as they do in the client
            fm.Fonts.LoadStores(FontStores.Value.Ascii, FontStores.Value.Unicode);
            uo.SetForTests(PacketPipe.Version, "7.0.13.0", fm, new GameCursor(new World()));
            uo.SetRendererForTests(new ClassicUO.Renderer.Gumps.Gump(fm.Gumps, null, GumpsLoader.MAX_GUMP_DATA_INDEX_COUNT));
            ClassicUO.Renderer.SolidColorTextureCache.TextureWithoutDeviceForTests ??= static _ => TextureWithoutDevice();
            Game = GameController.CreateForTests(uo);
            Client.UseForTests(Game);

            _profile.SetValue(null, profile ? new Profile() : null);
        }

        /// <summary>A texture object that is never drawn or uploaded (no device): what a control holds when it takes one at construction.</summary>
        public static Microsoft.Xna.Framework.Graphics.Texture2D TextureWithoutDevice()
        {
            var t = (Microsoft.Xna.Framework.Graphics.Texture2D)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(Microsoft.Xna.Framework.Graphics.Texture2D));
            GC.SuppressFinalize(t); // it owns no native texture

            return t;
        }

        /// <summary>Gump <paramref name="graphic"/> as if it were in the atlas, <paramref name="width"/> x <paramref name="height"/>.</summary>
        public void Resident(ushort graphic, int width, int height) =>
            Game.UO.Gumps.SetResidentForTests(graphic, TextureWithoutDevice(), width, height);

        public ItemHold ItemHold => Game.UO.GameCursor.ItemHold;

        public void Dispose()
        {
            // gumps a handler opened (UIManager is process wide)
            ClassicUO.Game.Managers.UIManager.ShowGamePopup(null);
            ClassicUO.Game.Managers.UIManager.Gumps.Clear();
            Client.UseForTests(null);
            _tiles.StaticData = _oldStatics;
            _tiles.LandData = _oldLand;
            _profile.SetValue(null, null);
        }
    }
}
