// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections;
using System.Collections.Generic;

namespace ClassicUO.Game.GameObjects
{
    /// <summary>
    /// <c>World.Items</c>: the <c>Dictionary&lt;uint, Item&gt;</c> the world used to hold, behind the calls the client makes on
    /// it. Every insert and removal goes through here, so each item can carry whether it is held and under which key, and
    /// <see cref="Holds"/> answers "this table holds this very instance under its serial" (the item side of
    /// <c>World.Holds</c>) without a lookup. An item held under a second key or by a second table falls back to the lookup
    /// for good (<see cref="Item.HeldShared"/>); the client never does either.
    /// <para>
    /// T9 slice 4: the table also keeps its items in slots, as <see cref="MobileTable"/> does (a removed entry's slot goes on a
    /// LIFO free list, an insert takes the most recently freed slot, else appends; <see cref="Clear"/> starts over), which is
    /// exactly the order the dictionary enumerates in. With <c>world_entity_index</c> on, <see cref="Values"/> walks the
    /// slots, so the item update order and every first-match scan over the items need no dictionary; off, it walks the
    /// dictionary as before. CUO_ENTITY_INDEX_CHECK compares both orders every world update (<see cref="OrderMatches"/>);
    /// <c>ItemTableTests</c> run random operations against a real dictionary. The serial key (<see cref="Get"/>) is still the
    /// dictionary: World's own bookkeeping must find objects the mirror's index cannot answer (destroyed but not yet swept,
    /// created but not yet committed).
    /// </para>
    /// Single-threaded, like the world. Removing and clearing during enumeration are allowed (as with the dictionary);
    /// inserting a new key invalidates active enumerators (they throw <see cref="InvalidOperationException"/> on the next step).
    /// </summary>
    internal sealed class ItemTable
    {
        private readonly Dictionary<uint, Item> _byKey = new Dictionary<uint, Item>();
        private readonly World _owner;
        private Item[] _slots = new Item[64];
        private uint[] _slotKeys = new uint[64]; // the key each slot holds its item under (a shared item's removal searches it)
        private int _count; // slots ever used since the last Clear (the dictionary's _count)
        private readonly Stack<int> _free = new Stack<int>();
        private int _version; // bumped by every insert of a new key (the dictionary's enumerators threw on those, and only those)

        public ItemTable() { }

        /// <summary>The table of <paramref name="owner"/>: <see cref="Values"/> follows its <c>world_entity_index</c> switch.</summary>
        public ItemTable(World owner) => _owner = owner;

        /// <summary>Tests: the enumeration of a table with no owner (true: the slots, false: the dictionary).</summary>
        internal bool OrderedWithoutOwner;

        /// <summary>Whether <see cref="Values"/> walks the slots (the owner's <c>world_entity_index</c>) or the dictionary.</summary>
        public bool Ordered => _owner != null ? _owner.EntityIndexOn : OrderedWithoutOwner;

        public int Count => _byKey.Count;

        public ValueCollection Values => new ValueCollection(this, Ordered);

        public Dictionary<uint, Item>.KeyCollection Keys => _byKey.Keys;

        public Item this[uint serial] => _byKey[serial];

        public bool TryGetValue(uint serial, out Item item) => _byKey.TryGetValue(serial, out item);

        public Item Get(uint serial)
        {
            _byKey.TryGetValue(serial, out Item item);

            return item;
        }

        public bool Contains(uint serial) => _byKey.ContainsKey(serial);

        public bool ContainsKey(uint serial) => _byKey.ContainsKey(serial);

        /// <summary>Adds <paramref name="item"/> under its serial unless that serial is taken.</summary>
        public bool Add(Item item)
        {
            uint serial = item.Serial;

            if (!_byKey.TryAdd(serial, item))
            {
                return false;
            }

            if (item.HeldBy != null)
            {
                item.HeldShared = true;
            }

            item.HeldBy = this;
            item.HeldKey = serial;
            _version++;
            int slot;

            if (_free.Count != 0)
            {
                slot = _free.Pop();
            }
            else
            {
                if (_count == _slots.Length)
                {
                    Array.Resize(ref _slots, _count * 2);
                    Array.Resize(ref _slotKeys, _count * 2);
                }

                slot = _count++;
            }

            _slots[slot] = item;
            _slotKeys[slot] = serial;

            if (!item.HeldShared)
            {
                item.Ordinal = slot;
            }

            return true;
        }

        public bool Remove(uint serial)
        {
            if (!_byKey.Remove(serial, out Item item))
            {
                return false;
            }

            int slot = item.HeldShared ? FindSlot(item, serial) : item.Ordinal;
            _slots[slot] = null;
            _free.Push(slot);

            if (!item.HeldShared)
            {
                item.HeldBy = null;
            }

            return true;
        }

        public void Clear()
        {
            for (int i = 0; i < _count; i++)
            {
                Item item = _slots[i];

                if (item != null && !item.HeldShared)
                {
                    item.HeldBy = null;
                }
            }

            _byKey.Clear();
            Array.Clear(_slots, 0, _count);
            _count = 0;
            _free.Clear();
        }

