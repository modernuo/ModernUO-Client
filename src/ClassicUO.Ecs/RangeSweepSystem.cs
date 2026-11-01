// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace ClassicUO.Ecs
{
    /// <summary>The range rule of <c>GameObject.Distance</c> in one place: Chebyshev distance from the range centre.</summary>
    public static class RangeSweepRules
    {
        public static bool Beyond(int x, int y, int cx, int cy, int range)
        {
            return Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)) > range;
        }
    }

    /// <summary>
    /// T5c: the 50 ms range sweep's selection, over the <see cref="Position"/> components alone. Read-only over the store: the
    /// caller removes through the funnel afterwards (a removal destroys entities, so scan and removals are separate phases).
    /// A mobile with queued steps is not decided here (<c>GameObject.Distance</c> uses the last step's tile, which the ECS does
    /// not hold): it is appended to <c>stepping</c> and the caller applies <see cref="RangeSweepRules.Beyond"/> to that tile.
    /// The lists follow archetype order; the caller puts them into table order. Zero allocation after warm-up.
    /// </summary>
    public sealed class RangeSweepSystem
    {
        private readonly ArchetypeQuery _mobiles;
        private readonly ArchetypeQuery _items;
        private readonly ArchetypeQuery _inputs;
        private readonly ArchetypeQuery _wornItems;
        private readonly ArchetypeQuery _containedItems;

        public RangeSweepSystem(EntityStore store)
        {
            _mobiles = store.Query().AllComponents(ComponentTypes.Get<Serial, Position, MobileClock>()).AllTags(Tags.Get<MobileTag>());
            _items = store.Query().AllComponents(ComponentTypes.Get<Serial, Position>()).AllTags(Tags.Get<ItemTag, OnGround>());
            // Item.OnGround is "the container serial is not valid": a dying mobile's equipment is re-keyed to 0x80000000 | serial
            // and stays EquippedOn without the OnGround tag, but the legacy sweep still treats it as a ground item
            _wornItems = store.Query().AllComponents(ComponentTypes.Get<Serial, Position, EquippedOn>()).AllTags(Tags.Get<ItemTag>());
            _containedItems = store.Query().AllComponents(ComponentTypes.Get<Serial, Position, ContainedIn>()).AllTags(Tags.Get<ItemTag>());
            _inputs = store.Query().AllComponents(ComponentTypes.Get<Serial, MobileInputs>()).AllTags(Tags.Get<MobileTag>());
        }

        /// <summary>ClassicUO's <c>SerialHelper.IsValid</c>: a container serial that names a real mobile or item. Anything else (0, or the 0x80000000 | serial of a dying mobile) puts the item on the ground.</summary>
        private static bool IsValidSerial(uint serial) => serial > 0 && serial < 0x80000000;

        /// <summary>Entities seen by the last collect.</summary>
        public int Scanned { get; private set; }

        /// <summary>
        /// Mobiles beyond <paramref name="range"/> of (<paramref name="cx"/>, <paramref name="cy"/>) by their position go to
        /// <paramref name="beyond"/>; mobiles with queued steps (<see cref="MobileClockFlags.Stepping"/>) go to
        /// <paramref name="stepping"/> whatever their position. <paramref name="player"/> is never selected.
        /// </summary>
        public void CollectMobiles(int cx, int cy, int range, uint player, List<uint> beyond, List<uint> stepping)
        {
            int scanned = 0;

            foreach (Archetype arch in _mobiles.Archetypes)
            {
                int n = arch.Count;

                if (n == 0)
                {
                    continue;
                }

                ReadOnlySpan<Serial> serials = arch.Components<Serial>();
                ReadOnlySpan<Position> positions = arch.Components<Position>();
                ReadOnlySpan<MobileClock> clocks = arch.Components<MobileClock>();
                scanned += n;

                for (int i = 0; i < n; i++)
                {
                    uint serial = serials[i].Value;

                    if (serial == player)
                    {
                        continue;
                    }

                    if ((clocks[i].Flags & MobileClockFlags.Stepping) != 0)
                    {
                        stepping.Add(serial);
                    }
                    else if (RangeSweepRules.Beyond(positions[i].X, positions[i].Y, cx, cy, range))
                    {
                        beyond.Add(serial);
                    }
                }
            }

            Scanned = scanned;
        }

        /// <summary>Ground items (legacy <c>OnGround</c>: no valid container serial) beyond <paramref name="range"/> by their position go to <paramref name="beyond"/>.</summary>
        public void CollectItems(int cx, int cy, int range, List<uint> beyond)
        {
            int scanned = 0;

            foreach (Archetype arch in _items.Archetypes)
            {
                int n = arch.Count;

                if (n == 0)
                {
                    continue;
                }

                ReadOnlySpan<Serial> serials = arch.Components<Serial>();
                ReadOnlySpan<Position> positions = arch.Components<Position>();
                scanned += n;

                for (int i = 0; i < n; i++)
                {
                    if (RangeSweepRules.Beyond(positions[i].X, positions[i].Y, cx, cy, range))
                    {
                        beyond.Add(serials[i].Value);
                    }
                }
            }

            foreach (Archetype arch in _wornItems.Archetypes)
            {
                int n = arch.Count;

                if (n == 0)
                {
                    continue;
                }

                ReadOnlySpan<Serial> serials = arch.Components<Serial>();
                ReadOnlySpan<Position> positions = arch.Components<Position>();
                ReadOnlySpan<EquippedOn> parents = arch.Components<EquippedOn>();

                for (int i = 0; i < n; i++)
                {
                    if (!IsValidSerial(parents[i].Parent) && !arch.Tags.Has<OnGround>() && RangeSweepRules.Beyond(positions[i].X, positions[i].Y, cx, cy, range))
                    {
                        beyond.Add(serials[i].Value);
                    }
                }
            }

            foreach (Archetype arch in _containedItems.Archetypes)
            {
                int n = arch.Count;

                if (n == 0)
                {
                    continue;
                }

                ReadOnlySpan<Serial> serials = arch.Components<Serial>();
                ReadOnlySpan<Position> positions = arch.Components<Position>();
                ReadOnlySpan<ContainedIn> parents = arch.Components<ContainedIn>();

                for (int i = 0; i < n; i++)
                {
                    if (!IsValidSerial(parents[i].Parent) && !arch.Tags.Has<OnGround>() && RangeSweepRules.Beyond(positions[i].X, positions[i].Y, cx, cy, range))
                    {
                        beyond.Add(serials[i].Value);
                    }
                }
            }

            Scanned = scanned;
        }

        /// <summary>The world-map pass's first half: mobiles whose pushed notoriety is <paramref name="ally"/>.</summary>
        public void CollectAllies(byte ally, List<uint> allies)
        {
            foreach (Archetype arch in _inputs.Archetypes)
            {
                int n = arch.Count;

                if (n == 0)
                {
                    continue;
                }

                ReadOnlySpan<Serial> serials = arch.Components<Serial>();
                ReadOnlySpan<MobileInputs> inputs = arch.Components<MobileInputs>();

                for (int i = 0; i < n; i++)
                {
                    if (inputs[i].Notoriety == ally)
                    {
                        allies.Add(serials[i].Value);
                    }
                }
            }
        }
    }
}
