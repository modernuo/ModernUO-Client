// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// The destroyed <see cref="Chunk"/>s of one <see cref="World"/>, handed out again by <see cref="Chunk.Create"/>.
    /// A chunk is bound to the World it was built for (its tile objects, mesh and Load/Destroy all go through that World's Map
    /// and <see cref="World.MapRecycler"/>), so the pool is owned by the World and refuses any other World's chunk: it
    /// can never hand a chunk out into another World. Like <see cref="MapObjectRecycler"/> it survives
    /// <see cref="World.Clear"/> (relog: same World, same objects, bounded by <see cref="Constants.PREDICTABLE_CHUNKS"/>)
    /// and is released by <see cref="World.ReleasePools"/> when the World itself is done.
    /// </summary>
    internal sealed class ChunkPool
    {
        private readonly Queue<Chunk> _pool = new Queue<Chunk>();
        private readonly World _owner;

        public ChunkPool(World owner)
        {
            _owner = owner;
        }

        public int Count => _pool.Count;

        public IEnumerable<Chunk> Chunks => _pool;

        /// <summary>A pooled chunk, or null (allocate one).</summary>
        public Chunk Take() => _pool.Count > 0 ? _pool.Dequeue() : null;

        /// <summary>False when the pool is full, or the chunk belongs to another World (the caller clears it instead).</summary>
        public bool CanAdd(Chunk chunk) => ReferenceEquals(chunk.Owner, _owner) && _pool.Count < Constants.PREDICTABLE_CHUNKS;

        /// <summary>Keeps a destroyed chunk of this World (<see cref="CanAdd"/> first).</summary>
        public void Add(Chunk chunk)
        {
            if (!CanAdd(chunk))
            {
                throw new InvalidOperationException("chunk of another World, or the pool is full");
            }

            _pool.Enqueue(chunk);
        }

        /// <summary>Login warm-up (Chunk.Prefill): fills the pool up to <paramref name="count"/> chunks. Returns the chunks added.</summary>
        public int Prefill(int count, int staticsCapacity)
        {
            int added = 0;
            count = Math.Min(count, Constants.PREDICTABLE_CHUNKS);

            while (_pool.Count < count)
            {
                var c = new Chunk(_owner) { IsDestroyed = true };
                c.Mesh.Statics.EnsureCapacity(staticsCapacity);
                _pool.Enqueue(c);
                added++;
            }

            return added;
        }

        /// <summary>Drops every pooled chunk (the World is done with them).</summary>
        public void Clear()
        {
            while (_pool.Count > 0)
            {
                _pool.Dequeue().Mesh.Clear();
            }
        }
    }
}
