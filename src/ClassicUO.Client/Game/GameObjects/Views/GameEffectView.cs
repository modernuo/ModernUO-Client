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


        /// <summary>The blend state <see cref="Draw"/> sets around its draw for <paramref name="blend"/>; null = none set
        /// (Normal, ScreenRed). T7b part 3: the Legacy effect sink sets the same one.</summary>
        internal static BlendState BlendStateOf(GraphicEffectBlendMode blend) => blend switch
        {
            GraphicEffectBlendMode.Multiply => _multiplyBlendState.Value,
            GraphicEffectBlendMode.Screen or GraphicEffectBlendMode.ScreenMore => _screenBlendState.Value,
            GraphicEffectBlendMode.ScreenLess => _screenLessBlendState.Value,
            GraphicEffectBlendMode.NormalHalfTransparent => _normalHalfBlendState.Value,
            GraphicEffectBlendMode.ShadowBlue => _shadowBlueBlendState.Value,
            _ => null
        };

        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (IsDestroyed || !AllowedToDraw || EcsEntity.IsNull)
            {
                return false;
            }

            // T9 S7: the frame, offset and angle from the state (the extract's input; this draw is its fallback)
            ref readonly EffectState s = ref EcsRead.Get<EffectState>(EcsEntity);
            ushort animationGraphic = s.AnimGraphic;
            float angle = s.Angle;

            if (animationGraphic == EffectSystem.NO_FRAME)
            {
                return false;
            }

            ref StaticTiles data = ref Client.Game.UO.FileManager.TileData.StaticData[Graphic];

            posX += (int)s.OffsetX;
            posY += (int)(s.OffsetZ + s.OffsetY);

            ushort hue = Hue;

            if (ProfileManager.CurrentProfile.NoColorObjectsOutOfRange && Distance > World.ClientViewRange)
            {
                hue = Constants.OUT_RANGE_COLOR;
            }
            else if (World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect)
            {
                hue = Constants.DEAD_RANGE_COLOR;
            }

            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, data.IsPartialHue, data.IsTranslucent ? .5f : 1f, effect: true);

            GameObject source = SourceObject;

            if (source != null)
            {
                depth = source.CalculateDepthZ() + 1f;
            }

            switch (Blend)
            {
                case GraphicEffectBlendMode.Multiply:
                    batcher.SetBlendState(_multiplyBlendState.Value);

                    DrawStaticRotated
                    (
                        batcher,
                        animationGraphic,
                        posX,
                        posY,
                        angle,
                        hueVec,
                        depth
                    );

                    batcher.SetBlendState(null);

                    break;

                case GraphicEffectBlendMode.Screen:
                case GraphicEffectBlendMode.ScreenMore:
                    batcher.SetBlendState(_screenBlendState.Value);

                    DrawStaticRotated
                    (
                        batcher,
                        animationGraphic,
                        posX,
                        posY,
                        angle,
                        hueVec,
                        depth
                    );

                    batcher.SetBlendState(null);

                    break;

                case GraphicEffectBlendMode.ScreenLess:
                    batcher.SetBlendState(_screenLessBlendState.Value);

                    DrawStaticRotated
                    (
                        batcher,
                        animationGraphic,
                        posX,
                        posY,
                        angle,
                        hueVec,
                        depth
                    );

                    batcher.SetBlendState(null);

                    break;

                case GraphicEffectBlendMode.NormalHalfTransparent:
                    batcher.SetBlendState(_normalHalfBlendState.Value);

                    DrawStaticRotated
                    (
                        batcher,
                        animationGraphic,
                        posX,
                        posY,
                        angle,
                        hueVec,
                        depth
                    );

                    batcher.SetBlendState(null);

                    break;

                case GraphicEffectBlendMode.ShadowBlue:
                    batcher.SetBlendState(_shadowBlueBlendState.Value);

                    DrawStaticRotated
                    (
                        batcher,
                        animationGraphic,
                        posX,
                        posY,
                        angle,
                        hueVec,
                        depth
                    );

                    batcher.SetBlendState(null);

                    break;

                default:
                    //if (Graphic == 0x36BD)
                    //{
                    //    hueVector = ShaderHueTranslator.GetHueVector(0);
                    //    HueVector.X = 0;
                    //    HueVector.Y = ShaderHueTranslator.SHADER_LIGHTS;
                    //    HueVector.Z = 0;
                    //    batcher.SetBlendState(BlendState.Additive);
                    //    base.Draw(batcher, posX, posY);
                    //    batcher.SetBlendState(null);
                    //}
                    //else

                    DrawStaticRotated
                    (
                        batcher,
                        animationGraphic,
                        posX,
                        posY,
                        angle,
                        hueVec,
                        depth
                    );

                    break;
            }

            //Engine.DebugInfo.EffectsRendered++;


            if (data.IsLight && source != null)
            {
                Client.Game.GetScene<GameScene>().AddLight(source, source, posX + 22, posY + 22);
            }

            return true;
        }

        public override bool CheckMouseSelection()
        {
            return false;
        }

    }
}
