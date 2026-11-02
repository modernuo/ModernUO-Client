// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game
{
    /// <summary>
    /// E5: the mirror entity a mobile is drawn from (GpuWorldRenderer's ECS mobile path and the Legacy sink).
    /// </summary>
    internal static class MobileEntityLookup
    {
        /// <summary>
        /// The entity found last time, while no mirror entity was deleted since (WorldMirror.Epoch; Friflo's 16-bit
        /// revision can wrap, so IsNull is not trusted) and the serial is unchanged. Only entities that had an
        /// AnimationState are cached, and a mirror entity never loses it. Costs two integer compares per mobile; every
        /// deletion makes all mobiles repeat one dictionary lookup. False: not mirrored with an AnimationState (the cache is
        /// cleared). The caller checks IsDestroyed / AllowedToDraw first.
        /// </summary>
        public static bool TryGet(Mobile mobile, out Friflo.Engine.ECS.Entity e)
        {
            WorldMirror mirror = mobile.World.Ecs.Mirror;
            e = mobile.EcsEntity;

            if (mobile.EcsEntitySerial != mobile.Serial || mobile.EcsEntityEpoch != mirror.Epoch)
            {
                if (!mirror.TryGetEntity(mobile.Serial, out e) || !e.HasComponent<AnimationState>())
                {
                    mobile.EcsEntity = default;
                    mobile.EcsEntitySerial = 0;

                    return false;
                }

                mobile.EcsEntity = e;
                mobile.EcsEntitySerial = mobile.Serial;
                mobile.EcsEntityEpoch = mirror.Epoch;
                mobile.EcsEntityHeld = false; // T4: no identity check here (Entity.TryScalarEntity redoes its own)
            }

            return true;
        }
    }
}
