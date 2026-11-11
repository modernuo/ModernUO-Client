// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#if CUO_DEVTOOLS
using System.Collections.Generic;
using ClassicUO.Utility.Logging;
#endif

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// T7b part 3: graphic effects drawn from the renderer-neutral <see cref="EffectSpriteExtract"/> over their
    /// <see cref="ClassicUO.Ecs.EffectState"/>, at the effect's render-list position (painter order unchanged): the Legacy
    /// world through <see cref="DrawLegacy"/> (the deleted view's batcher call and blend state,
    /// <see cref="LegacySpriteResolve.ResolveEffect"/>), the Gpu world through GpuWorldRenderer.DrawEffect
    /// (<see cref="GpuEffectResolve"/>). The light at the source comes from the extract. Lightning is one gump drawn
    /// additively by the batcher in both worlds (<see cref="DrawLightning"/>). T9 S8a deleted the view fallbacks: an effect
    /// without a state and art the pack lacks draw nothing.
    /// </summary>
    internal static class EffectSpriteSink
    {
        private static readonly EffectSpriteExtract _extract = new EffectSpriteExtract();

        internal static EffectSpriteExtract Extract => _extract;

        /// <summary>The Legacy world's draw of one effect (effect list, or the transparent list for a translucent one).</summary>
        public static bool DrawLegacy(UltimaBatcher2D batcher, GameEffect e, float depth)
        {
            if (!EffectSpriteInput.Gather(e, out EffectSpriteInput input))
            {
                return false;
            }

            bool drawn = _extract.Select(in input, e.RealScreenPosition.X, e.RealScreenPosition.Y, depth);

            if (_extract.MissingArt)
            {
                NoteMissingArt(input.AnimGraphic);

                return false;
            }

            if (_extract.HasLightning)
            {
                DrawLightning(batcher, in _extract.Lightning);

                return drawn;
            }

            if (_extract.HasSprite)
            {
                ref readonly EffectSprite sprite = ref _extract.Sprite;
                // GameEffectView.Draw set its blend state around the draw; DragEffect.Draw set none
                BlendState blend = sprite.Rotated ? GameEffect.BlendStateOf(sprite.Blend) : null;

                if (blend != null)
                {
                    batcher.SetBlendState(blend);
                }

                if (LegacySpriteResolve.ResolveEffect(ClientLegacySpriteSource.Instance, in sprite, out LegacyDraw d))
                {
                    LegacySpriteResolve.DrawEffect(batcher, in d);
                }

                if (blend != null)
                {
                    batcher.SetBlendState(null);
                }
            }

            if (_extract.Light)
            {
                Client.Game.GetScene<GameScene>()?.AddLight(input.Source, input.Source, _extract.LightX, _extract.LightY);
            }

            return drawn;
        }

        /// <summary>
        /// LightningEffectView.Draw's batcher call: the gump centred on the sprite's x with its bottom at its y, additive blend.
        /// Both worlds draw it through the batcher (the Gpu world's capture had no gump mapping, so the view's draw went to
        /// FNA there too). A gump without a texture draws nothing, as in the view.
        /// </summary>
        public static void DrawLightning(UltimaBatcher2D batcher, in LightningSprite s)
        {
            Client.Game.UO.FileManager.Gumps.TryGetSize(s.Gump, out int width, out int height);
            batcher.SetBlendState(BlendState.Additive);
            ref readonly var gump = ref Client.Game.UO.Gumps.GetGump(s.Gump);

            if (gump.Texture != null)
            {
                batcher.Draw(gump.Texture, new Vector2(s.X - (width >> 1), s.Y - height), gump.UV, s.Hue, 0f, Vector2.Zero, 1f, SpriteEffects.None,
                    s.Depth);
            }

            batcher.SetBlendState(null);
        }

#if CUO_DEVTOOLS
        // CLEANUP(release): each effect graphic the pack lacks is logged once (a pack bug; it draws nothing)
        private static HashSet<ushort> _missingLogged;
#endif

        /// <summary>An effect's art is missing from the pack: logged once per graphic in dev builds.</summary>
        internal static void NoteMissingArt(ushort graphic)
        {
#if CUO_DEVTOOLS
            _missingLogged ??= new HashSet<ushort>();

            if (_missingLogged.Add(graphic))
            {
                Log.Warn($"[effect-missing-art] effect art 0x{graphic:X4} is not in the pack: not drawn");
            }
#endif
        }
    }
}
