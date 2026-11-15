// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    /// <summary>
    /// An item's boat offset (the boat's own, and that of the items on its deck): the ECS
    /// <see cref="ClassicUO.Ecs.ItemOffset"/> component is its only home (T9 S7 deleted the Item's copy and
    /// CUO_ITEM_OFFSET_CHECK). <see cref="Push"/> is called by WorldWriter's boat operations; <see cref="Read"/> serves every
    /// reader (<see cref="GameObject.ScreenOffset"/>: depth, text, picking; the item extract's Gather through
    /// <see cref="ReadRow(Item, bool, in ClassicUO.Ecs.EcsRow)"/>). A mobile passenger's offset is in its Steps component (T6c)
    /// and on the Mobile; a boat's multi components are not world objects and keep theirs on the Multi. An item without a
    /// record (the house placement preview) has none: no operation writes one.
    /// perf-17, item_offset_flag (default off): <see cref="Push"/> also sets <see cref="Item.ItemOffsetHeld"/> (the offset it
    /// wrote is not zero) and the readers test that flag instead of the mirror's count, so while a boat moves the items that
    /// are not on it skip the record lookup. CUO_ITEM_OFFSET_FLAG_CHECK=1 runs the count path next to it at every read.
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
            // item_offset_flag: written whatever the switch, so a switch turned on mid-session finds every flag right
            item.ItemOffsetHeld = offset != Vector3.Zero;

            if (e.HasComponent<ClassicUO.Ecs.ItemOffset>())
            {
                if (offset == Vector3.Zero)
                {
                    e.RemoveComponent<ClassicUO.Ecs.ItemOffset>();
                    mirror.ItemOffsetCount--;
                    // pa-gpusig: the Gpu item cache holds only items without an offset; a passenger is not the operation's
                    // subject, so its chunk is told here
                    ClassicUO.Game.Scenes.GpuItemGen.Touch(item);

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
                ClassicUO.Game.Scenes.GpuItemGen.Touch(item);
            }
        }

        // Codex audit P2 (accepted, owner's shared-serial rule of 2026-11-07): a record replaced by a mobile under the same
        // serial and then re-created starts without ItemOffset, so an item still riding a boat reads zero until the next
        // boat write. Only a misconfigured server sends an item and a mobile under one serial.
        /// <summary>The item's offset from its component (zero without one, or without a record).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector3 Read(Item item)
        {
            if (_flagFrame)
            {
                return ReadFlagged(item);
            }

            return ReadCounted(item);
        }

        // the count path: zero while no item of the world has an offset
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 ReadCounted(Item item)
        {
            WorldEcs ecs = item.World?.Ecs;

            if (ecs == null || ecs.Mirror.ItemOffsetCount == 0)
            {
                return default;
            }

            return ReadRecord(item);
        }

        // item_offset_flag: zero unless the last Push gave this instance an offset. An instance that takes over a record
        // with an offset reads zero until the next boat write (each step of a moving boat pushes its passengers), as a
        // re-created record already does (Codex P2 above)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 ReadFlagged(Item item)
        {
#if CUO_DEVTOOLS
            if (_checkFrame)
            {
                return ReadChecked(item);
            }
#endif
            return item.ItemOffsetHeld ? ReadHeld(item) : default;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector3 ReadHeld(Item item) => item.World?.Ecs == null ? default : ReadRecord(item);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector3 ReadRecord(Item item)
        {
            return item.TryScalarEntity(out EcsEntity e) && ClassicUO.Ecs.EcsRead.TryGet(e, out ClassicUO.Ecs.ItemOffset c)
                ? new Vector3(c.X, c.Y, c.Z)
                : default;
        }

        /// <summary>
        /// <see cref="Read"/> for a reader that already holds the item's record row: the component from
        /// <paramref name="row"/>. <paramref name="record"/>: false = no record (the placement preview): zero.
        /// </summary>
        internal static Vector3 ReadRow(bool record, in ClassicUO.Ecs.EcsRow row)
        {
            return record && row.TryGet(out ClassicUO.Ecs.ItemOffset c) ? new Vector3(c.X, c.Y, c.Z) : default;
        }

        /// <summary>
        /// The item extract's Gather: <see cref="ReadRow(bool, in ClassicUO.Ecs.EcsRow)"/>, under item_offset_flag without the
        /// row lookup for an item whose flag is clear.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector3 ReadRow(Item item, bool record, in ClassicUO.Ecs.EcsRow row)
        {
            if (!_flagFrame)
            {
                return ReadRow(record, in row);
            }

#if CUO_DEVTOOLS
            if (_checkFrame)
            {
                return ReadRowChecked(item, record, in row);
            }
#endif
            return item.ItemOffsetHeld ? ReadRow(record, in row) : default;
        }

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_ITEM_OFFSET_FLAG=1|0)
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_ITEM_OFFSET_FLAG") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        // CLEANUP(release): CUO_ITEM_OFFSET_FLAG_CHECK=1
        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_ITEM_OFFSET_FLAG_CHECK") == "1";

        /// <summary>Tests: the checker on (null: CUO_ITEM_OFFSET_FLAG_CHECK).</summary>
        internal static bool? CheckOverrideForTests;
