// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game
{
    /// <summary>
    /// The turn prefetch (gpu-popin round 5; owner 2026-11-20: a brief turn delay on one mobile is preferred to a whole-frame
    /// stall). A turn or new action of a body already drawn decodes on a worker (Animations.LoadDirection), and the draw keeps
    /// the mobile's previous look meanwhile (MobileSpriteExtract's hold, at most <see cref="LookPrefetch.HoldMaxMs"/>); the
    /// upload's asset signal presents the new look (asset_ready_wake). So that most turns are already resident, the
    /// WorldWriter operations that move, turn or change the war mode of a mobile note it here, and the next Update requests
    /// the direction it will face (the end of its queued steps) and the two next to it, in the group it will draw (walk or
    /// run while moving, the war stance in war mode), for body, mount and equipment. Bounded like <see cref="LookPrefetch"/>: at most
    /// <see cref="Capacity"/> notes waiting (the oldest dropped), <see cref="BudgetPerUpdate"/> resolved an Update, mobiles
    /// beyond the view range skipped. Main thread; no allocation. One queue per <see cref="World"/> (World.AsyncTurn): a
    /// process-wide queue let one world's notes reach another world's drain (the unit suite's parallel worlds).
    /// </summary>
    internal sealed class AnimAsyncTurn
    {
        public const int Capacity = 128;
        public const int BudgetPerUpdate = 16;

        private readonly World _world;
        private readonly uint[] _serials = new uint[Capacity];
        private int _head, _count;

        /// <summary>Notes dropped because the queue was full, and mobiles resolved (totals).</summary>
        public long Dropped, Resolved;

        public int Count => _count;

        public AnimAsyncTurn(World world)
        {
            _world = world;
        }

        /// <summary>WorldWriter: <paramref name="serial"/> moved, turned or changed its war mode (main thread).</summary>
        public void Note(uint serial)
        {
            if (serial == 0)
            {
                return;
            }

            if (_count == Capacity)
            {
                _head = (_head + 1) % Capacity;
                _count--;
                Dropped++;
            }

            _serials[(_head + _count) % Capacity] = serial;
            _count++;
        }

        internal bool TryTake(out uint serial)
        {
            if (_count == 0)
            {
                serial = 0;

                return false;
            }

            serial = _serials[_head];
            _head = (_head + 1) % Capacity;
            _count--;

            return true;
        }

        /// <summary>The UO directions requested for a mobile that will face <paramref name="uoDir"/>: that one first, then
        /// its two neighbours (the likely next turn).</summary>
        internal static void Directions(byte uoDir, Span<byte> dirs)
        {
            dirs[0] = (byte)(uoDir & 7);
            dirs[1] = (byte)((uoDir + 1) & 7);
            dirs[2] = (byte)((uoDir + 7) & 7);
        }

        /// <summary>Each Update, before the draw.</summary>
        public void Drain()
        {
            World world = _world;

            if (_count == 0)
            {
                return;
            }

            if (world?.Player == null)
            {
                _head = _count = 0;

                return;
            }

            int range = world.ClientViewRange;
            Span<byte> dirs = stackalloc byte[3];
            bool gpu = Client.Game.GetGpuWorld()?.World != null;

            for (int n = 0; n < BudgetPerUpdate && TryTake(out uint serial); n++)
            {
                if (world.Get(serial) is not Mobile mobile || mobile.IsDestroyed || mobile.Distance > range)
                {
                    continue;
                }

                Resolved++;
                Direction facing = mobile.Direction;

                if (mobile.Steps.Count != 0)
                {
                    mobile.GetEndPosition(out _, out _, out _, out facing);
                }

                Directions((byte)((byte)facing & 7), dirs);
                ushort graphic = mobile.GetGraphicForAnimation();
                byte group = Mobile.GetGroupForAnimation(mobile, graphic, true);

                // the Gpu world's anim prefetch already queues the action's eight directions, the facing one first
                for (int d = 0; d < (gpu ? 1 : dirs.Length); d++)
                {
                    LookPrefetch.PrefetchMobile(mobile, dirs[d], group);
                }
            }
        }
    }
}
