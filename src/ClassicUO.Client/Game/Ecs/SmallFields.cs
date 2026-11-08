// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    internal enum SmallField : byte
    {
        LightID,
        IsDamageable,
        UsedLayer,
        Race,
        SpeedMode,
        IsFemale,
        IsRenamable,
        InWarMode
    }

    /// <summary>
    /// <c>ecs_small_fields</c> (wave 1C): the small cold fields are ECS-owned. WorldWriter's operations commit them to the
    /// record's <see cref="ItemTraits"/> (Item <c>LightID</c>, <c>IsDamageable</c>, <c>UsedLayer</c>) and
    /// <see cref="MobileTraits"/> (Mobile <c>Race</c>, <c>SpeedMode</c>, <c>IsFemale</c>, <c>IsRenamable</c>, the player's
    /// <c>InWarMode</c>) before their cache writes, whatever the switch; with the switch on the getters read the record. An
    /// instance without a held record (a preview, the detached player, a stale or destroyed instance) reads its cache, as
    /// with the switch off. CUO_SMALL_FIELDS_CHECK=1 routes every getter through here with either switch state and compares
    /// the record with the cache at every read.
    /// </summary>
    internal static class SmallFields
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): CUO_SMALL_FIELDS_CHECK=1, the record against the cache at every read
        public static readonly bool EnvCheck = Environment.GetEnvironmentVariable("CUO_SMALL_FIELDS_CHECK") == "1";
#else
        public static readonly bool EnvCheck = false;
#endif

        // tests set World.SmallFieldsCheckOverride (per World: other test classes' Worlds run in parallel)
        public static bool Check(World world) => world.SmallFieldsCheckOverride ?? EnvCheck;

        public static long Reads, Fallbacks, Checks, Diffs;
        private static int _logged;
        private static uint _logAt;

        /// <summary>Tests: the counters back to zero.</summary>
        internal static void ResetCounters()
        {
            Reads = Fallbacks = Checks = Diffs = 0;
            _logged = 0;
            _logAt = 0;
        }

        /// <summary>Whether a getter asks here: the switch is on, or the checker runs.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Routed(World world) => world != null && (world.SmallFieldsOn || Check(world));

        internal static byte LightId(Item item)
        {
            byte cache = item.CachedLightId;

            if (!Record(item, out EcsEntity e))
            {
                return cache;
            }

            byte ecs = EcsRead.TryGet(e, out ItemTraits t) ? t.LightId : (byte)0;

            return (byte)Pick(item, SmallField.LightID, ecs, cache);
        }

        internal static bool ItemBit(Item item, SmallField field, ItemTraitBits bit, bool cache)
        {
            if (!Record(item, out EcsEntity e))
            {
                return cache;
            }

            bool ecs = EcsRead.TryGet(e, out ItemTraits t) && (t.Bits & bit) != 0;

            return Pick(item, field, ecs ? 1 : 0, cache ? 1 : 0) != 0;
        }

        internal static RaceType Race(Mobile mobile)
        {
            RaceType cache = mobile.CachedRace;

            if (!Record(mobile, out EcsEntity e))
            {
                return cache;
            }

            byte ecs = EcsRead.TryGet(e, out MobileTraits t) ? t.Race : (byte)0;

            return (RaceType)Pick(mobile, SmallField.Race, ecs, (byte)cache);
        }

        internal static CharacterSpeedType SpeedMode(Mobile mobile)
        {
            CharacterSpeedType cache = mobile.CachedSpeedMode;

            if (!Record(mobile, out EcsEntity e))
            {
                return cache;
            }

            byte ecs = EcsRead.TryGet(e, out MobileTraits t) ? t.SpeedMode : (byte)0;

            return (CharacterSpeedType)Pick(mobile, SmallField.SpeedMode, ecs, (int)cache);
        }

        internal static bool MobileBit(Mobile mobile, SmallField field, MobileTraitBits bit, bool cache)
        {
            if (!Record(mobile, out EcsEntity e))
            {
                return cache;
            }

            bool ecs = EcsRead.TryGet(e, out MobileTraits t) && (t.Bits & bit) != 0;

            return Pick(mobile, field, ecs ? 1 : 0, cache ? 1 : 0) != 0;
        }

        private static bool Record(Entity entity, out EcsEntity e)
        {
            if (entity.TryScalarEntity(out e))
            {
                return true;
            }

            Fallbacks++;

            return false;
        }

        private static int Pick(Entity entity, SmallField field, int ecs, int cache)
        {
            Reads++;

            if (Check(entity.World))
            {
                Compare(entity, field, ecs, cache);
            }

            return entity.World.SmallFieldsOn ? ecs : cache;
        }

        private static void Compare(Entity entity, SmallField field, int ecs, int cache)
        {
            Checks++;

            if (ecs == cache)
            {
                return;
            }

            Diffs++;

            if (_logged++ < 10)
            {
                Log.Warn($"[small-fields-check] 0x{entity.Serial:X8} g=0x{entity.Graphic:X4} {field}: ecs {ecs} | cache {cache} (tick {Time.Ticks})");
            }
        }

        /// <summary>The counters, for the periodic log line.</summary>
        internal static string Summary(World world) =>
            $"[small-fields-check] ecs_small_fields {(world.SmallFieldsOn ? "on" : "off")}: record reads {Reads}, fallbacks to the cache {Fallbacks}; checked {Checks}, different {Diffs}";

        /// <summary>Every 5 s while the checker is on: <see cref="Summary"/> (WorldEcs.Update, once per frame).</summary>
        internal static void LogSummary(World world)
        {
            if (!Check(world) || Time.Ticks < _logAt)
            {
                return;
            }

            _logAt = Time.Ticks + 5000;
            Log.Info(Summary(world));
        }
    }
}
