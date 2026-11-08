// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// T7b part 3 (setting <c>ecs_effect_sprites</c>, needs <c>ecs_effects</c>): graphic effects drawn from the
    /// renderer-neutral <see cref="EffectSpriteExtract"/> over their <see cref="ClassicUO.Ecs.EffectState"/> instead of
    /// GameEffectView.Draw, at the effect's render-list position (painter order unchanged): the Legacy world through
    /// <see cref="DrawLegacy"/> (the view's batcher call and blend state, <see cref="LegacySpriteResolve.ResolveEffect"/>), the
    /// Gpu world through GpuWorldRenderer.DrawEffect (<see cref="GpuEffectResolve"/>). The light at the source comes from the
    /// extract. Lightning, effects without a state and art the pack lacks take the view. CUO_EFFECT_SPRITE_CHECK=1
    /// (<see cref="EffectSpriteCheck"/>) compares both paths for every effect drawn.
    /// </summary>
    internal static class EffectSpriteSink
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_ECS_EFFECT_SPRITES=1|0)
        private static readonly bool? _env = LegacySpriteSink.ParseSwitch(Environment.GetEnvironmentVariable("CUO_ECS_EFFECT_SPRITES"));
#else
        private static readonly bool? _env = null;
#endif

        /// <summary>DevBench (effectsprites:on|off) and tests: wins over the env override and the setting.</summary>
        internal static bool? Override;

        private static readonly EffectSpriteExtract _extract = new EffectSpriteExtract();

        /// <summary>This frame's effects are drawn from the extract.</summary>
        public static bool On { get; private set; }

        internal static EffectSpriteExtract Extract => _extract;

        /// <summary>The switch: without ecs_effects there is no state to draw from.</summary>
        internal static bool Resolve(bool effectsOn, bool? over, bool? env, bool setting) => effectsOn && (over ?? env ?? setting);

        /// <summary>Start of a world draw (both renderers, before the first effect is drawn or captured): reads the switch.</summary>
        public static void BeginFrame()
        {
            On = Resolve(EffectSystem.GlobalOn, Override, _env, Configuration.Settings.GlobalSettings.EcsEffectSprites);
#if CUO_DEVTOOLS
            EffectSpriteCheck.BeginFrame();
#endif
        }

        /// <summary>The Legacy world's effect list: one effect drawn by the extract while the switch is on (or compared while
        /// the checker is), by its view otherwise.</summary>
        public static bool DrawLegacy(UltimaBatcher2D batcher, GameObject obj, float depth)
        {
            if (obj is GameEffect e && EffectSpriteExtract.Handles(e))
            {
#if CUO_DEVTOOLS
                if (EffectSpriteCheck.Active)
                {
                    return EffectSpriteCheck.CompareLegacy(batcher, e, depth, On);
                }
#endif

                if (On && TryDrawLegacy(batcher, e, depth, true, out bool drawn))
                {
                    return drawn;
                }
            }

            return obj.Draw(batcher, obj.RealScreenPosition.X, obj.RealScreenPosition.Y, depth);
        }

        /// <summary>
        /// The extract's draw of <paramref name="e"/> in the Legacy world. False: it takes the view (no state, or art the pack
        /// lacks); nothing was drawn. <paramref name="light"/>: add the source's light (the checker compares it first).
        /// </summary>
        internal static bool TryDrawLegacy(UltimaBatcher2D batcher, GameEffect e, float depth, bool light, out bool drawn)
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

            if (light && _extract.Light)
            {
                Client.Game.GetScene<GameScene>()?.AddLight(input.Source, input.Source, _extract.LightX, _extract.LightY);
            }

            return true;
        }
    }
}
