// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections;
using System.Collections.Generic;
using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;
using Friflo.Engine.ECS;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    /// <summary>
    /// T9 (<c>ecs_active_items_source</c>): the per-frame item update list with the ECS as its source. Each listed item has an
    /// <see cref="ActiveItemEntry"/> entity holding the item and its slot; <see cref="Item.ActiveEntryId"/> is the reverse handle
    /// (the O(1) membership test). The rules are <see cref="ActiveItemList"/>'s: an added item takes the next slot, and a removal
    /// moves the last slot's item into the freed one. The visiting order is rebuilt from the entities by <see cref="Rebuild"/>
    /// (a scatter by slot into a reused array, no sort, no allocation) at the start of every active item stage; adds and
    /// removals keep that array in step until the next rebuild, so readers between rebuilds see the same order.
    /// <para>
    /// The entry names its item by <see cref="ActiveItemEntry.Handle"/>, an index into <see cref="_items"/>, not by reference:
    /// Friflo's DeleteEntity leaves the moved-from component tail uncleared, so a reference in the component would keep
    /// destroyed items reachable. The table's slot is nulled when the entry goes.
    /// </para>
    /// </summary>
    internal sealed class ActiveItemStore : IReadOnlyList<Item>
    {
        private readonly EntityStore _store;
        private readonly ArchetypeQuery<ActiveItemEntry> _query;
        private Item[] _order = new Item[64];
        private int _count;
        private Item[] _items = new Item[64];
        private int[] _freeHandles = new int[64];
        private int _freeCount;
        private int _handleEnd;
        private readonly List<EcsEntity> _scratch = new List<EcsEntity>();

        public ActiveItemStore(EntityStore store)
        {
            _store = store;
            _query = store.Query<ActiveItemEntry>();
        }

        /// <summary>Items listed (the entities' count; equal to the order's length after every operation).</summary>
        public int Count => _count;

        /// <summary>Entities holding an <see cref="ActiveItemEntry"/> (the checker compares it with <see cref="Count"/>).</summary>
        public int EntityCount => _query.Count;

        public Item this[int index] => (uint)index < (uint)_count ? _order[index] : throw new ArgumentOutOfRangeException(nameof(index));

        /// <summary>The item's entry entity (null when it is not listed).</summary>
        public EcsEntity EntryOf(Item item) => item.ActiveEntryId == 0 ? default : _store.GetEntityById(item.ActiveEntryId);

        /// <summary>Lists <paramref name="item"/> in the next slot unless it is listed. True when it was added.</summary>
        public bool Add(Item item, bool mirrorTag)
        {
            if (item.ActiveEntryId != 0)
            {
                return false;
            }

            int slot = _count;
            int handle = TakeHandle();
            _items[handle] = item;
            item.ActiveEntryId = _store.CreateEntity(new ActiveItemEntry { Handle = handle, Serial = item.Serial, Slot = slot }).Id;
            Grow(slot + 1);
            _order[slot] = item;
            _count = slot + 1;

            if (mirrorTag)
            {
                ActiveItemList.SetTag(item, true);
            }

            return true;
        }

        /// <summary>Unlists the item in <paramref name="index"/>: the last slot's item takes the slot.</summary>
        public void SwapRemoveAt(int index, bool mirrorTag)
        {
            Item item = _order[index];
            int last = _count - 1;

            if (index != last)
            {
                Item moved = _order[last];
                EntryOf(moved).GetComponent<ActiveItemEntry>().Slot = index;
                _order[index] = moved;
            }

            _order[last] = null;
            _count = last;
            EcsEntity entry = EntryOf(item);
            FreeHandle(entry.GetComponent<ActiveItemEntry>().Handle);
            entry.DeleteEntity();
            item.ActiveEntryId = 0;

            if (mirrorTag)
            {
                ActiveItemList.SetTag(item, false);
            }
        }

        public void Clear(bool mirrorTag)
        {
            _scratch.Clear();

            foreach (EcsEntity e in _query.Entities)
            {
                _scratch.Add(e);
            }

            foreach (EcsEntity e in _scratch)
            {
                Item item = ItemOf(e.GetComponent<ActiveItemEntry>().Handle);
                e.DeleteEntity();
                item.ActiveEntryId = 0;

                if (mirrorTag)
                {
                    ActiveItemList.SetTag(item, false);
                }
            }

            _scratch.Clear();
            Array.Clear(_order, 0, _count);
            _count = 0;
            // also drops the items of entries deleted outside the store (only the checker's tests do that)
            Array.Clear(_items, 0, _handleEnd);
            _handleEnd = 0;
            _freeCount = 0;
        }

        /// <summary>The item an entry names (null for a handle the store does not hold).</summary>
        public Item ItemOf(int handle) => (uint)handle < (uint)_handleEnd ? _items[handle] : null;

        private int TakeHandle()
        {
            if (_freeCount > 0)
            {
                return _freeHandles[--_freeCount];
            }

            if (_handleEnd == _items.Length)
            {
                Array.Resize(ref _items, _items.Length * 2);
            }

            return _handleEnd++;
        }

        private void FreeHandle(int handle)
        {
            if ((uint)handle >= (uint)_handleEnd || _items[handle] == null)
            {
                return;
            }

            _items[handle] = null;

            if (_freeCount == _freeHandles.Length)
            {
                Array.Resize(ref _freeHandles, _freeHandles.Length * 2);
            }

            _freeHandles[_freeCount++] = handle;
        }

        /// <summary>
        /// Rebuilds the visiting order from the entities: each entry's item goes to its slot. False when the slots are not
        /// 0..n-1 once each (the order then holds the entries in query order, and <paramref name="error"/> says why); the rules
        /// above never leave them so.
        /// </summary>
        public bool Rebuild(out string error)
        {
            error = null;
            int n = _query.Count;
            Grow(n);
            Array.Clear(_order, 0, Math.Max(_count, n));
            _count = n;
            bool ok = true;

            foreach (var (entries, _) in _query.Chunks)
            {
                Span<ActiveItemEntry> span = entries.Span;

                for (int i = 0; i < span.Length; i++)
                {
                    ref ActiveItemEntry entry = ref span[i];
                    int slot = entry.Slot;

                    if ((uint)slot >= (uint)n || _order[slot] != null)
                    {
                        error ??= $"entry 0x{entry.Serial:X8} slot {slot} of {n}{((uint)slot < (uint)n ? " taken" : "")}";
                        ok = false;

                        continue;
                    }

                    _order[slot] = ItemOf(entry.Handle);
                }
            }

            if (!ok)
            {
                // keep every entry visitable (the checker reports the error): fill the order in query order
                int k = 0;

                foreach (var (entries, _) in _query.Chunks)
                {
                    Span<ActiveItemEntry> span = entries.Span;

                    for (int i = 0; i < span.Length; i++)
                    {
                        span[i].Slot = k;
                        _order[k++] = ItemOf(span[i].Handle);
                    }
                }
            }

            return ok;
        }

        private void Grow(int size)
        {
            if (size > _order.Length)
            {
                Array.Resize(ref _order, Math.Max(size, _order.Length * 2));
            }
        }

        public Enumerator GetEnumerator() => new Enumerator(this);

        IEnumerator<Item> IEnumerable<Item>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<Item>
        {
            private readonly ActiveItemStore _owner;
            private int _index;

            internal Enumerator(ActiveItemStore owner)
            {
                _owner = owner;
                _index = -1;
            }

            public Item Current => _owner._order[_index];

            object IEnumerator.Current => Current;

            public bool MoveNext() => ++_index < _owner._count;

            public void Reset() => _index = -1;

            public void Dispose() { }
        }
    }
}
