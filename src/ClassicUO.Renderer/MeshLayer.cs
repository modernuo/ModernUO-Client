// SPDX-License-Identifier: BSD-2-Clause

using System;
using BitOperations = System.Numerics.BitOperations;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using static ClassicUO.Renderer.UltimaBatcher2D;

namespace ClassicUO.Renderer
{
    public struct TextureRun
    {
        public Texture2D Texture;
        public int Start;
        public int Count;
    }

    /// <summary>
    /// A GPU-buffered layer of sprites (vertices + textures + visibility) with
    /// per-frame index buffer filtering. Sprites must be added in texture-sorted
    /// order (via bucket-insert in the caller) so that draw call batching is optimal.
    /// </summary>
    public sealed class MeshLayer
    {
        private const int INITIAL_CAPACITY = 64;

        // Sprite data (parallel arrays, must be in texture-sorted order)
        public PositionNormalTextureColor4[] Vertices = new PositionNormalTextureColor4[INITIAL_CAPACITY];
        public Texture2D[] Textures = new Texture2D[INITIAL_CAPACITY];
        public int Count;

        // GPU vertex buffer
        public DynamicVertexBuffer VertexBuffer;

        // Per-frame visibility filtering
        private short[] _visibleIndexData = new short[INITIAL_CAPACITY * 6];
        public int VisibleSpriteCount;
        public TextureRun[] VisibleRuns = new TextureRun[16];
        public int VisibleRunCount;
        private bool _visibilityDirty = true;
        private bool _alphaDirty;

        // mesh_vis_bits: this frame's visibility as one bit per sprite (ResetVisibility clears (Count + 63) / 64 words;
        // BuildVisibleIndices compares words and walks set bits)
        private ulong[] _visBits = new ulong[INITIAL_CAPACITY / 64];
        private ulong[] _prevVisBits = new ulong[INITIAL_CAPACITY / 64];

        // Track which sprite indices had alpha modified (avoids full scan in ResetAlpha)
        private int[] _alphaDirtyIndices = new int[16];
        private int _alphaDirtyCount;

        // mesh_hue_on_change: per sprite, the key (caller's hue epoch and object hue) its vertex hue was last set from; 0 =
        // unknown (built, or set without a key, like the highlight). The caller skips SetHue while the key matches.
        private long[] _hueKeys = new long[INITIAL_CAPACITY];

        /// <summary>Managed bytes held by this layer's arrays ([gcs] pools line).</summary>
        public long HeldBytes =>
            (long)Vertices.Length * PositionNormalTextureColor4.SIZE_IN_BYTES + (long)Textures.Length * IntPtr.Size
            + _visibleIndexData.Length * sizeof(short) + _hueKeys.Length * sizeof(long)
            + (_visBits.Length + _prevVisBits.Length) * sizeof(ulong) + _alphaDirtyIndices.Length * sizeof(int)
            + VisibleRuns.Length * (IntPtr.Size + 2 * sizeof(int));

        /// <summary>Bytes of this layer's GPU vertex buffer ([gcs] pools line).</summary>
        public long GpuBytes => VertexBuffer == null || VertexBuffer.IsDisposed ? 0 : (long)VertexBuffer.VertexCount * (PositionNormalTextureColor4.SIZE_IN_BYTES / 4);

        /// <summary>
        /// Ensures internal arrays can hold at least <paramref name="minCapacity"/> sprites.
        /// </summary>
        public void EnsureCapacity(int minCapacity)
        {
            if (minCapacity <= Vertices.Length)
                return;

            int newSize = Vertices.Length;
            while (newSize < minCapacity)
                newSize *= 2;

            Array.Resize(ref Vertices, newSize);
            Array.Resize(ref Textures, newSize);
            Array.Resize(ref _hueKeys, newSize);
            Array.Resize(ref _visBits, (newSize + 63) >> 6);
        }

        /// <summary>
        /// <see cref="EnsureCapacity(int)"/> for a layer just <see cref="Reset"/> (its sprites are discarded, not copied): the
        /// arrays and the vertex buffer are exchanged for a <paramref name="pool"/> set of the same size EnsureCapacity would
        /// grow to, and the old set goes to the pool. Without a pool it grows in place.
        /// </summary>
        public void EnsureCapacity(int minCapacity, MeshLayerPool pool)
        {
            if (minCapacity <= Vertices.Length)
                return;

            if (pool == null)
            {
                EnsureCapacity(minCapacity);

                return;
            }

            int newSize = Vertices.Length;
            while (newSize < minCapacity)
                newSize *= 2;

            Exchange(pool, newSize);
        }

