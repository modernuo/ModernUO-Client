// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game
{
    /// <summary>
    /// W5/T5d: the items whose <c>Update</c> does something this frame (corpses, items whose handles just opened), in visiting
    /// order. Membership is <see cref="Item.InUpdateList"/> (the O(1) test) and, with <c>mirror</c> on
    /// (<c>world_active_items_system</c>), the <see cref="ItemActive"/> tag on the item's mirror entity; the order is the
    /// list's: items are appended, and a removal swaps the last item into the freed slot (the order every caller relied on).
    /// </summary>
    internal sealed class ActiveItemList
    {
        private readonly List<Item> _items = new List<Item>();

        public int Count => _items.Count;

        public IReadOnlyList<Item> Items => _items;

        public Item this[int index] => _items[index];

        /// <summary>Appends <paramref name="item"/> unless it is listed. True when it was added.</summary>
        public bool Add(Item item, bool mirror)
        {
            if (item.InUpdateList)
            {
                return false;
            }

            item.InUpdateList = true;
            _items.Add(item);

            if (mirror)
            {
                SetTag(item, true);
            }

            return true;
        }

        /// <summary>Removes the item at <paramref name="index"/> by moving the last one into its slot.</summary>
        public void SwapRemoveAt(int index, bool mirror)
        {
            Item item = _items[index];
            item.InUpdateList = false;
            _items[index] = _items[_items.Count - 1];
            _items.RemoveAt(_items.Count - 1);

            if (mirror)
            {
                SetTag(item, false);
            }
        }

        public void Clear(bool mirror)
        {
            foreach (Item item in _items)
            {
                item.InUpdateList = false;

                if (mirror)
                {
                    SetTag(item, false);
                }
            }

            _items.Clear();
        }

        private static void SetTag(Item item, bool on)
        {
            if (item.TryScalarEntity(out Friflo.Engine.ECS.Entity e))
            {
                if (on)
                {
                    e.AddTag<ItemActive>();
                }
                else if (e.Tags.Has<ItemActive>())
                {
                    e.RemoveTag<ItemActive>();
                }
            }
        }
    }
}
