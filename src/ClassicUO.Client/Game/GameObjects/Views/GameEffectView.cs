using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClassicUO.Configuration;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Game.Scenes;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.GameObjects
{
    partial class GameEffect
    {
        private static readonly Lazy<BlendState> _multiplyBlendState = new Lazy<BlendState>
        (
            () =>
            {
                BlendState state = new BlendState
                {
                    ColorSourceBlend = Microsoft.Xna.Framework.Graphics.Blend.Zero,
                    ColorDestinationBlend = Microsoft.Xna.Framework.Graphics.Blend.SourceColor
                };

                return state;
            }
        );

        private static readonly Lazy<BlendState> _screenBlendState = new Lazy<BlendState>
        (
            () =>
            {
                BlendState state = new BlendState
                {
                    ColorSourceBlend = Microsoft.Xna.Framework.Graphics.Blend.One,
                    ColorDestinationBlend = Microsoft.Xna.Framework.Graphics.Blend.One
                };

                return state;
            }
        );

        private static readonly Lazy<BlendState> _screenLessBlendState = new Lazy<BlendState>
        (
            () =>
            {
                BlendState state = new BlendState
                {
                    ColorSourceBlend = Microsoft.Xna.Framework.Graphics.Blend.DestinationColor,
                    ColorDestinationBlend = Microsoft.Xna.Framework.Graphics.Blend.InverseSourceAlpha
                };

                return state;
            }
        );

        private static readonly Lazy<BlendState> _normalHalfBlendState = new Lazy<BlendState>
        (
            () =>
            {
                BlendState state = new BlendState
                {
                    ColorSourceBlend = Microsoft.Xna.Framework.Graphics.Blend.DestinationColor,
                    ColorDestinationBlend = Microsoft.Xna.Framework.Graphics.Blend.SourceColor
                };

                return state;
            }
        );

        private static readonly Lazy<BlendState> _shadowBlueBlendState = new Lazy<BlendState>
        (
            () =>
            {
                BlendState state = new BlendState
                {
                    ColorSourceBlend = Microsoft.Xna.Framework.Graphics.Blend.SourceColor,
                    ColorDestinationBlend = Microsoft.Xna.Framework.Graphics.Blend.InverseSourceColor,
                    ColorBlendFunction = BlendFunction.ReverseSubtract
                };

                return state;
            }
        );


        /// <summary>The blend state GameEffectView.Draw set around its draw for <paramref name="blend"/>; null = none set
        /// (Normal, ScreenRed). The Legacy effect sink sets it (T7b part 3; T9 S8a deleted the view's Draw).</summary>
        internal static BlendState BlendStateOf(GraphicEffectBlendMode blend) => blend switch
        {
            GraphicEffectBlendMode.Multiply => _multiplyBlendState.Value,
            GraphicEffectBlendMode.Screen or GraphicEffectBlendMode.ScreenMore => _screenBlendState.Value,
            GraphicEffectBlendMode.ScreenLess => _screenLessBlendState.Value,
            GraphicEffectBlendMode.NormalHalfTransparent => _normalHalfBlendState.Value,
            GraphicEffectBlendMode.ShadowBlue => _shadowBlueBlendState.Value,
            _ => null
        };

        public override bool CheckMouseSelection()
        {
            return false;
        }

    }
}
