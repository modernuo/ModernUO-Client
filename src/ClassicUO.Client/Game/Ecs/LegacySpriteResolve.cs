// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game
{
    /// <summary>One Legacy batcher call: Draw (Normal), DrawShadow (Shadow) or DrawCharacterSitted (Sitted).</summary>
    internal struct LegacyDraw
    {
        public Texture2D Texture;
        public Vector2 Position;
        public Rectangle Source;
        public Vector3 Hue;
        public float Depth;
        public bool Flip;
        public SpriteCaptureMode Mode;

        /// <summary>Sitted: DrawCharacterSitted's section fractions.</summary>
        public Vector3 SitMod;

        /// <summary>T7b part 3: the rectangle draw with a rotation (View.DrawStaticRotated; the batcher's DrawRotated capture).</summary>
        public bool Rotated;

        public float Rotation;

        public readonly bool Same(in LegacyDraw o) =>
            ReferenceEquals(Texture, o.Texture) && Position == o.Position && Source == o.Source && Hue == o.Hue && Depth == o.Depth &&
            Flip == o.Flip && Mode == o.Mode && (Mode != SpriteCaptureMode.Sitted || SitMod == o.SitMod) && Rotated == o.Rotated &&
            Rotation == o.Rotation;

        public override readonly string ToString() =>
            $"{Mode} tex#{(Texture == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Texture))} at {Position.X},{Position.Y} src {Source} hue {Hue} depth {Depth} flip {Flip}" +
            (Mode == SpriteCaptureMode.Sitted ? $" mod {SitMod}" : "") + (Rotated ? $" rotated {Rotation}" : "");
    }

    /// <summary>The FNA atlas sprites a <see cref="DynamicSprite"/> names: the client's pack-backed Art and Animations, or a
    /// test fixture.</summary>
    internal interface ILegacySpriteSource
    {
        /// <summary>Art.GetArt(graphic): the art graphic (with its animated-art offset already applied).</summary>
        ref readonly SpriteInfo Art(uint graphic);

        /// <summary>Animations.GetAnimationFrames(body, group, dir, …, isEquip, isCorpse).</summary>
        Span<SpriteInfo> Frames(ushort body, byte group, byte dir, bool isEquip, bool isCorpse);
    }

    /// <summary>The client's loaders, as MobileView/ItemView/View.DrawStaticAnimated call them.</summary>
    internal sealed class ClientLegacySpriteSource : ILegacySpriteSource
    {
        public static readonly ClientLegacySpriteSource Instance = new ClientLegacySpriteSource();

        public ref readonly SpriteInfo Art(uint graphic) => ref Client.Game.UO.Arts.GetArt(graphic);

        public Span<SpriteInfo> Frames(ushort body, byte group, byte dir, bool isEquip, bool isCorpse) =>
            Client.Game.UO.Animations.GetAnimationFrames(body, group, dir, out _, out _, isEquip, isCorpse);
    }

    /// <summary>
    /// T7b: the Legacy half of the extract: a <see cref="DynamicSprite"/> to the batcher call the view makes for the same draw
    /// (View.DrawStaticAnimated's art draw; MobileView.DrawInternal's shadow, strip and seated draws; ItemView.DrawLayer's
    /// corpse strips). The record's position and depth are the view's already (no scene offset in Legacy), so the resolve
    /// only looks up the atlas texture and rect. A sprite the loader has no texture for yet (a direction still decoding)
    /// draws nothing, as in the view.
    /// </summary>
    internal static class LegacySpriteResolve
    {
        public static bool Resolve(ILegacySpriteSource src, in DynamicSprite s, out LegacyDraw d)
        {
            d = default;

            if (s.Kind == DynamicSpriteKind.Art)
            {
                ref readonly SpriteInfo art = ref src.Art(s.Id);

                if (art.Texture == null)
                {
                    return false;
                }

                d.Texture = art.Texture;
                d.Position = (s.Flags & DynamicSpriteFlags.MissingArt) != 0
                    ? new Vector2(s.X - ArtOffsetX(art.UV), s.Y - ArtOffsetY(art.UV))
                    : new Vector2(s.X, s.Y);
                d.Source = art.UV;
                d.Hue = s.Hue;
                d.Depth = s.Depth;
                d.Mode = SpriteCaptureMode.Normal;

                return true;
            }

            Span<SpriteInfo> frames = src.Frames(s.Body, s.Group, s.Dir, (s.Flags & DynamicSpriteFlags.Equip) != 0, (s.Flags & DynamicSpriteFlags.Corpse) != 0);

            if (s.Frame >= frames.Length)
            {
                return false;
            }

            ref readonly SpriteInfo fr = ref frames[s.Frame];

            if (fr.Texture == null)
            {
                return false;
            }

            d.Texture = fr.Texture;
            d.Position = new Vector2(s.X, s.Y);
            d.Hue = s.Hue;
            d.Depth = s.Depth;
            d.Flip = s.Flipped;

            if ((int)s.Hue.Y == ShaderHueTranslator.SHADER_SHADOW)
            {
                d.Source = fr.UV;
                d.Mode = SpriteCaptureMode.Shadow;
            }
            else if ((s.Flags & DynamicSpriteFlags.Sitted) != 0)
            {
                d.Source = fr.UV;
                d.Mode = SpriteCaptureMode.Sitted;
                d.SitMod = s.SitMod;
            }
            else
            {
                // the view's strip: its rows of the untrimmed frame; an empty strip draws nothing
                if (s.SrcH <= 0)
                {
                    return false;
                }

                d.Source = new Rectangle(fr.UV.X, fr.UV.Y + s.SrcY, fr.UV.Width, s.SrcH);
                d.Mode = SpriteCaptureMode.Normal;
            }

            return true;
        }

        /// <summary>T7b part 3: the effect sprite's batcher call (GameEffectView's View.DrawStaticRotated or DragEffect's
        /// View.DrawStatic). False: the atlas has no texture for the art (the view draws nothing either).</summary>
        public static bool ResolveEffect(ILegacySpriteSource src, in EffectSprite e, out LegacyDraw d)
        {
            d = default;
            ref readonly SpriteInfo art = ref src.Art(e.Sprite.Id);

            if (art.Texture == null)
            {
                return false;
            }

            d.Texture = art.Texture;
            d.Position = new Vector2(e.Sprite.X, e.Sprite.Y);
            d.Source = art.UV;
            d.Hue = e.Sprite.Hue;
            d.Depth = e.Sprite.Depth;
            d.Mode = SpriteCaptureMode.Normal;
            d.Rotated = e.Rotated;
            d.Rotation = e.Rotated ? e.Rotation : 0f;

            return true;
        }

        /// <summary>The effect's batcher call as its view makes it: the rotated rectangle draw (DrawStaticRotated) or the
        /// position draw with a unit scale (DrawStatic, not wet).</summary>
        public static void DrawEffect(UltimaBatcher2D batcher, in LegacyDraw d)
        {
            if (d.Rotated)
            {
                batcher.Draw(d.Texture, new Rectangle((int)d.Position.X, (int)d.Position.Y, d.Source.Width, d.Source.Height), d.Source, d.Hue, d.Rotation,
                    Vector2.Zero, SpriteEffects.None, d.Depth);
            }
            else
            {
                batcher.Draw(d.Texture, d.Position, d.Source, d.Hue, 0f, Vector2.Zero, Vector2.One, SpriteEffects.None, d.Depth);
            }
        }

        // View.DrawStaticAnimated's draw offset of an art sprite. For the placeholder it also writes it to the art entry, but
        // ArtLoader.GetEntry of art the pack lacks is a discarded scratch entry, so picking keeps 0, 0: nothing to store.
        private static int ArtOffsetX(Rectangle uv) => (uv.Width >> 1) - 22;

        private static int ArtOffsetY(Rectangle uv) => uv.Height - 44;

        /// <summary>The batcher call of <paramref name="d"/>, as the view makes it.</summary>
        public static void Draw(UltimaBatcher2D batcher, in LegacyDraw d)
        {
            switch (d.Mode)
            {
                case SpriteCaptureMode.Shadow:
                    batcher.DrawShadow(d.Texture, d.Position, d.Source, d.Flip, d.Depth);

                    break;

                case SpriteCaptureMode.Sitted:
                    batcher.DrawCharacterSitted(d.Texture, d.Position, d.Source, d.SitMod, d.Hue, d.Flip, d.Depth);

                    break;

                default:
                    batcher.Draw(d.Texture, d.Position, d.Source, d.Hue, 0f, Vector2.Zero, 1f, d.Flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None, d.Depth);

                    break;
            }
        }

        /// <summary>
        /// <see cref="Resolve"/> + <see cref="Draw"/> of an art record from its fields (ItemSpriteExtract.SelectDraw, the
        /// Legacy sink's item draw): the record's position (<paramref name="missing"/>: less the placeholder's draw offset),
        /// the atlas rect, no flip. Nothing without a texture.
        /// </summary>
        public static void DrawArt(UltimaBatcher2D batcher, in SpriteInfo art, int x, int y, bool missing, Vector3 hue, float depth)
        {
            if (art.Texture == null)
            {
                return;
            }

            Vector2 position = missing ? new Vector2(x - ArtOffsetX(art.UV), y - ArtOffsetY(art.UV)) : new Vector2(x, y);
            batcher.Draw(art.Texture, position, art.UV, hue, 0f, Vector2.Zero, 1f, SpriteEffects.None, depth);
        }

        /// <summary>Resolves and draws every record of <paramref name="sprites"/> in order; the number drawn.</summary>
        public static int DrawAll(UltimaBatcher2D batcher, ILegacySpriteSource src, ReadOnlySpan<DynamicSprite> sprites)
        {
            int n = 0;

            for (int i = 0; i < sprites.Length; i++)
            {
                if (Resolve(src, in sprites[i], out LegacyDraw d))
                {
                    Draw(batcher, in d);
                    n++;
                }
            }

            return n;
        }
    }
}
