// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Managers;

namespace ClassicUO.Game.GameObjects
{
    internal sealed partial class LightningEffect : GameEffect
    {
        public LightningEffect(World world, EffectManager manager, uint src, ushort x, ushort y, sbyte z, ushort hue)
            : base(world, manager, 0x4E20, hue, 400, 0)
        {
            IsEnabled = true;
            AnimIndex = 0;

            Entity source = World.Get(src);

            if (SerialHelper.IsValid(src) && source != null)
            {
                SetSource(source);
            }
            else
            {
                SetSource(x, y, z);
            }
        }

        public override void Update()
        {
            if (UsesEndpoints && !EcsEntity.IsNull)
            {
                EffectSystem.StepLightning(this);

                return;
            }

            if (!IsDestroyed)
            {
                if (EffectSystem.AnimateLightning(this))
                {
                    (var x, var y, var z) = LegacySource();

                    if (X != x || Y != y || Z != z)
                    {
                        SetInWorldTile(x, y, z);
                    }
                }
            }
        }
    }
}