// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game
{
    /// <summary>
    /// T2 (ecs-transition-plan.md): the world state the ECS owns, read by the client. The first piece is container contents:
    /// <see cref="Contents"/> is a parent's attached children in child order, from the mirror's per-parent cached span.
    /// </summary>
    internal sealed partial class WorldState
    {
        private readonly World _world;

        /// <summary>
        /// T9 child-list slice B2: the deepest nesting a recursive walk of the children follows (and the longest parent chain
        /// <see cref="WorldWriter"/> attaches under). Real containers nest a few levels; the bound only matters if a loop in
        /// the serial graph ever got in, which the writer refuses.
        /// </summary>
        internal const int MaxNestingDepth = 256;

        public WorldState(World world)
        {
            _world = world;
        }

        /// <summary>
        /// T2: the serials of the parent's attached children (worn or contained) in child order: arrival order, a re-push to
        /// the back, as ClassicUO's <c>LinkedObject</c> list. Orphans (a child that arrived before its parent) and the
        /// spellbook's detached spell entries (<see cref="Spellbook"/>) are not in it. Valid until the parent's contents
        /// change; no allocation.
        /// </summary>
        public ReadOnlySpan<uint> Contents(uint parent) => _world.Ecs.Mirror.Contents(parent);

        /// <summary>
        /// T2: the spell set of a spellbook's 0xBF.1B content (bit i = spell i + 1; 0 for none). ClassicUO keeps it as
        /// detached entries (serial and amount i + 1, graphic 0x1F2E) enumerated first by <see cref="Children"/>.
        /// </summary>
        public ulong Spellbook(uint book) => _world.Ecs.Mirror.Spellbook(book);

        // ---- T2 readers: the child readers (container, grid loot, trade and spellbook gumps, the shop's lists, FindItem*,
        // the counters, macros) walk Children(parent), the ECS enumeration. T9 removed the entity child list they replaced.

        /// <summary>
        /// T2: the parent's children, in child order, from the ECS (a spellbook's spell entries, then <see cref="Contents"/>).
        /// A reader must not change the parent's contents while it walks them (no reader does).
        /// </summary>
        public ChildList Children(Entity parent)
        {
            if (parent == null)
            {
                return default;
            }

            // Codex audit P1: the ECS keys contents by serial, so an instance that is not the entity World holds for its
            // serial (a spellbook's detached spell entry, serial 1-64, which can be a real mobile's; a destroyed or replaced
            // object) must not read that entity's contents: it is a leaf
            if (!IsCurrent(parent))
            {
                return default;
            }

            // T9 child-list slice B: an item and a mobile World holds under one serial (the unconverted EquipItem path can
            // make one) read the same, merged contents: the mirror keeps one record per serial and cannot tell their
            // children apart. The item is never its own child (it can be worn by the mobile under its serial): skipped, so
            // the recursive readers and the destroy cascade do not loop
            return new ChildList(_world, SpellEntries(parent.Serial), _world.Ecs.Mirror.Contents(parent.Serial), parent as Item);
        }

        /// <summary>
        /// T9 child-list slices A and B: <see cref="SnapshotContents"/> of an entity's children, as <see cref="Children"/>
        /// gives them without the spell entries: an item never lists itself (the shared-serial case, see <see cref="Children"/>).
        /// </summary>
        public ContentsSnapshot SnapshotChildren(Entity parent)
        {
            ReadOnlySpan<uint> contents = Contents(parent.Serial);
            bool item = parent is Item;
            int start = _snapshotTop;

            if (start + contents.Length > _snapshots.Length)
            {
                Array.Resize(ref _snapshots, Math.Max(start + contents.Length, _snapshots.Length * 2));
            }

            int n = 0;

            foreach (uint serial in contents)
            {
                if (!(item && serial == parent.Serial))
                {
                    _snapshots[start + n++] = serial;
                }
            }

            _snapshotTop = start + n;

            return new ContentsSnapshot(this, start, new ReadOnlySpan<uint>(_snapshots, start, n));
        }

        // the instance World holds for the entity's serial (the one the ECS's record under that serial stands for)
        private bool IsCurrent(Entity e) => _world.Holds(e);

        /// <summary>T2: <c>!parent.IsEmpty</c>, from the ECS.</summary>
        public bool HasChildren(Entity parent) => !Children(parent).IsEmpty;

        /// <summary>T9 child-list slice A: the parent's first child (<c>parent.Items</c>), from the ECS; null when none.</summary>
        public Item FirstChild(Entity parent)
        {
            foreach (Item it in Children(parent))
            {
                return it;
            }

            return null;
        }

        // ---- T9 child-list slice A: walks that change the contents they walk (the destroy cascades, the funnel's clears,
        // the re-key) copy the parent's Contents here first; each removal or detach changes the mirror's span. A stack, so
        // nested walks (a cascade's recursion, a removal inside a clear) each keep their own copy; a span handed out stays
        // valid when a nested copy grows the array (it keeps the old one). No allocation once the stack has grown.
        private uint[] _snapshots = new uint[64];
        private int _snapshotTop;

        /// <summary>
        /// T9 slice A: a copy of <see cref="Contents"/>, for a walk that changes the parent's contents. Dispose it (a <c>using</c>) in reverse order of taking.
        /// </summary>
        public ContentsSnapshot SnapshotContents(uint parent)
        {
            ReadOnlySpan<uint> contents = Contents(parent);
            int start = _snapshotTop;
            int end = start + contents.Length;

            if (end > _snapshots.Length)
            {
                Array.Resize(ref _snapshots, Math.Max(end, _snapshots.Length * 2));
            }

            contents.CopyTo(_snapshots.AsSpan(start));
            _snapshotTop = end;

            return new ContentsSnapshot(this, start, new ReadOnlySpan<uint>(_snapshots, start, contents.Length));
        }

        internal void ReleaseSnapshot(int start) => _snapshotTop = start;

        /// <summary>Tests: the snapshot stack's depth (0 when every snapshot was released).</summary>
        internal int SnapshotDepth => _snapshotTop;

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

        /// <summary>The memory log: WorldWriter's spell-entry scratch list (List 32 B + Item[SpellScratchCapacity]).</summary>
        internal const long SpellScratchBytes = 32 + 24 + 8L * WorldWriter.SpellScratchCapacity;

        /// <summary>Tests and the memory log: the books whose entries are held.</summary>
        public int SpellEntryBooks => _spellEntries.Count;

        /// <summary>
        /// The memory log: the bytes the spell-entry storage holds, by layout-based accounting of the 64-bit runtime (not
        /// measured): the dictionary (<see cref="ClassicUO.Ecs.WorldMirror.DictionaryBytes"/>: object, 8-aligned buckets,
        /// entries 24 B per slot, the ValueCollection <c>.Values</c> keeps), each set 40 B with its Item[] 24 B + 8 B per
        /// slot, and WorldWriter's permanent spell-entry scratch list (List 32 B + its Item[64] 24 + 512 B = 568 B; a book
        /// has at most 64 spells, so it never grows). The entries' Item objects are not counted (Codex audit round 2: alignment, the ValueCollection and the scratch list were missing).
        /// </summary>
        public long SpellEntriesBytes
        {
            get
            {
                long bytes = ClassicUO.Ecs.WorldMirror.DictionaryBytes(_spellEntries.EnsureCapacity(0), 24) + SpellScratchBytes;

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
            ClearLayerTables();
        }
    }

    /// <summary>T9 slice A: a parent's child serials copied before a walk that changes them (<see cref="WorldState.SnapshotContents"/>).</summary>
    internal readonly ref struct ContentsSnapshot
    {
        private readonly WorldState _state;
        private readonly int _start;

        public ContentsSnapshot(WorldState state, int start, ReadOnlySpan<uint> serials)
        {
            _state = state;
            _start = start;
            Serials = serials;
        }

        public ReadOnlySpan<uint> Serials { get; }

        public void Dispose() => _state?.ReleaseSnapshot(_start);
    }

    /// <summary>
    /// T2: a parent's children for a foreach (<see cref="WorldState.Children"/>): the ECS's spell entries, then the contents'
    /// serials looked up in World. No allocation.
    /// </summary>
    internal readonly ref struct ChildList
    {
        private readonly World _world;
        private readonly ReadOnlySpan<Item> _spells;
        private readonly ReadOnlySpan<uint> _contents;
        private readonly Item _self;

        /// <param name="self">T9 child-list slice B: an item parent, skipped among its contents (an item under a mobile's
        /// serial, worn by that mobile, is in the merged contents of that serial); null for a mobile.</param>
        public ChildList(World world, ReadOnlySpan<Item> spells, ReadOnlySpan<uint> contents, Item self)
        {
            _world = world;
            _spells = spells;
            _contents = contents;
            _self = self;
        }

        public bool IsEmpty =>
            _spells.IsEmpty && (_contents.IsEmpty || _self != null && _contents.Length == 1 && _contents[0] == _self.Serial);

        public Enumerator GetEnumerator() => new Enumerator(this);

        public ref struct Enumerator
        {
            private readonly World _world;
            private readonly ReadOnlySpan<Item> _spells;
            private readonly ReadOnlySpan<uint> _contents;
            private readonly Item _self;
            private int _index;

            public Enumerator(ChildList list)
            {
                _world = list._world;
                _spells = list._spells;
                _contents = list._contents;
                _self = list._self;
                _index = 0;
                Current = null;
            }

            public Item Current { get; private set; }

            public bool MoveNext()
            {
                if (_index < _spells.Length)
                {
                    Current = _spells[_index++];

                    return true;
                }

                while (_index - _spells.Length < _contents.Length)
                {
                    Item it = _world.FindItem(_contents[_index++ - _spells.Length]);

                    if (it != null && !ReferenceEquals(it, _self))
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
