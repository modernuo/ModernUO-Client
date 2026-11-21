// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;

namespace ClassicUO.Utility
{
    public class QueuedPool<T> where T : class, new()
    {
        private readonly Action<T> _on_pickup;
        private readonly Stack<T> _pool;


        public QueuedPool(int size, Action<T> onpickup = null)
        {
            MaxSize = size;
            _pool = new Stack<T>(size);
            _on_pickup = onpickup;

            for (int i = 0; i < size; i++)
            {
                _pool.Push(new T());
            }
        }


        public int MaxSize { get; }

        public int Remains => MaxSize - _pool.Count;

        /// <summary>Objects created because the pool was empty, and returns dropped because it was full (counters).</summary>
        public int Misses, Dropped;

        /// <summary>Objects the pool holds now (at most <see cref="MaxSize"/>).</summary>
        public int Count => _pool.Count;

        public T GetOne()
        {
            T result;

            if (_pool.Count != 0)
            {
                result = _pool.Pop();
            }
            else
            {
                result = new T();
                Misses++;
            }

            _on_pickup?.Invoke(result);

            return result;
        }

        public void ReturnOne(T obj)
        {
            if (obj == null)
            {
                return;
            }

            // capped at MaxSize: a burst above the prefill no longer keeps its peak alive for the session
            if (_pool.Count < MaxSize)
            {
                _pool.Push(obj);
            }
            else
            {
                Dropped++;
            }
        }

        public void Clear()
        {
            _pool.Clear();
        }
    }
}