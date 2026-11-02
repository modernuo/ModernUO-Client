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

        public readonly bool Same(in LegacyDraw o) =>
            ReferenceEquals(Texture, o.Texture) && Position == o.Position && Source == o.Source && Hue == o.Hue && Depth == o.Depth &&
            Flip == o.Flip && Mode == o.Mode && (Mode != SpriteCaptureMode.Sitted || SitMod == o.SitMod);

        public override readonly string ToString() =>
            $"{Mode} tex#{(Texture == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Texture))} at {Position.X},{Position.Y} src {Source} hue {Hue} depth {Depth} flip {Flip}" +
            (Mode == SpriteCaptureMode.Sitted ? $" mod {SitMod}" : "");
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
                d.Position = new Vector2(s.X, s.Y);
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
