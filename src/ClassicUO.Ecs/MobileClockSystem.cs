// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace ClassicUO.Ecs
{
    /// <summary>
    /// E4: picks, from the <see cref="MobileClock"/> components alone, the mobiles whose per-frame update has work this
    /// frame. Read-only over the store (the caller then updates the legacy Mobiles by serial, which may delete entities,
    /// so the scan and the updates are separate phases). Zero allocation after warm-up.
    /// </summary>
    public sealed class MobileClockSystem
    {
        private readonly ArchetypeQuery _query;

        public MobileClockSystem(EntityStore store)
        {
            _query = store.Query().AllComponents(ComponentTypes.Get<Serial, MobileClock>()).AllTags(Tags.Get<MobileTag>());
        }

        /// <summary>Mobile entities seen by the last <see cref="CollectDue"/>.</summary>
        public int Scanned { get; private set; }

        /// <summary>Appends the serials of the mobiles due at <paramref name="now"/> to <paramref name="due"/>.</summary>
        public void CollectDue(long now, List<uint> due)
        {
            int scanned = 0;

            foreach (Archetype arch in _query.Archetypes)
            {
                int n = arch.Count;

                if (n == 0)
                {
                    continue;
                }

                ReadOnlySpan<MobileClock> clocks = arch.Components<MobileClock>();
                ReadOnlySpan<Serial> serials = arch.Components<Serial>();
                scanned += n;

                for (int i = 0; i < n; i++)
                {
                    if (MobileClockRules.IsDue(in clocks[i], now))
                    {
                        due.Add(serials[i].Value);
                    }
                }
            }

            Scanned = scanned;
        }
    }
}
