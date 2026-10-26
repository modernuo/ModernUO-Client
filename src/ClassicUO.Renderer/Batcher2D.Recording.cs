// SPDX-License-Identifier: BSD-2-Clause

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ClassicUO.Renderer
{
    /// <summary>What <see cref="UltimaBatcher2D.StopRecording"/> found.</summary>
    public enum RecordingResult
    {
        /// <summary>Byte-identical to the pass's last submitted recording: the target still holds these pixels.</summary>
        Same,
        /// <summary>Different (or no valid previous recording): clear the target and <see cref="UltimaBatcher2D.SubmitRecording"/>.</summary>
        Changed,
        /// <summary>Something that can't be recorded ran (a custom effect, a mesh or direct draw): what was recorded was cleared
        /// and drawn, the rest of the pass drew directly. The pass is invalid; nothing to submit.</summary>
        Aborted
    }

    /// <summary>
    /// The last submitted recording of one pass (B8, the UI render cache): sprites, textures and the states of each flush.
    /// Owned by the caller, one per target that keeps its pixels between frames.
    /// </summary>
    public sealed class RecordedPass
    {
        internal UltimaBatcher2D.PositionNormalTextureColor4[] Vertices = new UltimaBatcher2D.PositionNormalTextureColor4[UltimaBatcher2D.MAX_SPRITES];
        internal Texture2D[] Textures = new Texture2D[UltimaBatcher2D.MAX_SPRITES];
        internal List<RecordedSegment> Segments = new();
        internal int SpriteCount;
        internal bool Valid;

        public bool IsValid => Valid;
        public int SpriteCountSubmitted => SpriteCount;
        public int SegmentCount => Segments.Count;

        /// <summary>The next recording compares as changed (the target was recreated, a texture changed, ...).</summary>
        public void Invalidate() => Valid = false;

        /// <summary>
        /// True when two recordings draw the same: the same segments (ranges and states), the same texture references and
        /// byte-identical vertices. Bytes, not floats: -0 and 0 differ (a needless redraw), NaN equals itself.
        /// </summary>
        public static bool RecordingsEqual<T>(
            List<RecordedSegment> segmentsA,
            List<RecordedSegment> segmentsB,
            ReadOnlySpan<UltimaBatcher2D.PositionNormalTextureColor4> verticesA,
            ReadOnlySpan<UltimaBatcher2D.PositionNormalTextureColor4> verticesB,
            ReadOnlySpan<T> texturesA,
            ReadOnlySpan<T> texturesB
        ) where T : class
        {
            if (segmentsA.Count != segmentsB.Count || verticesA.Length != verticesB.Length || texturesA.Length != texturesB.Length ||
                texturesA.Length != verticesA.Length)
            {
                return false;
            }

            for (int i = 0; i < segmentsA.Count; i++)
            {
                if (!segmentsA[i].SameAs(segmentsB[i]))
                {
                    return false;
                }
            }

            for (int i = 0; i < texturesA.Length; i++)
            {
                if (!ReferenceEquals(texturesA[i], texturesB[i]))
                {
                    return false;
                }
            }

            return MemoryMarshal.AsBytes(verticesA).SequenceEqual(MemoryMarshal.AsBytes(verticesB));
        }
    }

    /// <summary>One flush of a recorded pass: its sprite range and every batcher and device state its draws depend on.</summary>
    public struct RecordedSegment
    {
        public int Start, Count;
        public BlendState Blend;
        public DepthStencilState Stencil;
        public SamplerState Sampler;
        public bool ScissorEnabled;
        public Rectangle Scissor;
        public Matrix Transform;
        /// <summary>The device viewport at the flush (origin, size and depth range): applied again before the replayed draw.</summary>
        public Viewport Viewport;
        public float Brightlight, CircleRadius;
        /// <summary>A Begin() ran since the previous flush: the original applied the effect again there.</summary>
        public bool AfterBegin;

        public readonly bool SameAs(in RecordedSegment o) =>
            Start == o.Start && Count == o.Count && Blend == o.Blend && Stencil == o.Stencil && Sampler == o.Sampler &&
            ScissorEnabled == o.ScissorEnabled && Scissor == o.Scissor && Transform == o.Transform && SameViewport(Viewport, o.Viewport) &&
            Brightlight.Equals(o.Brightlight) && CircleRadius.Equals(o.CircleRadius) && AfterBegin == o.AfterBegin;

        public static bool SameViewport(in Viewport a, in Viewport b) =>
            a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height && a.MinDepth.Equals(b.MinDepth) && a.MaxDepth.Equals(b.MaxDepth);
    }

    public sealed unsafe partial class UltimaBatcher2D
    {
        // ---- B8: recorded passes ----
        //
        // While recording, Flush() doesn't draw: it closes a segment (the queued sprites plus the states the draw would use)
        // and keeps the sprites. StopRecording() compares everything with the pass's previous submission. Same: the target
        // (which keeps its contents) already holds exactly these pixels, so nothing is drawn. Changed: the caller clears the
        // target and SubmitRecording() replays the segments in order with their states, the same FNA calls Flush() makes.
        //
        // What a segment can't see, the caller checks: the target's identity and size, the textures bound outside the batcher
        // (hue samplers) and texture contents changed in place (TextureUploads.Epoch).

        private RecordedPass _recordPass;
        private readonly List<RecordedSegment> _recSegments = new();
        private int _recSegmentStart;
        private bool _recBegun, _recAborted;
        private Color _recClearColor;

        public bool IsRecording => _recordPass != null;

        /// <summary>Start recording into <paramref name="pass"/>. The target it will be compared for must already be bound;
        /// <paramref name="clearColor"/> is what the caller clears it to before a submission.</summary>
        public void BeginRecording(RecordedPass pass, Color clearColor)
        {
            if (_recordPass != null)
            {
                throw new InvalidOperationException("already recording");
            }

            // anything queued belongs to an earlier pass
            Flush();

            _recordPass = pass ?? throw new ArgumentNullException(nameof(pass));
            _recSegments.Clear();
            _recSegmentStart = 0;
            _recBegun = _started;
            _recAborted = false;
            _recClearColor = clearColor;
        }

        public RecordingResult StopRecording()
        {
            RecordedPass pass = _recordPass;

            if (pass == null)
            {
                if (_recAborted)
                {
                    _recAborted = false;

                    return RecordingResult.Aborted;
                }

                throw new InvalidOperationException("not recording");
            }

            RecordSegment();
            _recordPass = null;

            return Matches(pass) ? RecordingResult.Same : RecordingResult.Changed;
        }

        /// <summary>After <see cref="RecordingResult.Same"/>: drop the recording (the target is left untouched).</summary>
        public void DropRecording()
        {
            _recSegments.Clear();
            _numSprites = 0;
        }

        /// <summary>After <see cref="RecordingResult.Changed"/>, with the target bound and cleared: draw the recording and keep
        /// it as the pass's previous one.</summary>
        public void SubmitRecording(RecordedPass pass)
        {
            Replay(_recSegments, _vertexInfo, _textureInfo, 0);

            (pass.Vertices, _vertexInfo) = (_vertexInfo, pass.Vertices);
            (pass.Textures, _textureInfo) = (_textureInfo, pass.Textures);
            pass.Segments.Clear();
            pass.Segments.AddRange(_recSegments);
            pass.SpriteCount = _numSprites;
            pass.Valid = true;

            _recSegments.Clear();
            _numSprites = 0;
        }

        /// <summary>Draw the pass's previous submission again (from segment <paramref name="fromSegment"/>) into the bound
        /// target: the UI cache check's reference image.</summary>
        public void ReplayPass(RecordedPass pass, int fromSegment = 0)
        {
            if (pass.Valid)
            {
                Replay(pass.Segments, pass.Vertices, pass.Textures, fromSegment);
            }
        }

        private bool Matches(RecordedPass pass)
        {
            int n = _numSprites;

            return pass.Valid && pass.SpriteCount == n && RecordedPass.RecordingsEqual<Texture2D>(
                _recSegments, pass.Segments,
                _vertexInfo.AsSpan(0, n), pass.Vertices.AsSpan(0, n),
                _textureInfo.AsSpan(0, n), pass.Textures.AsSpan(0, n)
            );
        }

        // Flush() while recording: close the open segment with the states its draw would have used.
        private void RecordSegment()
        {
            int count = _numSprites - _recSegmentStart;

            if (count <= 0)
            {
                return;
            }

            _recSegments.Add(new RecordedSegment
            {
                Start = _recSegmentStart,
                Count = count,
                Blend = _blendState,
                Stencil = _stencil,
                Sampler = _sampler,
                ScissorEnabled = _rasterizerState.ScissorTestEnable,
                Scissor = GraphicsDevice.ScissorRectangle,
                Transform = _transformMatrix,
                Viewport = GraphicsDevice.Viewport,
                Brightlight = _brightlight,
                CircleRadius = _circleRadius,
                AfterBegin = _recBegun
            });

            _recBegun = false;
            _recSegmentStart = _numSprites;
        }

        private void Replay(List<RecordedSegment> segments, PositionNormalTextureColor4[] vertices, Texture2D[] textures, int fromSegment)
        {
            if (fromSegment >= segments.Count)
            {
                return;
            }

            BlendState blend = _blendState;
            DepthStencilState stencil = _stencil;
            SamplerState sampler = _sampler;
            Matrix transform = _transformMatrix;
            bool scissorEnabled = _rasterizerState.ScissorTestEnable;
            Rectangle scissor = GraphicsDevice.ScissorRectangle;
            Viewport viewport = GraphicsDevice.Viewport;
            float brightlight = _brightlight, radius = _circleRadius;

            for (int i = fromSegment; i < segments.Count; i++)
            {
                RecordedSegment seg = segments[i];

                _blendState = seg.Blend;
                _stencil = seg.Stencil;
                _sampler = seg.Sampler;
                _transformMatrix = seg.Transform;
                _rasterizerState.ScissorTestEnable = seg.ScissorEnabled;

                if (!RecordedSegment.SameViewport(GraphicsDevice.Viewport, seg.Viewport))
                {
                    GraphicsDevice.Viewport = seg.Viewport;
                }

                if (GraphicsDevice.ScissorRectangle != seg.Scissor)
                {
                    GraphicsDevice.ScissorRectangle = seg.Scissor;
                }

                if (!float.IsNaN(seg.Brightlight))
                {
                    SetBrightlight(seg.Brightlight);
                }

                if (!float.IsNaN(seg.CircleRadius))
                {
                    SetCircleOfTransparencyRadius(seg.CircleRadius);
                }

                if (seg.AfterBegin)
                {
                    _effectDirty = true;
                }

                DrawRange(vertices, textures, seg.Start, seg.Count);
            }

            _blendState = blend;
            _stencil = stencil;
            _sampler = sampler;
            _transformMatrix = transform;
            _rasterizerState.ScissorTestEnable = scissorEnabled;

            if (!RecordedSegment.SameViewport(GraphicsDevice.Viewport, viewport))
            {
                GraphicsDevice.Viewport = viewport;
            }

            if (GraphicsDevice.ScissorRectangle != scissor)
            {
                GraphicsDevice.ScissorRectangle = scissor;
            }

            if (!float.IsNaN(brightlight))
            {
                SetBrightlight(brightlight);
            }

            if (!float.IsNaN(radius))
            {
                SetCircleOfTransparencyRadius(radius);
            }
        }

        private void AbortRecordingIfActive()
        {
            if (_recordPass != null)
            {
                AbortRecording();
            }
        }

        // Something that draws immediately runs while recording: clear the target and draw the closed segments now, keep the
        // open (still queued) sprites queued, and continue without recording, as the original order requires.
        private void AbortRecording()
        {
            RecordedPass pass = _recordPass;
            _recordPass = null;
            _recAborted = true;
            pass.Valid = false;

            // the replay binds the batcher's own buffers: a direct draw that already bound its mesh buffers gets them back
            IndexBuffer indices = GraphicsDevice.Indices;
            VertexBufferBinding[] vertexBuffers = GraphicsDevice.GetVertexBuffers();

            GraphicsDevice.Clear(_recClearColor);
            Replay(_recSegments, _vertexInfo, _textureInfo, 0);

            if (_recSegments.Count > 0)
            {
                if (vertexBuffers.Length > 0)
                {
                    GraphicsDevice.SetVertexBuffers(vertexBuffers);
                }

                GraphicsDevice.Indices = indices;
            }

            int open = _numSprites - _recSegmentStart;

            if (open > 0 && _recSegmentStart > 0)
            {
                Array.Copy(_vertexInfo, _recSegmentStart, _vertexInfo, 0, open);
                Array.Copy(_textureInfo, _recSegmentStart, _textureInfo, 0, open);
            }

            _numSprites = Math.Max(0, open);
            _recSegments.Clear();
            _recSegmentStart = 0;
        }
    }
}
