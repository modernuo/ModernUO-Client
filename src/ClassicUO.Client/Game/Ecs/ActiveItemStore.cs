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
    /// </summary>
    internal sealed class ActiveItemStore : IReadOnlyList<Item>
    {
        private readonly EntityStore _store;
        private readonly ArchetypeQuery<ActiveItemEntry> _query;
        private Item[] _order = new Item[64];
        private int _count;
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
            item.ActiveEntryId = _store.CreateEntity(new ActiveItemEntry { Item = item, Serial = item.Serial, Slot = slot }).Id;
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
            EntryOf(item).DeleteEntity();
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
                Item item = (Item)e.GetComponent<ActiveItemEntry>().Item;
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

                    _order[slot] = (Item)entry.Item;
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
                        _order[k++] = (Item)span[i].Item;
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
