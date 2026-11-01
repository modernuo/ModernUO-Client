// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.Game
{
    /// <summary>
    /// T2 (ecs-transition-plan.md): the world state the ECS owns, read by the client. The first piece is container contents:
    /// <see cref="Contents"/> is a parent's attached children in child order, from the mirror's per-parent cached span.
    /// </summary>
    internal sealed class WorldState
    {
        private readonly World _world;

        public WorldState(World world)
        {
            _world = world;
        }

        /// <summary>
        /// T2: the serials of the parent's attached children (worn or contained, in no particular kind order) in child order:
        /// arrival order, a re-push to the back, as ClassicUO's <c>LinkedObject</c> list. Orphans (a child that arrived
        /// before its parent) and the spellbook's detached spell entries are not in it. Valid until the parent's contents
        /// change; no allocation.
        /// </summary>
        public ReadOnlySpan<uint> Contents(uint parent) => _world.Ecs.Mirror.Contents(parent);
    }
}
