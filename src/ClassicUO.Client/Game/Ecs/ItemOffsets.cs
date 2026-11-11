// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    /// <summary>
    /// An item's boat offset (the boat's own, and that of the items on its deck) as the ECS
    /// <see cref="ClassicUO.Ecs.ItemOffset"/> component (T9 deleted <c>ecs_item_offset</c>'s off path). <see cref="Push"/> is
    /// called by WorldWriter's boat operations with the values they write to the GameObject; <see cref="Read"/> serves the
    /// per-frame readers (the item extract's Gather, the item pick). A mobile passenger's offset is in its Steps component
    /// (T6c); a boat's multi components are not world objects and keep theirs on the Multi. CUO_ITEM_OFFSET_CHECK=1 compares
    /// the component with the Item's Offset (still written by the same operations and read by the views) at every read.
    /// </summary>
    internal static class ItemOffsets
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): CUO_ITEM_OFFSET_CHECK=1, the component against the Item's Offset at every read
        public static readonly bool EnvCheck = Environment.GetEnvironmentVariable("CUO_ITEM_OFFSET_CHECK") == "1";
#else
        public static readonly bool EnvCheck = false;
#endif

        /// <summary>Tests: the checker on or off (null: the environment).</summary>
        internal static bool? CheckOverride;

        public static bool Check => CheckOverride ?? EnvCheck;

        public static long Writes, Adds, Reads, Fallbacks, Checks, Diffs;
        private static int _logged;
        private static uint _logAt;

        /// <summary>Tests: the counters back to zero.</summary>
        internal static void ResetCounters()
        {
            Writes = Adds = Reads = Fallbacks = Checks = Diffs = 0;
            _logged = 0;
            _logAt = 0;
        }

        /// <summary>
        /// The boat operations' ECS write: <paramref name="offset"/> into the item's component (added when the offset is not
        /// zero and the item has none yet). Nothing for a mobile (its Steps) or an item without a record.
        /// </summary>
        internal static void Push(World world, Entity entity, Vector3 offset)
        {
            if (entity is not Item item || !item.TryScalarEntity(out EcsEntity e))
            {
                return;
            }

            if (e.HasComponent<ClassicUO.Ecs.ItemOffset>())
            {
                ref ClassicUO.Ecs.ItemOffset c = ref e.GetComponent<ClassicUO.Ecs.ItemOffset>();
                c.X = offset.X;
                c.Y = offset.Y;
                c.Z = offset.Z;
                Writes++;
            }
            else if (offset != Vector3.Zero)
            {
                e.AddComponent(new ClassicUO.Ecs.ItemOffset { X = offset.X, Y = offset.Y, Z = offset.Z });
                Writes++;
                Adds++;
            }
        }

        // Codex audit P2 (accepted, owner's shared-serial rule of 2026-11-07): a record replaced by a mobile under the same
        // serial and then re-created starts without ItemOffset, so an item still riding a boat reads zero until the next
        // boat write. Only a misconfigured server sends an item and a mobile under one serial; the record is not reseeded
        // from the object, as the cold scalars are not.
        /// <summary>
        /// The item's offset from its component (zero without one); for an item without a record (the house placement
        /// preview) the Item's.
        /// </summary>
        internal static Vector3 Read(Item item)
        {
            Vector3 v;

            if (item.TryScalarEntity(out EcsEntity e))
            {
                v = ClassicUO.Ecs.EcsRead.TryGet(e, out ClassicUO.Ecs.ItemOffset c) ? new Vector3(c.X, c.Y, c.Z) : Vector3.Zero;
                Reads++;
            }
            else
            {
                v = item.Offset;
                Fallbacks++;
            }

            if (Check)
            {
                Compare(item, v);
            }

            return v;
        }

        /// <summary>
        /// <see cref="Read"/> for a reader that already holds the item's record row (the item extract's
        /// Gather): the component from <paramref name="row"/> instead of a second TryScalarEntity and node lookup.
        /// <paramref name="record"/>: false = no record (the placement preview), read the Item's.
        /// </summary>
        internal static Vector3 ReadRow(Item item, bool record, in ClassicUO.Ecs.EcsRow row)
        {
            Vector3 v;

            if (record)
            {
                v = row.TryGet(out ClassicUO.Ecs.ItemOffset c) ? new Vector3(c.X, c.Y, c.Z) : Vector3.Zero;
                Reads++;
            }
            else
            {
                v = item.Offset;
                Fallbacks++;
            }

            if (Check)
            {
                Compare(item, v);
            }

            return v;
        }

        private static void Compare(Item item, Vector3 ecs)
        {
            Checks++;

            if (ecs == item.Offset)
            {
                return;
            }

            Diffs++;

            if (_logged++ < 10)
            {
                Log.Warn($"[item-offset-check] 0x{item.Serial:X8} g=0x{item.Graphic:X4} at {item.X},{item.Y},{item.Z}: ecs {ecs} | item {item.Offset} (tick {Time.Ticks})");
            }
        }

        /// <summary>The counters, for the periodic log line.</summary>
        internal static string Summary() =>
            $"[item-offset-check] writes {Writes} (adds {Adds}), ecs reads {Reads}, fallbacks to the Item {Fallbacks}; checked {Checks}, different {Diffs}";

        /// <summary>Every 5 s while the checker is on: <see cref="Summary"/> (BoatMovingManager.Update, once per frame).</summary>
        internal static void LogSummary()
        {
            if (!Check || Time.Ticks < _logAt)
            {
                return;
            }

            _logAt = Time.Ticks + 5000;
            Log.Info(Summary());
        }
    }
}