        /// <summary>
        /// <see cref="SoftReset()"/>, then a layer above <paramref name="keepCapacity"/> sprites hands its arrays and vertex buffer
        /// to <paramref name="pool"/> for a set of <paramref name="keepCapacity"/> (meshlayer-growth: a pooled chunk no longer
        /// keeps the arrays of the busiest chunk it ever held).
        /// </summary>
        public void SoftReset(MeshLayerPool pool, int keepCapacity)
        {
            SoftReset();

            if (pool != null && Vertices.Length > keepCapacity)
            {
                Exchange(pool, keepCapacity);
            }
        }

        // swaps this layer's set (arrays and vertex buffer) with a pooled set of exactly `size` sprites, or a new one
        private void Exchange(MeshLayerPool pool, int size)
        {
            MeshLayerPool.Set set = pool.Take(size) ?? MeshLayerPool.Set.Create(size);

            (Vertices, set.Vertices) = (set.Vertices, Vertices);
            (Textures, set.Textures) = (set.Textures, Textures);
            (_hueKeys, set.HueKeys) = (set.HueKeys, _hueKeys);
            (_visBits, set.VisBits) = (set.VisBits, _visBits);
            (_prevVisBits, set.PrevVisBits) = (set.PrevVisBits, _prevVisBits);
            (_visibleIndexData, set.IndexData) = (set.IndexData, _visibleIndexData);
            (VertexBuffer, set.VertexBuffer) = (set.VertexBuffer, VertexBuffer);

            // the new arrays hold no sprites this layer knows of: the next BuildVisibleIndices rebuilds
            _visibilityDirty = true;
            _alphaDirty = false;
            _alphaDirtyCount = 0;

            pool.Return(set);
        }

        /// <summary>
        /// Resets all visibility bits. Called each frame before AddTileToRenderList marks visible sprites.
        /// </summary>
        public void ResetVisibility()
        {
            int words = (Count + 63) >> 6;
            ulong[] bits = _visBits;

            for (int w = 0; w < words; w++)
            {
                bits[w] = 0;
            }
        }

        /// <summary>
        /// Marks a sprite as visible this frame and updates its vertex alpha.
        /// AlphaHue=0 means "not yet processed" → treat as fully opaque.
        /// AlphaHue=255 means fully opaque. Any other value is a fade in progress.
        /// </summary>
        public void SetVisible(int index, byte alphaHue, bool circletrans = false)
        {
            _visBits[index >> 6] |= 1UL << index;

            float alpha;
            if (circletrans)
            {
                // Alpha > 1.0 signals the shader to apply circle of transparency
                alpha = (alphaHue == 0 ? 1f : alphaHue / 255f) + 1f;
            }
            else if (alphaHue != 0 && alphaHue != 0xFF)
            {
                alpha = alphaHue / 255f;
            }
            else
            {
                return;
            }

            ref var v = ref Vertices[index];
            v.Hue0.Z = alpha;
            v.Hue1.Z = alpha;
            v.Hue2.Z = alpha;
            v.Hue3.Z = alpha;

            if (_alphaDirtyCount >= _alphaDirtyIndices.Length)
                Array.Resize(ref _alphaDirtyIndices, _alphaDirtyIndices.Length * 2);
            _alphaDirtyIndices[_alphaDirtyCount++] = index;

            MarkVertexDirty();
        }

        /// <summary>
        /// Resets vertex alphas back to fully opaque for sprites that were modified.
        /// Only touches indices tracked by SetVisible, avoiding a full scan.
        /// </summary>
        public void ResetAlpha()
        {
            if (_alphaDirtyCount == 0)
                return;

            for (int i = 0; i < _alphaDirtyCount; i++)
            {
                ref var v = ref Vertices[_alphaDirtyIndices[i]];
                v.Hue0.Z = 1f;
                v.Hue1.Z = 1f;
                v.Hue2.Z = 1f;
                v.Hue3.Z = 1f;
            }

            _alphaDirty = true;
            _alphaDirtyCount = 0;
        }

        /// <summary>
        /// Sets the hue (X=hue index, Y=shader type) on a meshed sprite, preserving alpha (Z).
        /// Used per-frame to apply out-of-range color or sync with runtime hue changes.
        /// </summary>
        public void SetHue(int index, float hueX, float hueY)
        {
            _hueKeys[index] = 0;
            WriteHue(index, hueX, hueY);
        }

        /// <summary>
        /// <see cref="SetHue(int, float, float)"/> that records the key the hue was computed from (<see cref="HueKey"/>).
        /// </summary>
        public void SetHue(int index, float hueX, float hueY, long key)
        {
            _hueKeys[index] = key;
            WriteHue(index, hueX, hueY);
        }

