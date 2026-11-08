// SPDX-License-Identifier: BSD-2-Clause

#if CUO_DEVTOOLS
using System;
using System.Collections.Generic;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.GameObjects
{
    /// <summary>
    /// CLEANUP(release): CUO_STEP_OPEN_CHECK=1 (perf-13) runs the every-item loops <see cref="PlayerMobile"/>'s auto-open rules
    /// replaced beside the new lookups and logs every difference as <c>[step-open-check]</c>: the door ahead of a step or turn
    /// (the old LINQ rule over Item.ItemData, against the tile lookup) and the corpses a position change opens (the old loop's
    /// serials in order, against the active-list count). A door the loop finds only off the ground (an item in a container or
    /// worn whose X/Y equal the tile) is the documented difference and counted apart. 5 s summary while the rules run.
    /// </summary>
    internal static class StepOpenCheck
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("CUO_STEP_OPEN_CHECK") == "1";

        public static long DoorChecks, DoorHits, DoorDiffs, DoorOffGround;
        public static long CorpseChecks, CorpsesFound, CorpseLoops, CorpseDiffs;
        private static readonly List<uint> _legacy = new List<uint>();
        private static uint _logAt;
        private static int _logged;

        public static void Door(World world, int x, int y, int z, bool door)
        {
            DoorChecks++;

            if (door)
            {
                DoorHits++;
            }

            bool legacy = false, onGround = false;

            foreach (Item item in world.Items.Values)
            {
                if (item.ItemData.IsDoor && item.X == x && item.Y == y && item.Z - 15 <= z && item.Z + 15 >= z)
                {
                    legacy = true;
                    onGround |= item.OnGround;
                }
            }

            if (legacy != door)
            {
                if (legacy && !onGround)
                {
                    DoorOffGround++;
                }
                else
                {
                    DoorDiffs++;
                    Warn($"door at {x},{y},{z}: tile lookup {door}, item loop {legacy}");
                }
            }

            MaybeSummary();
        }

        public static void Corpses(PlayerMobile player, int range, int found, Item single)
        {
            CorpseChecks++;
            _legacy.Clear();

            foreach (Item item in player.World.Items.Values)
            {
                if (player.ShouldAutoOpen(item, range))
                {
                    _legacy.Add(item.Serial);
                }
            }

            CorpsesFound += _legacy.Count;

            if (!player.World.ActiveItemsListed)
            {
                CorpseLoops++;
            }
            else if (found == 0 && _legacy.Count != 0 || found == 1 && (_legacy.Count != 1 || _legacy[0] != single.Serial) || found > 1 && _legacy.Count < 2)
            {
                CorpseDiffs++;
                Warn($"corpses in range {range}: active list found {found} (0x{single?.Serial ?? 0:X8}), item loop {_legacy.Count} (first 0x{(_legacy.Count > 0 ? _legacy[0] : 0):X8})");
            }
            else if (found > 1)
            {
                CorpseLoops++;
            }

            MaybeSummary();
        }

        public static string Summary() =>
            $"[step-open-check] doors checked {DoorChecks}, ahead {DoorHits}, {DoorDiffs} different, {DoorOffGround} off the ground; corpse checks {CorpseChecks}, corpses to open {CorpsesFound}, item loops {CorpseLoops}, {CorpseDiffs} different";

        private static void Warn(string text)
        {
            if (_logged++ < 50)
            {
                Log.Warn($"[step-open-check] {text}");
            }
        }

        private static void MaybeSummary()
        {
            if (Time.Ticks >= _logAt)
            {
                _logAt = Time.Ticks + 5000;
                Log.Info(Summary());
            }
        }
    }
}
#endif
