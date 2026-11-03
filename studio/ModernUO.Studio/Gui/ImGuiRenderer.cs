using Hexa.NET.ImGui;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ModernUO.Studio.Gui
{
    /// <summary>
    /// Dear ImGui on FNA. Draws ImDrawData with a BasicEffect and owns ImGui's own textures through the 1.92
    /// RendererHasTextures protocol, so the font atlas can grow at run time. Textures that belong to others (the client's
    /// atlases) are bound by id through <see cref="Bind"/>.
    /// </summary>
    internal sealed unsafe class ImGuiRenderer : IDisposable
    {
        private struct Slot
        {
            public Texture2D Texture;
            public bool Owned;
            public bool Linear;
        }

        private static readonly VertexDeclaration _declaration = new VertexDeclaration(
            sizeof(ImDrawVert),
            new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.Position, 0),
            new VertexElement(8, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(16, VertexElementFormat.Color, VertexElementUsage.Color, 0)
        );

        private readonly GraphicsDevice _device;
        private readonly BasicEffect _effect;
        private readonly RasterizerState _rasterizer;
        // id 0 is "no texture"; ids are slot indices, reused after a texture is released
        private readonly List<Slot> _slots = new() { default };
        private readonly Dictionary<Texture2D, int> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly Stack<int> _free = new();
        private DynamicVertexBuffer _vertices;
        private DynamicIndexBuffer _indices;

        public ImGuiRenderer(GraphicsDevice device)
        {
            _device = device;
            _effect = new BasicEffect(device)
            {
                TextureEnabled = true,
                VertexColorEnabled = true,
                World = Matrix.Identity,
                View = Matrix.Identity
            };
            _rasterizer = new RasterizerState
            {
                CullMode = CullMode.None,
                ScissorTestEnable = true,
                FillMode = FillMode.Solid
            };

            var io = ImGui.GetIO();
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures;

            var platform = ImGui.GetPlatformIO();
            platform.RendererTextureMaxWidth = 8192;
            platform.RendererTextureMaxHeight = 8192;
        }

        /// <summary>The ImGui texture reference of a texture someone else owns. Pixel art samples with point filtering.</summary>
        public ImTextureRef Bind(Texture2D texture, bool linear = false)
        {
            if (texture == null)
            {
                return new ImTextureRef(null, ImTextureID.Null);
            }

            if (!_ids.TryGetValue(texture, out int id))
            {
                id = AddSlot(new Slot { Texture = texture, Linear = linear });
                _ids[texture] = id;
            }

            return new ImTextureRef(null, new ImTextureID((ulong)id));
        }

        /// <summary>Forgets a bound texture (call before disposing it, or its id keeps it alive).</summary>
        public void Unbind(Texture2D texture)
        {
            if (texture != null && _ids.Remove(texture, out int id))
            {
                FreeSlot(id);
            }
        }

        private int AddSlot(Slot slot)
        {
            if (_free.TryPop(out int id))
            {
                _slots[id] = slot;

                return id;
            }

            _slots.Add(slot);

            return _slots.Count - 1;
        }

        private void FreeSlot(int id)
        {
            _slots[id] = default;
            _free.Push(id);
        }

        public void Render(ImDrawDataPtr drawData)
        {
            UpdateTextures();

            if (drawData.CmdListsCount == 0 || drawData.TotalVtxCount == 0)
            {
                return;
            }

            UploadBuffers(drawData);

            var viewport = _device.Viewport;
            var displayPos = drawData.DisplayPos;
            var displaySize = drawData.DisplaySize;
            var scale = drawData.FramebufferScale;

            _effect.Projection = Matrix.CreateOrthographicOffCenter(
                displayPos.X, displayPos.X + displaySize.X, displayPos.Y + displaySize.Y, displayPos.Y, -1f, 1f);

            var oldScissor = _device.ScissorRectangle;
            _device.BlendFactor = Color.White;
            _device.BlendState = BlendState.NonPremultiplied;
            _device.DepthStencilState = DepthStencilState.None;
            _device.RasterizerState = _rasterizer;
            _device.SetVertexBuffer(_vertices);
            _device.Indices = _indices;

            int vtxBase = 0, idxBase = 0;

            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                ImDrawListPtr list = drawData.CmdLists[n];

                for (int c = 0; c < list.CmdBuffer.Size; c++)
                {
                    ImDrawCmd* cmd = &((ImDrawCmd*)list.CmdBuffer.Data)[c];

                    if (cmd->UserCallback != null || cmd->ElemCount == 0)
                    {
                        continue;
                    }

                    var clip = cmd->ClipRect;
                    int x0 = (int)Math.Max(0, (clip.X - displayPos.X) * scale.X);
                    int y0 = (int)Math.Max(0, (clip.Y - displayPos.Y) * scale.Y);
                    int x1 = (int)Math.Min(viewport.Width, (clip.Z - displayPos.X) * scale.X);
                    int y1 = (int)Math.Min(viewport.Height, (clip.W - displayPos.Y) * scale.Y);

                    if (x1 <= x0 || y1 <= y0)
                    {
                        continue;
                    }

                    ulong id = cmd->GetTexID().Handle;
                    Slot slot = id < (ulong)_slots.Count ? _slots[(int)id] : default;

                    if (slot.Texture == null || slot.Texture.IsDisposed)
                    {
                        continue;
                    }

                    _device.ScissorRectangle = new Rectangle(x0, y0, x1 - x0, y1 - y0);
                    _device.SamplerStates[0] = slot.Linear ? SamplerState.LinearClamp : SamplerState.PointClamp;
                    _effect.Texture = slot.Texture;
                    _effect.CurrentTechnique.Passes[0].Apply();

                    _device.DrawIndexedPrimitives(
                        PrimitiveType.TriangleList,
                        vtxBase + (int)cmd->VtxOffset,
                        0,
                        list.VtxBuffer.Size - (int)cmd->VtxOffset,
                        idxBase + (int)cmd->IdxOffset,
                        (int)cmd->ElemCount / 3
                    );
                }

                vtxBase += list.VtxBuffer.Size;
                idxBase += list.IdxBuffer.Size;
            }

            _device.ScissorRectangle = oldScissor;
        }

        private void UploadBuffers(ImDrawDataPtr drawData)
        {
            if (_vertices == null || _vertices.VertexCount < drawData.TotalVtxCount)
            {
                _vertices?.Dispose();
                _vertices = new DynamicVertexBuffer(_device, _declaration, (int)(drawData.TotalVtxCount * 1.5f), BufferUsage.WriteOnly);
            }

            if (_indices == null || _indices.IndexCount < drawData.TotalIdxCount)
            {
                _indices?.Dispose();
                _indices = new DynamicIndexBuffer(_device, IndexElementSize.SixteenBits, (int)(drawData.TotalIdxCount * 1.5f), BufferUsage.WriteOnly);
            }

            int vtxOffset = 0, idxOffset = 0;

            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                ImDrawListPtr list = drawData.CmdLists[n];
                int vtxBytes = list.VtxBuffer.Size * sizeof(ImDrawVert);
                int idxBytes = list.IdxBuffer.Size * sizeof(ushort);

                // the first list discards the buffer contents (the GPU may still read last frame's), the rest append
                var options = n == 0 ? SetDataOptions.Discard : SetDataOptions.NoOverwrite;
                _vertices.SetDataPointerEXT(vtxOffset, (IntPtr)list.VtxBuffer.Data, vtxBytes, options);
                _indices.SetDataPointerEXT(idxOffset, (IntPtr)list.IdxBuffer.Data, idxBytes, options);

                vtxOffset += vtxBytes;
                idxOffset += idxBytes;
            }
        }

        private void UpdateTextures()
        {
            var textures = ImGui.GetPlatformIO().Textures;

            for (int i = 0; i < textures.Size; i++)
            {
                ImTextureDataPtr tex = textures.Data[i];

                switch (tex.Status)
                {
                    case ImTextureStatus.WantCreate:
                    {
                        var texture = new Texture2D(_device, tex.Width, tex.Height, false, SurfaceFormat.Color);
                        Upload(texture, tex);
                        int id = AddSlot(new Slot { Texture = texture, Owned = true, Linear = true });
                        tex.SetTexID(new ImTextureID((ulong)id));
                        tex.SetStatus(ImTextureStatus.Ok);

                        break;
                    }

                    case ImTextureStatus.WantUpdates:
                    {
                        int id = (int)tex.GetTexID().Handle;

                        if (id > 0 && id < _slots.Count && _slots[id].Texture != null)
                        {
                            // the atlas is small and changes rarely (new glyphs): one whole upload beats a pitch-aware sub-rect copy
                            Upload(_slots[id].Texture, tex);
                        }

                        tex.SetStatus(ImTextureStatus.Ok);

                        break;
                    }

                    case ImTextureStatus.WantDestroy when tex.UnusedFrames > 0:
                    {
                        int id = (int)tex.GetTexID().Handle;

                        if (id > 0 && id < _slots.Count && _slots[id].Owned)
                        {
                            _slots[id].Texture?.Dispose();
                            FreeSlot(id);
                        }

                        tex.SetTexID(ImTextureID.Null);
                        tex.SetStatus(ImTextureStatus.Destroyed);

                        break;
                    }
                }
            }
        }

        private static void Upload(Texture2D texture, ImTextureDataPtr tex)
        {
            // ImTextureFormat.Rgba32 is R,G,B,A in memory, the layout of SurfaceFormat.Color
            texture.SetDataPointerEXT(0, null, (IntPtr)tex.GetPixels(), tex.GetSizeInBytes());
        }

        public void Dispose()
        {
            foreach (var slot in _slots)
            {
                if (slot.Owned)
                {
                    slot.Texture?.Dispose();
                }
            }

            _slots.Clear();
            _ids.Clear();
            _vertices?.Dispose();
            _indices?.Dispose();
            _effect.Dispose();
            _rasterizer.Dispose();
        }
    }
}
