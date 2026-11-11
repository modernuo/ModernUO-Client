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
            EffectSystem.StepLightning(this);
        }
    }
}