// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.GameObjects
{
    internal class DragEffect : GameEffect
    {
        /// <summary>The next offset step's time (with ecs_effect_endpoints a cache of the state's MoveTime).</summary>
        internal uint LastMoveTime;

        public DragEffect
        (
            World world,
            EffectManager manager,
            uint src,
            uint trg,
            ushort xSource,
            ushort ySource,
            sbyte zSource,
            ushort xTarget,
            ushort yTarget,
            sbyte zTarget,
            ushort graphic,
            ushort hue,
            int duration,
            byte speed
        )
            : base(world, manager, graphic, hue, duration, speed)
        {
            Entity source = World.Get(src);

            if (SerialHelper.IsValid(src) && source != null)
            {
                SetSource(source);
            }
            else
            {
                SetSource(xSource, ySource, zSource);
            }


            Entity target = World.Get(trg);

            if (SerialHelper.IsValid(trg) && target != null)
            {
                SetTarget(target);
            }
            else
            {
                SetTarget(xTarget, yTarget, zTarget);
            }

            Hue = hue;
            Graphic = graphic;
        }

        public override void Update()
        {
            if (UsesEndpoints && !EcsEntity.IsNull)
            {
                EffectSystem.StepDrag(this);

                return;
            }

            if (LastMoveTime > Time.Ticks)
            {
                return;
            }

            OffsetX += 8;
            OffsetY += 8;

            LastMoveTime = Time.Ticks + 20;

            base.Update();
        }

        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (IsDestroyed)
            {
                return false;
            }

            ushort hue;
            if (ProfileManager.CurrentProfile.NoColorObjectsOutOfRange && Distance > World.ClientViewRange)
            {
                hue = Constants.OUT_RANGE_COLOR;
            }
            else if (World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect)
            {
                hue = Constants.DEAD_RANGE_COLOR;
            }
            else
            {
                hue = Hue;
            }

            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue);

            //Engine.DebugInfo.EffectsRendered++;

            DrawStatic
            (
                batcher,
                AnimationGraphic,
                posX - ((int) Offset.X + 22),
                posY - ((int) -Offset.Y + 22),
                hueVec,
                depth
            );

            ref var data = ref Client.Game.UO.FileManager.TileData.StaticData[Graphic];

            GameObject source;

            if (data.IsLight && (source = SourceObject) != null)
            {
                Client.Game.GetScene<GameScene>().AddLight(source, source, posX + 22, posY + 22);
            }

            return true;
        }
    }
}