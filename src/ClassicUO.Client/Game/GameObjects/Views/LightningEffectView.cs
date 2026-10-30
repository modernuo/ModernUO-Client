// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.IO;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.GameObjects
{
    internal sealed partial class LightningEffect
    {
        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            ushort hue = Hue;

            if (ProfileManager.CurrentProfile.NoColorObjectsOutOfRange && Distance > World.ClientViewRange)
            {
                hue = Constants.OUT_RANGE_COLOR;
            }
            else if (World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect)
            {
                hue = Constants.DEAD_RANGE_COLOR;
            }

            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, false, 1);
            hueVec.Y = hueVec.X > 1.0f ? ShaderHueTranslator.SHADER_LIGHTS : ShaderHueTranslator.SHADER_NONE;

            Client.Game.UO.FileManager.Gumps.TryGetSize(AnimationGraphic, out int gumpWidth, out int gumpHeight);

            posX -= gumpWidth >> 1;
            posY -= gumpHeight;

            batcher.SetBlendState(BlendState.Additive);

            DrawGump
            (
                batcher,
                AnimationGraphic,
                posX,
                posY,
                hueVec,
                depth
            );

            batcher.SetBlendState(null);

            return true;
        }
    }
}
