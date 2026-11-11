// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// T7b part 3: graphic effects drawn from the renderer-neutral <see cref="EffectSpriteExtract"/> over their
    /// <see cref="ClassicUO.Ecs.EffectState"/> instead of GameEffectView.Draw, at the effect's render-list position (painter
    /// order unchanged): the Legacy world through <see cref="DrawLegacy"/> (the view's batcher call and blend state,
    /// <see cref="LegacySpriteResolve.ResolveEffect"/>), the Gpu world through GpuWorldRenderer.DrawEffect
    /// (<see cref="GpuEffectResolve"/>). The light at the source comes from the extract. Lightning, effects without a state and
    /// art the pack lacks take the view. T9 deleted <c>ecs_effect_sprites</c>' off path and CUO_EFFECT_SPRITE_CHECK.
    /// </summary>
    internal static class EffectSpriteSink
    {
        private static readonly EffectSpriteExtract _extract = new EffectSpriteExtract();

        internal static EffectSpriteExtract Extract => _extract;

        /// <summary>The Legacy world's effect list: one effect drawn by the extract, by its view when the extract leaves it.</summary>
        public static bool DrawLegacy(UltimaBatcher2D batcher, GameObject obj, float depth)
        {
            if (obj is GameEffect e && EffectSpriteExtract.Handles(e) && TryDrawLegacy(batcher, e, depth, out bool drawn))
            {
                return drawn;
            }

            return obj.Draw(batcher, obj.RealScreenPosition.X, obj.RealScreenPosition.Y, depth);
        }

        /// <summary>
        /// The extract's draw of <paramref name="e"/> in the Legacy world, with the source's light. False: it takes the view (no
        /// state, or art the pack lacks); nothing was drawn.
        /// </summary>
        private static bool TryDrawLegacy(UltimaBatcher2D batcher, GameEffect e, float depth, out bool drawn)
        {
            drawn = false;

            if (!EffectSpriteInput.Gather(e, out EffectSpriteInput input))
            {
                return false;
            }

            drawn = _extract.Select(in input, e.RealScreenPosition.X, e.RealScreenPosition.Y, depth);

            if (_extract.MissingArt)
            {
                drawn = false;

                return false;
            }

            if (_extract.HasSprite)
            {
                ref readonly EffectSprite sprite = ref _extract.Sprite;
                // GameEffectView.Draw sets its blend state around the draw; DragEffect.Draw sets none
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

            return true;
        }
    }
}
