// SPDX-License-Identifier: BSD-2-Clause

using System.Runtime.CompilerServices;
using ClassicUO.Game.GameObjects;
using Microsoft.Xna.Framework;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    /// <summary>
    /// An item's boat offset (the boat's own, and that of the items on its deck): the ECS
    /// <see cref="ClassicUO.Ecs.ItemOffset"/> component is its only home (T9 S7 deleted the Item's copy and
    /// CUO_ITEM_OFFSET_CHECK). <see cref="Push"/> is called by WorldWriter's boat operations; <see cref="Read"/> serves every
    /// reader (<see cref="GameObject.ScreenOffset"/>: depth, text, picking; the item extract's Gather through
    /// <see cref="ReadRow"/>). A mobile passenger's offset is in its Steps component (T6c) and on the Mobile; a boat's multi
    /// components are not world objects and keep theirs on the Multi. An item without a record (the house placement preview)
    /// has none: no operation writes one.
    /// </summary>
    internal static class ItemOffsets
    {
        /// <summary>
        /// The boat operations' ECS write: <paramref name="offset"/> into the item's component, added for a non-zero offset
        /// and removed at zero (so <see cref="ClassicUO.Ecs.WorldMirror.ItemOffsetCount"/> is zero while no boat moves).
        /// Nothing for a mobile (its Steps) or an item without a record.
        /// </summary>
        internal static void Push(World world, Entity entity, Vector3 offset)
        {
            if (entity is not Item item || !item.TryScalarEntity(out EcsEntity e))
            {
                return;
            }

            ClassicUO.Ecs.WorldMirror mirror = world.Ecs.Mirror;

            if (e.HasComponent<ClassicUO.Ecs.ItemOffset>())
            {
                if (offset == Vector3.Zero)
                {
                    e.RemoveComponent<ClassicUO.Ecs.ItemOffset>();
                    mirror.ItemOffsetCount--;

                    return;
                }

                ref ClassicUO.Ecs.ItemOffset c = ref e.GetComponent<ClassicUO.Ecs.ItemOffset>();
                c.X = offset.X;
                c.Y = offset.Y;
                c.Z = offset.Z;
            }
            else if (offset != Vector3.Zero)
            {
                e.AddComponent(new ClassicUO.Ecs.ItemOffset { X = offset.X, Y = offset.Y, Z = offset.Z });
                mirror.ItemOffsetCount++;
            }
        }

        // Codex audit P2 (accepted, owner's shared-serial rule of 2026-11-07): a record replaced by a mobile under the same
        // serial and then re-created starts without ItemOffset, so an item still riding a boat reads zero until the next
        // boat write. Only a misconfigured server sends an item and a mobile under one serial.
        /// <summary>The item's offset from its component (zero without one, or without a record).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector3 Read(Item item)
        {
            WorldEcs ecs = item.World?.Ecs;

            if (ecs == null || ecs.Mirror.ItemOffsetCount == 0)
            {
                return default;
            }

            return ReadRecord(item);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector3 ReadRecord(Item item)
        {
            return item.TryScalarEntity(out EcsEntity e) && ClassicUO.Ecs.EcsRead.TryGet(e, out ClassicUO.Ecs.ItemOffset c)
                ? new Vector3(c.X, c.Y, c.Z)
                : default;
        }

        /// <summary>
        /// <see cref="Read"/> for a reader that already holds the item's record row (the item extract's Gather): the component
        /// from <paramref name="row"/>. <paramref name="record"/>: false = no record (the placement preview): zero.
        /// </summary>
        internal static Vector3 ReadRow(bool record, in ClassicUO.Ecs.EcsRow row)
        {
            return record && row.TryGet(out ClassicUO.Ecs.ItemOffset c) ? new Vector3(c.X, c.Y, c.Z) : default;
        }
    }
}
