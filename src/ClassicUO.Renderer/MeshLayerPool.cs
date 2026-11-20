// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Xna.Framework.Graphics;
using static ClassicUO.Renderer.UltimaBatcher2D;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// Spare <see cref="MeshLayer"/> array sets (vertices, textures, visibility, hue keys, index data and the vertex buffer), by
    /// size. A layer that needs more room trades its set for a larger one here, and a pooled chunk's layer trades a large set
    /// back for its base size (<see cref="MeshLayer.SoftReset(MeshLayerPool, int)"/>). Before this, a layer grew in place and
    /// never shrank, so every pooled chunk kept the arrays of the busiest chunk it had ever held (meshlayer-growth). The spares
    /// are capped at <see cref="SpareBudgetBytes"/> (managed and GPU bytes together); a set returned above the cap is dropped.
    /// Main thread only (chunk build and destroy).
    /// </summary>
    public sealed class MeshLayerPool
    {
        /// <summary>The default spare cap: 8 MB.</summary>
        public const long DefaultSpareBudgetBytes = 8L << 20;

        private readonly Stack<Set>[] _bySize = new Stack<Set>[32];

        public MeshLayerPool(long spareBudgetBytes = DefaultSpareBudgetBytes)
        {
            SpareBudgetBytes = spareBudgetBytes;
        }

        /// <summary>Spares are kept while their total stays at or below this many bytes.</summary>
        public long SpareBudgetBytes { get; }

        /// <summary>Managed and GPU bytes of the spare sets.</summary>
        public long SpareBytes { get; private set; }

        public int SpareSets { get; private set; }

        /// <summary>Sets handed out from the spares, sets created because none of the size was spare, sets dropped at the cap.</summary>
        public long Reused, Created, Dropped;

        /// <summary>A spare set of exactly <paramref name="size"/> sprites, or null.</summary>
        internal Set Take(int size)
        {
            Stack<Set> stack = BitOperations.IsPow2(size) ? _bySize[BitOperations.Log2((uint)size)] : null;

            if (stack == null || stack.Count == 0)
            {
                Created++;

                return null;
            }

            Set set = stack.Pop();
            SpareBytes -= set.Bytes;
            SpareSets--;
            Reused++;

            return set;
        }

        /// <summary>Keeps <paramref name="set"/> as a spare, or drops it (and disposes its vertex buffer) above the cap.</summary>
        internal void Return(Set set)
        {
            int size = set.Vertices.Length;
            set.Bytes = set.HeldBytes();

            if (!BitOperations.IsPow2(size) || SpareBytes + set.Bytes > SpareBudgetBytes)
            {
                set.Drop();
                Dropped++;

                return;
            }

            // no texture stays reachable from a spare; hue keys start at 0 (unknown) for the next layer, as after Reset
            Array.Clear(set.Textures);
            Array.Clear(set.HueKeys);

            ref Stack<Set> stack = ref _bySize[BitOperations.Log2((uint)size)];
            stack ??= new Stack<Set>();
            stack.Push(set);
            SpareBytes += set.Bytes;
            SpareSets++;
        }

        /// <summary>Drops every spare and disposes their vertex buffers.</summary>
        public void Clear()
        {
            foreach (Stack<Set> stack in _bySize)
            {
                if (stack == null)
                {
                    continue;
                }

                while (stack.Count > 0)
                {
                    stack.Pop().Drop();
                }
            }

            SpareBytes = 0;
            SpareSets = 0;
        }

        /// <summary>One layer's arrays and vertex buffer, swapped whole with <see cref="MeshLayer"/>'s.</summary>
        internal sealed class Set
        {
            public PositionNormalTextureColor4[] Vertices;
            public Texture2D[] Textures;
            public bool[] Visible;
            public long[] HueKeys;
            public ulong[] VisBits;
            public bool[] PrevVisible;
            public ulong[] PrevVisBits;
            public short[] IndexData;
            public DynamicVertexBuffer VertexBuffer;
            public long Bytes;

            /// <summary>A new set for <paramref name="size"/> sprites, every array at full size (no lazy growth later).</summary>
            public static Set Create(int size)
            {
                int words = (size + 63) >> 6;

                return new Set
                {
                    Vertices = new PositionNormalTextureColor4[size],
                    Textures = new Texture2D[size],
                    Visible = new bool[size],
                    HueKeys = new long[size],
                    VisBits = new ulong[words],
                    PrevVisible = new bool[size],
                    PrevVisBits = new ulong[words],
                    IndexData = new short[size * 6]
                };
            }

            public long HeldBytes()
            {
                long bytes = (long)Vertices.Length * PositionNormalTextureColor4.SIZE_IN_BYTES + (long)Textures.Length * IntPtr.Size
                    + Visible.Length + PrevVisible.Length + IndexData.Length * sizeof(short) + HueKeys.Length * sizeof(long)
                    + (VisBits.Length + PrevVisBits.Length) * sizeof(ulong);

                if (VertexBuffer != null && !VertexBuffer.IsDisposed)
                {
                    bytes += (long)VertexBuffer.VertexCount * (PositionNormalTextureColor4.SIZE_IN_BYTES / 4);
                }

                return bytes;
            }

            public void Drop()
            {
                VertexBuffer?.Dispose();
                VertexBuffer = null;
                Vertices = null;
                Textures = null;
                Visible = null;
                HueKeys = null;
                VisBits = null;
                PrevVisible = null;
                PrevVisBits = null;
                IndexData = null;
            }
        }
    }
}
