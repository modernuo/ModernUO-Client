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
                Compare(_world.Get(parent), SpellEntries(parent), contents, "read", parent);
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
        public const bool CheckAvailable = true;

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
                Compare(_world.Get(parent), SpellEntries(parent), _world.Ecs.Mirror.Contents(parent), where, parent);
            }
        }

        // the ECS enumeration Children returns with the switch on (the spell entries, then the contents) against the parent's
        // World child list; the spell entries must also be the mirror's spell set (bit i: serial and amount i + 1)
        private void Compare(Entity e, ReadOnlySpan<Item> spellEntries, ReadOnlySpan<uint> contents, string where, uint parent = 0)
        {
            _list.Clear();
            _ecs.Clear();
            parent = e?.Serial ?? parent;

            for (LinkedObject i = e?.Items; i != null; i = i.Next)
            {
                _list.Add(i is Entity child ? child.Serial : 0);
            }

            ulong spells = _world.Ecs.Mirror.Spellbook(parent);
            int bit = 0;
            int entries = 0;

            foreach (Item entry in spellEntries)
            {
                while (bit < 64 && (spells & (1UL << bit)) == 0)
                {
                    bit++;
                }

                // an entry that is not the set's next spell shows as serial 0 (a mismatch)
                _ecs.Add(bit < 64 && entry.Serial == (uint)(bit + 1) && entry.Amount == bit + 1 ? entry.Serial : 0);
                bit++;
                entries++;
            }

            // the set's spells without an entry (FillSpellbook's hand-over missing) are a mismatch
            if (entries != System.Numerics.BitOperations.PopCount(spells))
            {
                _ecs.Add(0);
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
            Log.Info($"[contents] checks reads={ContentsReads} gumps={GumpChecks} mismatches={Mismatches} | read_contents={(ReadContents ? "on" : "off")} cached parents={m.CachedContentsParents} builds={m.ContentsBuilds} invalidations={m.ContentsInvalidations} cache bytes={m.ContentsCacheBytes} | spell books={SpellEntryBooks} bytes={SpellEntriesBytes}");
        }
#else
        // release builds: no checker (the tests skip what needs it)
        public const bool CheckAvailable = false;

        public bool CheckEnabled
        {
            get => false;
            set { }
        }

        public long ContentsReads => 0;

        public long GumpChecks => 0;

        public long Mismatches => 0;

        internal Action<string> MismatchObserver
        {
            get => null;
            set { }
        }

        public void CheckParent(uint parent, string where) { }

        public void CheckOpenContainers() { }
#endif

        // ---- T2 readers (ecs_read_contents): the child-list readers (container, grid loot, trade and spellbook gumps, the
        // shop's lists, FindItem*, the counters, macros) walk Children(parent), which is the ECS enumeration when the switch
        // is on and the World child list when it is off. The child list is still maintained (dual) until T9.

        /// <summary>Tests: the switch for this World (null: the setting).</summary>
        internal bool? ReadContentsOverride;

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_ECS_READ_CONTENTS=1|0)
        private static readonly bool? _envReadContents = Environment.GetEnvironmentVariable("CUO_ECS_READ_CONTENTS") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
#else
        private static readonly bool? _envReadContents = null;