        /// <summary>The key of the sprite's last keyed <see cref="SetHue(int, float, float, long)"/>; 0 if none since.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long HueKeyAt(int index) => _hueKeys[index];

        /// <summary>A key for <see cref="SetHue(int, float, float, long)"/>: never 0 for an epoch other than 0.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long HueKey(int epoch, ushort graphic, ushort hue) => ((long)(uint)epoch << 32) | ((long)graphic << 16) | hue;

        /// <summary>This frame's visible index data (6 per visible sprite).</summary>
        public ReadOnlySpan<short> VisibleIndices => _visibleIndexData.AsSpan(0, VisibleSpriteCount * 6);

        /// <summary>The sprite's current vertex hue (X = hue index, Y = shader type).</summary>
        public void GetHue(int index, out float hueX, out float hueY)
        {
            ref var v = ref Vertices[index];
            hueX = v.Hue0.X;
            hueY = v.Hue0.Y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteHue(int index, float hueX, float hueY)
        {
            ref var v = ref Vertices[index];
            if (v.Hue0.X == hueX && v.Hue0.Y == hueY)
                return;

            float alpha = v.Hue0.Z;
            var h = new Vector3(hueX, hueY, alpha);
            v.Hue0 = v.Hue1 = v.Hue2 = v.Hue3 = h;

            MarkVertexDirty();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MarkVertexDirty()
        {
            _alphaDirty = true;
        }

        /// <summary>
        /// Writes a quad at a specific index from a texture atlas source rectangle.
        /// Does not modify Count — caller is responsible for setting Count after all writes.
        /// </summary>
        public void WriteQuadAt(int index, Texture2D texture, Rectangle sourceRect, int posX, int posY,
            Vector3 hue, float depth, float uvInset = 0f)
        {
            ref var vertex = ref Vertices[index];

            float sourceX = (sourceRect.X + uvInset) / (float)texture.Width;
            float sourceY = (sourceRect.Y + uvInset) / (float)texture.Height;
            float sourceW = (sourceRect.Width - uvInset * 2f) / (float)texture.Width;
            float sourceH = (sourceRect.Height - uvInset * 2f) / (float)texture.Height;

            vertex.Position0.X = posX;
            vertex.Position0.Y = posY;
            vertex.Position1.X = posX + sourceRect.Width;
            vertex.Position1.Y = posY;
            vertex.Position2.X = posX;
            vertex.Position2.Y = posY + sourceRect.Height;
            vertex.Position3.X = posX + sourceRect.Width;
            vertex.Position3.Y = posY + sourceRect.Height;

            vertex.Position0.Z = vertex.Position1.Z = vertex.Position2.Z = vertex.Position3.Z = depth;

            vertex.TextureCoordinate0 = new Vector3(sourceX, sourceY, 0);
            vertex.TextureCoordinate1 = new Vector3(sourceW + sourceX, sourceY, 0);
            vertex.TextureCoordinate2 = new Vector3(sourceX, sourceH + sourceY, 0);
            vertex.TextureCoordinate3 = new Vector3(sourceW + sourceX, sourceH + sourceY, 0);

            vertex.Normal0 = vertex.Normal1 = vertex.Normal2 = vertex.Normal3 = new Vector3(0, 0, 1);
            vertex.Hue0 = vertex.Hue1 = vertex.Hue2 = vertex.Hue3 = hue;

            Textures[index] = texture;
        }

        /// <summary>
        /// Uploads all vertex data to the GPU buffer. Creates or resizes as needed.
        /// </summary>
        public unsafe void UploadVertexBuffer(GraphicsDevice graphicsDevice)
        {
            if (Count == 0)
                return;

            int requiredVertices = Count * 4;
            if (VertexBuffer == null || VertexBuffer.IsDisposed || VertexBuffer.VertexCount < requiredVertices)
            {
                VertexBuffer?.Dispose();
                VertexBuffer = new DynamicVertexBuffer(
                    graphicsDevice,
                    PositionNormalTextureColor4.VertexDeclaration,
                    requiredVertices,
                    BufferUsage.WriteOnly
                );
            }

            fixed (PositionNormalTextureColor4* p = &Vertices[0])
            {
                VertexBuffer.SetDataPointerEXT(
                    0,
                    (IntPtr)p,
                    Count * PositionNormalTextureColor4.SIZE_IN_BYTES,
                    SetDataOptions.Discard
                );
            }
        }

        /// <summary>
        /// Builds per-frame index data from the visibility bits. Sprites are already
        /// in texture-sorted order, so the visible subset preserves texture grouping for
        /// optimal draw call batching. Skips rebuild if visibility is unchanged.
        /// </summary>
        public bool BuildVisibleIndices()
        {
            if (Count == 0)
            {
                VisibleSpriteCount = 0;
                VisibleRunCount = 0;
                return false;
            }

            int words = (Count + 63) >> 6;
            ulong[] bits = _visBits;

            if (!_visibilityDirty && bits.AsSpan(0, words).SequenceEqual(_prevVisBits.AsSpan(0, words)))
                return VisibleSpriteCount > 0;

            if (_prevVisBits.Length < bits.Length)
                _prevVisBits = new ulong[bits.Length];
            bits.AsSpan(0, words).CopyTo(_prevVisBits);
            _visibilityDirty = false;

            VisibleSpriteCount = 0;
            VisibleRunCount = 0;

            int maxIndices = Count * 6;
            if (_visibleIndexData.Length < maxIndices)
                _visibleIndexData = new short[maxIndices];

            short[] indexData = _visibleIndexData;
            Texture2D[] textures = Textures;
            Texture2D curTexture = null;
            int runStart = 0;
            int indexPos = 0;

            for (int w = 0; w < words; w++)
            {
                ulong word = bits[w];

                while (word != 0)
                {
                    int i = (w << 6) + BitOperations.TrailingZeroCount(word);
                    word &= word - 1;

                    var tex = textures[i];
                    if (tex != curTexture)
                    {
                        if (curTexture != null && VisibleSpriteCount > runStart)
                            AddRun(curTexture, runStart);
                        curTexture = tex;
                        runStart = VisibleSpriteCount;
                    }

                    short baseVertex = (short)(i * 4);
                    indexData[indexPos++] = baseVertex;
                    indexData[indexPos++] = (short)(baseVertex + 1);
                    indexData[indexPos++] = (short)(baseVertex + 2);
                    indexData[indexPos++] = (short)(baseVertex + 1);
                    indexData[indexPos++] = (short)(baseVertex + 3);
                    indexData[indexPos++] = (short)(baseVertex + 2);

                    VisibleSpriteCount++;
                }
            }

            if (curTexture != null && VisibleSpriteCount > runStart)
                AddRun(curTexture, runStart);

            return VisibleSpriteCount > 0;
        }

        private void AddRun(Texture2D texture, int runStart)
        {
            if (VisibleRunCount >= VisibleRuns.Length)
                Array.Resize(ref VisibleRuns, VisibleRuns.Length * 2);

            VisibleRuns[VisibleRunCount++] = new TextureRun
            {
                Texture = texture,
                Start = runStart,
                Count = VisibleSpriteCount - runStart
            };
        }


        /// <summary>
        /// Uploads the per-frame visible index data to a shared DynamicIndexBuffer.
        /// </summary>
        public unsafe void UploadVisibleIndices(DynamicIndexBuffer indexBuffer)
        {
            if (VisibleSpriteCount == 0)
                return;

            fixed (short* p = &_visibleIndexData[0])
            {
                indexBuffer.SetDataPointerEXT(
                    0,
                    (IntPtr)p,
                    VisibleSpriteCount * 6 * sizeof(short),
                    SetDataOptions.Discard
                );
            }
        }

        /// <summary>
        /// Re-uploads vertex data when alpha/hue has been modified.
        /// Uses full buffer Discard to avoid partial-update driver bugs on Intel GPUs.
        /// </summary>
        public unsafe void FlushAlphaChanges()
        {
            if (!_alphaDirty || Count == 0 || VertexBuffer == null || VertexBuffer.IsDisposed)
                return;

            _alphaDirty = false;

            fixed (PositionNormalTextureColor4* p = &Vertices[0])
            {
                VertexBuffer.SetDataPointerEXT(
                    0,
                    (IntPtr)p,
                    Count * PositionNormalTextureColor4.SIZE_IN_BYTES,
                    SetDataOptions.Discard
                );
            }
        }

        public void Reset()
        {
            Array.Clear(_hueKeys);
            Count = 0;
            VisibleSpriteCount = 0;
            VisibleRunCount = 0;
            _visibilityDirty = true;
            _alphaDirty = false;
            _alphaDirtyCount = 0;
        }

        /// <summary>
        /// Resets all state for reuse from a pool. Keeps GPU buffer and arrays allocated.
        /// </summary>
        public void SoftReset()
        {
            Array.Clear(_hueKeys);
            Count = 0;
            VisibleSpriteCount = 0;
            VisibleRunCount = 0;
            _visibilityDirty = true;
            _alphaDirty = false;
            _alphaDirtyCount = 0;

            // Clear texture references so they can be GC'd if atlas rebuilds
            if (Textures.Length > 0)
                Array.Clear(Textures, 0, Textures.Length);
        }

        public void Dispose()
        {
            VertexBuffer?.Dispose();
            VertexBuffer = null;
        }
    }
}
