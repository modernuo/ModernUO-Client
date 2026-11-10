// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;

namespace ClassicUO.Game.GameObjects
{
    /// <summary>
    /// <c>World.Items</c>: the <c>Dictionary&lt;uint, Item&gt;</c> the world used to hold, behind the calls the client makes on
    /// it (the same enumeration order: <see cref="Values"/> and <see cref="Keys"/> are the dictionary's own collections). Every
    /// insert and removal goes through here, so each item can carry whether it is held and under which key, and
    /// <see cref="Holds"/> answers "this table holds this very instance under its serial" (the item side of
    /// <c>World.Holds</c>) without a lookup. An item held under a second key or by a second table falls back to the lookup
    /// for good (<see cref="Item.HeldShared"/>); the client never does either.
    /// </summary>
    internal sealed class ItemTable
    {
        private readonly Dictionary<uint, Item> _byKey = new Dictionary<uint, Item>();

        public int Count => _byKey.Count;

        public Dictionary<uint, Item>.ValueCollection Values => _byKey.Values;

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

            return true;
        }

        public bool Remove(uint serial)
        {
            if (!_byKey.Remove(serial, out Item item))
            {
                return false;
            }

            if (!item.HeldShared)
            {
                item.HeldBy = null;
            }

            return true;
        }

        public void Clear()
        {
            foreach (Item item in _byKey.Values)
            {
                if (!item.HeldShared)
                {
                    item.HeldBy = null;
                }
            }

            _byKey.Clear();
        }

        /// <summary>Whether this table holds <paramref name="item"/> itself under <c>item.Serial</c> (same answer as
        /// <c>ReferenceEquals(Get(item.Serial), item)</c>).</summary>
        public bool Holds(Item item)
        {
            if (item.HeldShared)
            {
                return ReferenceEquals(Get(item.Serial), item);
            }

            return ReferenceEquals(item.HeldBy, this) && item.HeldKey == item.Serial;
        }
    }
}
