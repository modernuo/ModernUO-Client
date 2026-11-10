// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game
{
    /// <summary>
    /// legacy-sprites-fix-2, <c>legacy_item_draw_cache</c> (default off): the Legacy sink's last draw of one art item (not a
    /// corpse, not the placement preview): the one or two batcher calls its select made, relative to the item's screen
    /// position, and the side outputs (the art pick record, the light). Replayed while the key is the same: the item's
    /// <see cref="ClassicUO.Ecs.ItemDrawRow.Version"/> (WorldMirror.UpsertItem bumps it when the row changes), the render
    /// list's alpha, the render offset, the multi graphic, the per-frame scene bits (<see cref="ItemSpriteInput.SceneBits"/>)
    /// and, for animated art, the art's frame offset. Recorded only when every call had its atlas texture (the atlas only
    /// gains textures, so a recorded texture and rect stay valid). CUO_ITEM_DRAW_CACHE_CHECK=1 compares every replay with a
    /// fresh select ([item-draw-cache-check], a summary every 5 s).
    /// </summary>
    internal sealed class ItemDrawCache
    {
        public const byte SELECTED = 1, HIGHLIGHT = 2, OUT_OF_RANGE = 4, DEAD_GRAY = 8, SELECTED_CONTAINER = 16, REPLACE_FIELDS = 32;

        // the key
        public uint Version;
        public Vector3 Offset;
        public ushort MultiGraphic;
        public byte Alpha, Scene;
        public bool TileContainer, TileAnimated;

        // animated art: the art graphic before its frame offset, and the offset the draw used
        public bool AnimCheck;
        public ushort ArtGraphic;
        public short AnimOffset;

        // the draw: one call at (X, Y), after a second copy at (X - 5, Y - 5) when Two; depth = the frame's depth + 0.5
        public bool Valid, Two;
        public Texture2D Texture;
        public Rectangle Source;
        public Vector3 Hue;
        public int X, Y;

        // the side outputs
        public bool Light, HasArtPick;
        public ushort PickGraphic;
        public int PickW, PickH;

        /// <summary>The key part a hit compares after the version and the alpha.</summary>
        public bool Matches(in Vector3 offset, ushort multiGraphic, byte scene) =>
            Offset == offset && MultiGraphic == multiGraphic && Scene == scene;

        /// <summary>The recorded calls at screen position (<paramref name="posX"/>, <paramref name="posY"/>) and the frame's
        /// <paramref name="depth"/>: the batcher calls of SelectDraw (LegacySpriteResolve.DrawArt).</summary>
        public void Replay(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            float d = depth + 0.5f;

            if (Two)
            {
                batcher.Draw(Texture, new Vector2(posX + X - 5, posY + Y - 5), Source, Hue, 0f, Vector2.Zero, 1f, SpriteEffects.None, d);
            }

            batcher.Draw(Texture, new Vector2(posX + X, posY + Y), Source, Hue, 0f, Vector2.Zero, 1f, SpriteEffects.None, d);
        }

        /// <summary>The <paramref name="k"/>th recorded call at the given origin, as a <see cref="LegacyDraw"/> (the checker).</summary>
        public LegacyDraw Call(int k, int posX, int posY, float depth)
        {
            int back = Two && k == 0 ? 5 : 0;

            return new LegacyDraw
            {
                Texture = Texture,
                Position = new Vector2(posX + X - back, posY + Y - back),
                Source = Source,
                Hue = Hue,
                Depth = depth + 0.5f,
                Mode = SpriteCaptureMode.Normal
            };
        }

        public int Calls => Two ? 2 : 1;

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override and the checker
        private static readonly bool? _env = ClassicUO.Game.Scenes.LegacySpriteSink.ParseSwitch(Environment.GetEnvironmentVariable("CUO_LEGACY_ITEM_DRAW_CACHE"));
        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_ITEM_DRAW_CACHE_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: wins over the env override and the setting.</summary>
        internal static bool? Override;

        /// <summary>The switch: <see cref="Override"/>, else CUO_LEGACY_ITEM_DRAW_CACHE=1|0 (dev builds), else the setting.</summary>
        public static bool On => Override ?? _env ?? Configuration.Settings.GlobalSettings?.LegacyItemDrawCache ?? false;

        public static long Hits, Misses, Checks, Diffs;
        private static int _logged;
        private static uint _logAt;

        internal static void Report(Item item, string problem)
        {
            Diffs++;

            if (_logged++ < 20)
            {
                Utility.Logging.Log.Warn($"[item-draw-cache-check] replay differs 0x{item.Serial:X8}:{problem}");
            }
        }

        internal static void LogSummary()
        {
            if (!Check || Time.Ticks < _logAt)
            {
                return;
            }

            _logAt = Time.Ticks + 5000;
            Utility.Logging.Log.Info($"[item-draw-cache-check] legacy_item_draw_cache {(On ? "on" : "off")}: hits {Hits}, misses {Misses}, checked {Checks}, different {Diffs}");
        }
    }
}
