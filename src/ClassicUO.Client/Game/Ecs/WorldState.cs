// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Text;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

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
#if CUO_DEVTOOLS
            CheckEnabled = ContentsCheckFromEnv;
#endif
        }

        /// <summary>
        /// T2: the serials of the parent's attached children (worn or contained) in child order: arrival order, a re-push to
        /// the back, as ClassicUO's <c>LinkedObject</c> list. Orphans (a child that arrived before its parent) and the
        /// spellbook's detached spell entries (<see cref="Spellbook"/>) are not in it. Valid until the parent's contents
        /// change; no allocation. With <c>CUO_CONTENTS_CHECK=1</c> every read is compared with the child list.
        /// </summary>
        public ReadOnlySpan<uint> Contents(uint parent)
        {
            ReadOnlySpan<uint> contents = _world.Ecs.Mirror.Contents(parent);
#if CUO_DEVTOOLS
            if (CheckEnabled)
            {
                ContentsReads++;
                Compare(parent, contents, "read");
            }
#endif

            return contents;
        }

        /// <summary>
        /// T2: the spell set of a spellbook's 0xBF.1B content (bit i = spell i + 1; 0 for none). ClassicUO keeps it as
        /// detached entries (serial and amount i + 1, graphic 0x1F2E) at the front of the book's child list.
        /// </summary>
        public ulong Spellbook(uint book) => _world.Ecs.Mirror.Spellbook(book);

        // ---- CUO_CONTENTS_CHECK (CUO_DEVTOOLS builds only): at every Contents read, and every frame for each open container
        // gump, the ECS enumeration (the spell entries, then Contents) must equal the World child list: the same serials in
        // the same order. CLEANUP(release): goes with the container half of the child list (T9).
#if CUO_DEVTOOLS
        public static readonly bool ContentsCheckFromEnv = Environment.GetEnvironmentVariable("CUO_CONTENTS_CHECK") == "1";

        private const int MAX_LOGGED_MISMATCHES = 25;

        /// <summary>The checker is on (from <c>CUO_CONTENTS_CHECK=1</c>; tests set it per World).</summary>
        public bool CheckEnabled;

        /// <summary>Checker counts: reads compared, per-frame gump comparisons, mismatches found.</summary>
        public long ContentsReads, GumpChecks, Mismatches;

        /// <summary>Tests: sees each mismatch report.</summary>
        internal Action<string> MismatchObserver;

        private readonly List<uint> _list = new List<uint>();
        private readonly List<uint> _ecs = new List<uint>();
        private uint _logAt;
        private long _loggedReads = -1, _loggedGumps = -1;

        /// <summary>Compares one parent's ECS enumeration with its World child list (no-op unless <see cref="CheckEnabled"/>).</summary>
        public void CheckParent(uint parent, string where)
        {
            if (CheckEnabled)
            {
                Compare(parent, _world.Ecs.Mirror.Contents(parent), where);
            }
        }

        private void Compare(uint parent, ReadOnlySpan<uint> contents, string where)
        {
            _list.Clear();
            _ecs.Clear();

            Entity e = _world.Get(parent);

            for (LinkedObject i = e?.Items; i != null; i = i.Next)
            {
                _list.Add(i is Entity child ? child.Serial : 0);
            }

            ulong spells = _world.Ecs.Mirror.Spellbook(parent);

            for (int b = 0; b < 64; b++)
            {
                if ((spells & (1UL << b)) != 0)
                {
                    _ecs.Add((uint)(b + 1));
                }
            }

            foreach (uint s in contents)
            {
                _ecs.Add(s);
            }

            bool same = _list.Count == _ecs.Count;

            for (int i = 0; same && i < _list.Count; i++)
            {
                same = _list[i] == _ecs[i];
            }

            if (same)
            {
                return;
            }

            Mismatches++;
            var sb = new StringBuilder();
            sb.Append($"[contents] mismatch ({where}) children of 0x{parent:X8}: World");

            foreach (uint s in _list)
            {
                sb.Append($" 0x{s:X8}");
            }

            sb.Append(" | ECS");

            foreach (uint s in _ecs)
            {
                sb.Append($" 0x{s:X8}");
            }

            string message = sb.ToString();
            MismatchObserver?.Invoke(message);

            if (Mismatches <= MAX_LOGGED_MISMATCHES)
            {
                Log.Warn(message);
            }
        }

        /// <summary>
        /// Every frame (World.Update, through WorldEcs.Update): each open container gump's parent (ContainerGump,
        /// GridLootGump, SpellbookGump, both sides of a TradingGump), then a summary every 5 s while the counts change.
        /// </summary>
        public void CheckOpenContainers()
        {
            if (!CheckEnabled)
            {
                return;
            }

            for (LinkedListNode<UI.Gumps.Gump> n = Managers.UIManager.Gumps.First; n != null; n = n.Next)
            {
                UI.Gumps.Gump g = n.Value;

                if (g.IsDisposed)
                {
                    continue;
                }

                switch (g)
                {
                    case UI.Gumps.ContainerGump:
                    case UI.Gumps.GridLootGump:
                    case UI.Gumps.SpellbookGump:
                        GumpChecks++;
                        CheckParent(g.LocalSerial, g.GetType().Name);

                        break;

                    case UI.Gumps.TradingGump t:
                        GumpChecks += 2;
                        CheckParent(t.ID1, "TradingGump");
                        CheckParent(t.ID2, "TradingGump");

                        break;
                }
            }

            if (Time.Ticks >= _logAt && (ContentsReads != _loggedReads || GumpChecks != _loggedGumps))
            {
                _logAt = Time.Ticks + 5000;
                _loggedReads = ContentsReads;
                _loggedGumps = GumpChecks;
                LogSummary();
            }
        }

        public void LogSummary()
        {
            ClassicUO.Ecs.WorldMirror m = _world.Ecs.Mirror;
            Log.Info($"[contents] checks reads={ContentsReads} gumps={GumpChecks} mismatches={Mismatches} | read_contents={(ReadContents ? "on" : "off")} cached parents={m.CachedContentsParents} builds={m.ContentsBuilds} invalidations={m.ContentsInvalidations} cache bytes={m.ContentsCacheBytes}");
        }
#else
        public const bool CheckEnabled = false;

        public void CheckParent(uint parent, string where) { }

        public void CheckOpenContainers() { }
#endif

        /// <summary>T2 (<c>ecs_read_contents</c>): the readers take container contents from the ECS. Read in step 3.</summary>
        public bool ReadContents => false;
    }
}
