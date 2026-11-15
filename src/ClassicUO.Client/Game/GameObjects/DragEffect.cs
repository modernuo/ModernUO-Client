// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Ecs;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.GameObjects
{
    internal class DragEffect : GameEffect
    {
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
        }

        public override void Update()
        {
            EffectSystem.StepDrag(this);
        }
    }
}