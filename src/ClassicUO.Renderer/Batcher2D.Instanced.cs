// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer
{
    // legacy_instanced_sprites: sprites the quad path would upload as 192-byte quads go up as one LegacySpriteInstance each and
    // are expanded by HueInstancedTechnique's vertex shader (IsometricWorld.fx VertexShaderInstanced = LegacySpriteInstance.Expand).
    // Only one of the two queues holds sprites at a time: a call that takes the other path flushes first, so the device sees
    // the sprites in submission order, and the instance queue splits into draw calls on texture changes as the quad queue does.
    public sealed unsafe partial class UltimaBatcher2D
    {
        internal enum PathKind : byte
        {
            None,
            Quad,
            Instance
        }

        private static readonly VertexDeclaration _cornerDeclaration = new VertexDeclaration
        (
            sizeof(float) * 2,
            new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.Position, 0)
        );

        private LegacySpriteInstance[] _instanceInfo;
        private Texture2D[] _instanceTextures;
        private int _numInstances;
        private int _instanceBufferPosition;
        private DynamicVertexBuffer _instanceBuffer;
        private VertexBuffer _cornerBuffer;
        private readonly VertexBufferBinding[] _instanceBindings = new VertexBufferBinding[2];

        /// <summary>
        /// legacy_instanced_sprites: the caller asks for the instanced sprite path. It is used only when
        /// <see cref="SupportsInstancing"/> is also true; off, the batcher behaves as before.
        /// </summary>
        public bool InstancedSprites { get; set; }

        /// <summary>Whether the device draws instanced primitives, probed once when the batcher is created.</summary>
        public bool SupportsInstancing { get; private set; }

        // FrameStats (CollectFrameStats): sprites uploaded as instances and as quads, flushes forced by a path change, and the
        // instance bytes uploaded (also counted in FrameUploadBytes, as the instances are in FrameUploadSprites)
        public static int FrameInstancedSprites, FrameQuadSprites, FramePathSwitches, FrameInstanceBytes;

        private void InitInstancing()
        {
            // FNA's DrawInstancedPrimitives throws when FNA3D reports no hardware instancing; FNA asks per draw but does not
            // expose the answer, so ask FNA3D the same question once. The answer is fixed for the device's lifetime.
            try
            {
                SupportsInstancing = GpuInterop.SupportsHardwareInstancing(GraphicsDevice);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                SupportsInstancing = false;
            }

            if (!SupportsInstancing)
            {
                Log.Info("Legacy batcher: the device has no hardware instancing; sprites use the quad path only");

                return;
            }

            _instanceInfo = new LegacySpriteInstance[MAX_SPRITES];
            _instanceTextures = new Texture2D[MAX_SPRITES];
            _instanceBuffer = new DynamicVertexBuffer(GraphicsDevice, LegacySpriteInstance.VertexDeclaration, MAX_SPRITES, BufferUsage.WriteOnly);

            // the corners in the static index buffer's order (_cornerOffsetX/Y, GenerateIndexArray)
            var corners = new Vector2[4];

            for (int i = 0; i < 4; i++)
            {
                corners[i] = new Vector2(_cornerOffsetX[i], _cornerOffsetY[i]);
            }

            _cornerBuffer = new VertexBuffer(GraphicsDevice, _cornerDeclaration, 4, BufferUsage.WriteOnly);
            _cornerBuffer.SetData(corners);
        }

        private void DisposeInstancing()
        {
            _instanceInfo = null;
            _instanceTextures = null;
            _instanceBuffer?.Dispose();
            _cornerBuffer?.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static PathKind PendingPath(int quads, int instances)
        {
            return instances > 0 ? PathKind.Instance : quads > 0 ? PathKind.Quad : PathKind.None;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool NeedsFlushForPath(PathKind pending, PathKind next)
        {
            return pending != PathKind.None && pending != next;
        }

        /// <summary>
        /// The path a sprite call may take: instances only when requested and supported, and not where the quad's vertices are
        /// patched, recorded, dropped or drawn with another effect (geo mode, a recording, suppression, a custom effect, a capture).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static PathKind ChoosePath(bool requested, bool supported, bool geo, bool customEffect, bool recording, bool suppressed, bool capture)
        {
            return requested && supported && !geo && !customEffect && !recording && !suppressed && !capture ? PathKind.Instance : PathKind.Quad;
        }

        /// <summary>The end of the run of equal textures that starts at <paramref name="start"/>: one draw call each.</summary>
        internal static int RunEnd(ReadOnlySpan<Texture2D> textures, int start)
        {
            Texture2D texture = textures[start];
            int end = start + 1;

            while (end < textures.Length && textures[end] == texture)
            {
                end++;
            }

            return end;
        }

        internal static int CountRuns(ReadOnlySpan<Texture2D> textures)
        {
            int runs = 0;

            for (int i = 0; i < textures.Length; i = RunEnd(textures, i))
            {
                runs++;
            }

            return runs;
        }

        private bool InstancePathOn
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ChoosePath(InstancedSprites, SupportsInstancing, _geoOn, _customEffect != null, _recordPass != null, _suppress, Capture != null) == PathKind.Instance;
        }

        // a quad is about to be queued: pending instances are drawn first
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureQuadPath()
        {
            if (_numInstances != 0)
            {
                SwitchPath();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SwitchPath()
        {
            if (CollectFrameStats)
            {
                FramePathSwitches++;
            }

            Flush();
        }

        private void QueueInstance(Texture2D texture, in LegacySpriteInstance instance)
        {
            EnsureStarted();

            if (NeedsFlushForPath(PendingPath(_numSprites, _numInstances), PathKind.Instance))
            {
                SwitchPath();
            }

            if (_numInstances >= _instanceInfo.Length)
            {
                int newMax = _instanceInfo.Length + MAX_SPRITES;
                Array.Resize(ref _instanceInfo, newMax);
                Array.Resize(ref _instanceTextures, newMax);
            }

            _instanceInfo[_numInstances] = instance;
            _instanceTextures[_numInstances] = texture;
            ++_numInstances;
#if CUO_DEVTOOLS
            GeneratedSprites++;
#endif
        }

        // Draws the instance queue: one upload per MAX_SPRITES, one DrawInstancedPrimitives per run of equal textures. The
        // states are the quad path's (ApplyDeviceStates); only the technique differs, and the normal pass and the quad vertex
        // buffer are bound again afterwards, so a direct draw that follows finds the device as a quad flush leaves it.
        private void FlushInstances()
        {
            if (_numInstances == 0)
            {
                return;
            }

            long t0 = CollectFrameStats ? Stopwatch.GetTimestamp() : 0;

            ApplyDeviceStates();
            GraphicsDevice.Indices = _indexBuffer;
            _basicUOEffect.ApplyInstanced();
            EffectApplies++;

            _instanceBindings[0] = new VertexBufferBinding(_cornerBuffer, 0, 0);

            int arrayOffset = 0;
            int remaining = _numInstances;

            while (remaining > 0)
            {
                ++FlushesDone;

                if (CollectFrameStats)
                {
                    FrameFlushes++;
                }

                int batchSize = Math.Min(remaining, MAX_SPRITES);
                int baseOff = UpdateInstanceBuffer(arrayOffset, batchSize);
                ReadOnlySpan<Texture2D> textures = _instanceTextures.AsSpan(arrayOffset, batchSize);

                for (int start = 0; start < batchSize;)
                {
                    int end = RunEnd(textures, start);

                    if (start > 0)
                    {
                        ++TextureSwitches;
                    }

                    DrawInstances(textures[start], baseOff + start, end - start);
                    start = end;
                }

                remaining -= batchSize;
                arrayOffset += batchSize;
            }

            Array.Clear(_instanceTextures, 0, _numInstances);
            _numInstances = 0;

            _basicUOEffect.RestoreTechnique();
            _effectDirty = true;
            ApplyEffect();
            GraphicsDevice.SetVertexBuffer(_vertexBuffer);

            if (CollectFrameStats)
            {
                FrameGpuTicks += Stopwatch.GetTimestamp() - t0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DrawInstances(Texture2D texture, int firstInstance, int count)
        {
            if (CollectFrameStats)
            {
                FrameDrawCalls++;
            }

            UsePage(texture);
            GraphicsDevice.Textures[0] = texture;

            _instanceBindings[1] = new VertexBufferBinding(_instanceBuffer, firstInstance, 1);
            GraphicsDevice.SetVertexBuffers(_instanceBindings);
            GraphicsDevice.DrawInstancedPrimitives(PrimitiveType.TriangleList, 0, 0, 4, 0, 2, count);
        }

        private int UpdateInstanceBuffer(int start, int count)
        {
            int offset;
            SetDataOptions hint;

            if (_instanceBufferPosition + count > MAX_SPRITES)
            {
                offset = 0;
                hint = SetDataOptions.Discard;
            }
            else
            {
                offset = _instanceBufferPosition;
                hint = SetDataOptions.NoOverwrite;
            }

            fixed (LegacySpriteInstance* p = &_instanceInfo[start])
            {
                _instanceBuffer.SetDataPointerEXT
                (
                    offset * LegacySpriteInstance.SIZE_IN_BYTES,
                    (IntPtr)p,
                    count * LegacySpriteInstance.SIZE_IN_BYTES,
                    hint
                );
            }

            _instanceBufferPosition = offset + count;

            if (CollectFrameStats)
            {
                int bytes = count * LegacySpriteInstance.SIZE_IN_BYTES;
                FrameUploadCalls++;
                FrameUploadBytes += bytes;
                FrameUploadSprites += count;
                FrameInstancedSprites += count;
                FrameInstanceBytes += bytes;
            }

            return offset;
        }
    }
}