        // a shared item's Ordinal is not this table's slot for it: search (tests only; the client never shares an item)
        private int FindSlot(Item item, uint serial)
        {
            for (int i = 0; i < _count; i++)
            {
                if (ReferenceEquals(_slots[i], item) && _slotKeys[i] == serial)
                {
                    return i;
                }
            }

            throw new InvalidOperationException("an item held by the dictionary has no slot");
        }

        /// <summary>Whether this table holds <paramref name="item"/> itself under <c>item.Serial</c> (same answer as
        /// <c>ReferenceEquals(Get(item.Serial), item)</c>).</summary>
        public bool Holds(Item item) => Holds(item, item.Serial);

        /// <summary><see cref="Holds(Item)"/> under <paramref name="serial"/>.</summary>
        public bool Holds(Item item, uint serial)
        {
            if (item.HeldShared)
            {
                return ReferenceEquals(Get(serial), item);
            }

            return ReferenceEquals(item.HeldBy, this) && item.HeldKey == serial;
        }

        /// <summary>
        /// CUO_ENTITY_INDEX_CHECK (T9 slice 4): whether the slots enumerate the same items in the same order as the dictionary.
        /// <paramref name="position"/> is the first position that differs (-1 when equal). No allocation.
        /// </summary>
        internal bool OrderMatches(out int position)
        {
            int i = 0;
            int slot = 0;

            foreach (Item item in _byKey.Values)
            {
                while (slot < _count && _slots[slot] == null)
                {
                    slot++;
                }

                if (slot >= _count || !ReferenceEquals(_slots[slot], item))
                {
                    position = i;

                    return false;
                }

                slot++;
                i++;
            }

            while (slot < _count && _slots[slot] == null)
            {
                slot++;
            }

            position = slot < _count ? i : -1;

            return position < 0;
        }

        /// <summary>The items in the dictionary's enumeration order: walked from the slots when ordered, else the dictionary.</summary>
        public readonly struct ValueCollection : IEnumerable<Item>
        {
            private readonly ItemTable _table;
            private readonly bool _ordered;

            internal ValueCollection(ItemTable table, bool ordered)
            {
                _table = table;
                _ordered = ordered;
            }

            public Enumerator GetEnumerator() => new Enumerator(_table, _ordered);

            IEnumerator<Item> IEnumerable<Item>.GetEnumerator() => new Enumerator(_table, _ordered);

            IEnumerator IEnumerable.GetEnumerator() => new Enumerator(_table, _ordered);
        }

        public struct Enumerator : IEnumerator<Item>
        {
            private readonly ItemTable _table;
            private readonly bool _ordered;
            private readonly int _version;
            private Dictionary<uint, Item>.Enumerator _dict;
            private int _index;
            private Item _current;
            private int _state; // 0 before the first MoveNext (or after Reset), 1 on an item, 2 past the end

            internal Enumerator(ItemTable table, bool ordered)
            {
                _table = table;
                _ordered = ordered;
                _version = table._version;
                _dict = ordered ? default : table._byKey.GetEnumerator();
                _index = 0;
                _current = null;
                _state = 0;
            }

            public Item Current => _current;

            // as the dictionary's enumerators (Codex audit of slice 4, P3): the non-generic Current throws before the first
            // MoveNext, past the end and after Reset
            object IEnumerator.Current
            {
                get
                {
                    if (_state != 1)
                    {
                        throw new InvalidOperationException("the enumeration has not started or has ended");
                    }

                    return _current;
                }
            }

            public bool MoveNext()
            {
                if (!_ordered)
                {
                    if (_dict.MoveNext())
                    {
                        _current = _dict.Current.Value;
                        _state = 1;

                        return true;
                    }

                    _current = null;
                    _state = 2;

                    return false;
                }

                // removals and Clear during the walk are fine (a removed slot is null); an insert of a new key throws, as the
                // dictionary's enumerator did
                if (_version != _table._version)
                {
                    throw new InvalidOperationException("the table was modified during enumeration");
                }

                while (_index < _table._count)
                {
                    Item item = _table._slots[_index++];

                    if (item != null)
                    {
                        _current = item;
                        _state = 1;

                        return true;
                    }
                }

                _current = null;
                _state = 2;

                return false;
            }

            public void Reset()
            {
                // both modes (Codex audit of slice 4, P3): an insert since the enumerator was made invalidates it, as it did the
                // dictionary's; the table's version counts the same inserts that bump the dictionary's
                if (_version != _table._version)
                {
                    throw new InvalidOperationException("the table was modified during enumeration");
                }

                if (!_ordered)
                {
                    _dict.Dispose();
                    _dict = _table._byKey.GetEnumerator();
                }

                _index = 0;
                _current = null;
                _state = 0;
            }

            public void Dispose()
            {
                if (!_ordered)
                {
                    _dict.Dispose();
                }
            }
        }
    }
}
