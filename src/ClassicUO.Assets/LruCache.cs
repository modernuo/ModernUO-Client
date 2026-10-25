// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;

namespace ClassicUO.Assets
{
    /// <summary>A bounded map that evicts the least recently used entry. Not thread-safe.</summary>
    internal sealed class LruCache<TKey, TValue>
    {
        private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;
        private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new LinkedList<KeyValuePair<TKey, TValue>>();

        public LruCache(int capacity)
        {
            Capacity = capacity;
            _map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(capacity);
        }

        public int Capacity { get; }
        public int Count => _map.Count;

        public bool TryGet(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;

                return true;
            }

            value = default;

            return false;
        }

        /// <summary>Adds (or replaces) an entry. When full, the least recently used entry is dropped and its value returned in <paramref name="evicted"/>.</summary>
        public bool Add(TKey key, TValue value, out TValue evicted)
        {
            evicted = default;
            bool dropped = false;

            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }
            else if (_map.Count >= Capacity && _order.Last != null)
            {
                var last = _order.Last;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                evicted = last.Value.Value;
                dropped = true;
            }

            _map[key] = _order.AddFirst(new KeyValuePair<TKey, TValue>(key, value));

            return dropped;
        }

        public void Clear()
        {
            _map.Clear();
            _order.Clear();
        }
    }
}