#endif

        /// <summary>T2 (<c>ecs_read_contents</c>, default off): the readers take container contents from the ECS.</summary>
        public bool ReadContents => ReadContentsOverride ?? _envReadContents ?? Configuration.Settings.GlobalSettings.EcsReadContents;

        /// <summary>
        /// T2: the parent's children, in child order: with <see cref="ReadContents"/> the ECS's (a spellbook's spell entries,
        /// then <see cref="Contents"/>), else the World child list. Under <c>CUO_CONTENTS_CHECK</c> each call compares the
        /// two. A reader must not change the parent's contents while it walks them (no reader does).
        /// </summary>
        public ChildList Children(Entity parent)
        {
            if (parent == null)
            {
                return default;
            }

            // Codex audit P1: the ECS keys contents by serial, so an instance that is not the entity World holds for its
            // serial (a spellbook's detached spell entry, serial 1-64, which can be a real mobile's; a destroyed or replaced
            // object) must not read that entity's contents: with the switch on it is a leaf, with it off its own list
            // (the legacy read; a spell entry's is empty), unchecked
            if (!IsCurrent(parent))
            {
                return ReadContents ? default : new ChildList(parent.Items);
            }

            if (ReadContents)
            {
                ReadOnlySpan<uint> contents = _world.Ecs.Mirror.Contents(parent.Serial);
                ReadOnlySpan<Item> spells = SpellEntries(parent.Serial);
#if CUO_DEVTOOLS
                if (CheckEnabled)
                {
                    ContentsReads++;
                    Compare(parent, spells, contents, "read");
                }
#endif

                return new ChildList(_world, spells, contents);
            }
#if CUO_DEVTOOLS
            if (CheckEnabled)
            {
                ContentsReads++;
                Compare(parent, SpellEntries(parent.Serial), _world.Ecs.Mirror.Contents(parent.Serial), "read");
            }
#endif

            return new ChildList(parent.Items);
        }

        // the instance World holds for the entity's serial (the one the ECS's record under that serial stands for)
        private bool IsCurrent(Entity e) =>
            e is Mobile ? ReferenceEquals(_world.Mobiles.Get(e.Serial), e) : ReferenceEquals(_world.Items.Get(e.Serial), e);

        /// <summary>T2: <c>!parent.IsEmpty</c>, from the ECS when <see cref="ReadContents"/> is on.</summary>
        public bool HasChildren(Entity parent) => !Children(parent).IsEmpty;

        // ---- the spellbook's entries: 0xBF.1B's spells as the detached items FillSpellbook creates (graphic 0x1F2E, serial and
        // amount = the spell number). The spell set is ECS state (SpellbookContent); the entries are its materialisation,
        // handed over by FillSpellbook with the set they stand for, and valid only while the mirror's set is that set (a
        // removed book, a clear, a refill: stale, never returned).

        private sealed class SpellEntrySet
        {
            public ulong Spells;
            public Item[] Entries = Array.Empty<Item>();
            public int Count;
        }

        private readonly Dictionary<uint, SpellEntrySet> _spellEntries = new Dictionary<uint, SpellEntrySet>();

        /// <summary>FillSpellbook: the entries it created for <paramref name="spells"/>, in list order.</summary>
        internal void SetSpellEntries(uint book, ulong spells, List<Item> entries)
        {
            // an empty set holds nothing (its entries went with the set's commit)
            if (entries.Count == 0)
            {
                DropSpellEntries(book);

                return;
            }

            if (!_spellEntries.TryGetValue(book, out SpellEntrySet set))
            {
                set = new SpellEntrySet();
                _spellEntries.Add(book, set);
            }

            if (set.Entries.Length < entries.Count)
            {
                set.Entries = new Item[64];
            }

            entries.CopyTo(set.Entries);
            Array.Clear(set.Entries, entries.Count, set.Entries.Length - entries.Count);
            set.Count = entries.Count;
            set.Spells = spells;
        }

        /// <summary>The book's spell entries, while the mirror's spell set is the one they were made for (else none).</summary>
        public ReadOnlySpan<Item> SpellEntries(uint book)
        {
            if (_spellEntries.Count == 0 || !_spellEntries.TryGetValue(book, out SpellEntrySet set))
            {
                return default;
            }

            ulong spells = _world.Ecs.Mirror.Spellbook(book);

            if (spells == 0 || spells != set.Spells)
            {
                // stale (the book was cleared, removed or refilled without its entries)
                _spellEntries.Remove(book);

                return default;
            }

            return new ReadOnlySpan<Item>(set.Entries, 0, set.Count);
        }

        /// <summary>
        /// Codex audit P2 (#3): the book's record went (destroyed: a removal, the bank's, a map change's) or its set changed
        /// (a clear, a refill): its entries go now, not at the next read (a removed book is never read again).
        /// </summary>
        internal void DropSpellEntries(uint book)
        {
            if (_spellEntries.Count != 0)
            {
                _spellEntries.Remove(book);
            }
        }

        /// <summary>Tests and the memory log: the books whose entries are held.</summary>
        public int SpellEntryBooks => _spellEntries.Count;

        /// <summary>
        /// The memory log: the bytes the spell-entry storage holds, by the 64-bit layout (the dictionary object 80 B, its
        /// buckets 4 B and entries 24 B per slot of capacity, each set 40 B with its Item[] 24 B + 8 B per slot). The entries'
        /// Item objects are not counted: the book's child list holds the same objects until T9.
        /// </summary>
        public long SpellEntriesBytes
        {
            get
            {
                int capacity = _spellEntries.EnsureCapacity(0);
                long bytes = 80 + (capacity == 0 ? 0 : 48 + 28L * capacity);

                foreach (SpellEntrySet set in _spellEntries.Values)
                {
                    bytes += 40 + (set.Entries.Length == 0 ? 0 : 24 + 8L * set.Entries.Length);
                }

                return bytes;
            }
        }

        /// <summary>World.Clear: no book is left.</summary>
        public void OnWorldCleared()
        {
            _spellEntries.Clear();
            _spellEntries.TrimExcess();
        }
    }

    /// <summary>
    /// T2: a parent's children for a foreach (<see cref="WorldState.Children"/>): the ECS's (spell entries, then the contents'
    /// serials looked up in World) or the World child list (walked as the old loops did: the next link is read after the
    /// body). No allocation.
    /// </summary>
    internal readonly ref struct ChildList
    {
        private readonly World _world;
        private readonly ReadOnlySpan<Item> _spells;
        private readonly ReadOnlySpan<uint> _contents;
        private readonly LinkedObject _head;
        private readonly bool _ecs;

        public ChildList(World world, ReadOnlySpan<Item> spells, ReadOnlySpan<uint> contents)
        {
            _world = world;
            _spells = spells;
            _contents = contents;
            _head = null;
            _ecs = true;
        }

        public ChildList(LinkedObject head)
        {
            _world = null;
            _spells = default;
            _contents = default;
            _head = head;
            _ecs = false;
        }

        public bool IsEmpty => _ecs ? _spells.IsEmpty && _contents.IsEmpty : _head == null;

        public Enumerator GetEnumerator() => new Enumerator(this);

        public ref struct Enumerator
        {
            private readonly World _world;
            private readonly ReadOnlySpan<Item> _spells;
            private readonly ReadOnlySpan<uint> _contents;
            private readonly LinkedObject _head;
            private readonly bool _ecs;
            private int _index;
            private LinkedObject _node;
            private bool _started;

            public Enumerator(ChildList list)
            {
                _world = list._world;
                _spells = list._spells;
                _contents = list._contents;
                _head = list._head;
                _ecs = list._ecs;
                _index = 0;
                _node = null;
                _started = false;
                Current = null;
            }

            public Item Current { get; private set; }

            public bool MoveNext()
            {
                if (!_ecs)
                {
                    _node = _started ? _node?.Next : _head;
                    _started = true;
                    Current = (Item)_node;

                    return _node != null;
                }

                if (_index < _spells.Length)
                {
                    Current = _spells[_index++];

                    return true;
                }

                while (_index - _spells.Length < _contents.Length)
                {
                    Item it = _world.Items.Get(_contents[_index++ - _spells.Length]);

                    if (it != null)
                    {
                        Current = it;

                        return true;
                    }
                }

                Current = null;

                return false;
            }
        }
    }
}