#else
        private static readonly bool? _env = null;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? Settings.GlobalSettings.ItemOffsetFlag;

        // the switch and the checker, taken once per world fill (BeginFrame): the readers run per item
        private static bool _flagFrame;
#if CUO_DEVTOOLS
        private static bool _checkFrame;
#endif

        /// <summary>Start of a world fill (and tests after changing an override): takes item_offset_flag and its checker.</summary>
        public static void BeginFrame()
        {
#if CUO_DEVTOOLS
            _checkFrame = CheckOverrideForTests ?? Check;
            _flagFrame = Enabled || _checkFrame;
#else
            _flagFrame = Enabled;
#endif
        }

#if CUO_DEVTOOLS
        private static long _reads, _unflagged, _mismatches, _at;
        private static string _sample;

        /// <summary>Tests: mismatches since start.</summary>
        internal static long Mismatches => _mismatches;

        /// <summary>Tests: back to the start state.</summary>
        internal static void ResetForTests()
        {
            OverrideForTests = null;
            CheckOverrideForTests = null;
            _reads = _unflagged = _mismatches = _at = 0;
            _sample = null;
            BeginFrame();
        }

        // CUO_ITEM_OFFSET_FLAG_CHECK: both paths on the same item; returns the switch's answer (the count path's with the
        // switch off)
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector3 ReadChecked(Item item)
        {
            Vector3 counted = ReadCounted(item);
            Vector3 flagged = item.ItemOffsetHeld ? ReadHeld(item) : default;
            Judge(item, counted, flagged, "read");

            return Enabled ? flagged : counted;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector3 ReadRowChecked(Item item, bool record, in ClassicUO.Ecs.EcsRow row)
        {
            Vector3 old = ReadRow(record, in row);
            Vector3 flagged = item.ItemOffsetHeld ? old : default;
            Judge(item, old, flagged, "row");

            return Enabled ? flagged : old;
        }

        private static void Judge(Item item, Vector3 old, Vector3 flagged, string site)
        {
            _reads++;

            if (!item.ItemOffsetHeld)
            {
                _unflagged++;
            }

            if (old != flagged)
            {
                _mismatches++;
                string s = $"{site}: item 0x{item.Serial:X8} at {item.X},{item.Y},{item.Z}: count path {old}, flag {item.ItemOffsetHeld} -> {flagged}";
                _sample ??= s;
                Log.Warn($"[item-offset-flag-check] mismatch: {s}");
            }

            long now = Stopwatch.GetTimestamp();

            if (_at == 0)
            {
                _at = now;
            }
            else if (now - _at >= Stopwatch.Frequency * 5)
            {
                Log.Info($"[item-offset-flag-check] switch {(Enabled ? "on" : "off")}: {_reads} reads, {_unflagged} without the flag, " +
                         $"{_mismatches} mismatches" + (_sample != null ? $"; first mismatch: {_sample}" : ""));
                _at = now;
                _reads = _unflagged = 0;
            }
        }
#endif
    }
}
