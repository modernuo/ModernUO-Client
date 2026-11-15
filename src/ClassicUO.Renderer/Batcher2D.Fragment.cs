// SPDX-License-Identifier: BSD-2-Clause
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace ClassicUO.Renderer
{
    // A subrange of an active recording. Only audited, state-balanced UI trees may retain one.
    public sealed class SpriteFragment
    {
        internal UltimaBatcher2D.PositionNormalTextureColor4[] Vertices = Array.Empty<UltimaBatcher2D.PositionNormalTextureColor4>();
        internal Texture2D[] Textures = Array.Empty<Texture2D>();
        internal readonly List<RecordedSegment> Segments = new();
        internal RecordedSegment Environment, EndEnvironment;
        public int Count { get; internal set; }
        public bool Valid { get; internal set; }
        public void Invalidate() => Valid = false;
        public bool TexturesAlive()
        {
            for (int i = 0; i < Count; i++)
                if (Textures[i] == null || Textures[i].IsDisposed) return false;
            return Valid;
        }
        public bool SameAs(SpriteFragment other) => Valid && other.Valid && Count == other.Count &&
            EndEnvironment.SameAs(other.EndEnvironment) &&
            RecordedPass.RecordingsEqual<Texture2D>(Segments, other.Segments, Vertices.AsSpan(0, Count),
                other.Vertices.AsSpan(0, other.Count), Textures.AsSpan(0, Count), other.Textures.AsSpan(0, other.Count));
    }

    public sealed unsafe partial class UltimaBatcher2D
    {
        public (int Sprite, int Segment, bool Begun, RecordedSegment Environment) BeginFragment()
        {
            if (!IsRecording) throw new InvalidOperationException("fragments require an outer recording");
            RecordSegment();
            return (_numSprites, _recSegments.Count, _recBegun, FragmentEnvironment());
        }

        public void EndFragment(SpriteFragment fragment, (int Sprite, int Segment, bool Begun, RecordedSegment Environment) mark)
        {
            RecordSegment();
            int count = _numSprites - mark.Sprite;
            if (fragment.Vertices.Length < count)
            {
                int capacity = Math.Max(count, 16);
                Array.Resize(ref fragment.Vertices, capacity);
                Array.Resize(ref fragment.Textures, capacity);
            }
            if (count < fragment.Count) Array.Clear(fragment.Textures, count, fragment.Count - count);
            _vertexInfo.AsSpan(mark.Sprite, count).CopyTo(fragment.Vertices);
            _textureInfo.AsSpan(mark.Sprite, count).CopyTo(fragment.Textures);
            fragment.Segments.Clear();
            for (int i = mark.Segment; i < _recSegments.Count; i++)
            {
                var segment = _recSegments[i];
                segment.Start -= mark.Sprite;
                // Begin belongs to the outer pass, not to a retained fragment.
                segment.AfterBegin = false;
                fragment.Segments.Add(segment);
            }
            fragment.Environment = mark.Environment;
            fragment.EndEnvironment = FragmentEnvironment();
            fragment.Count = count;
            fragment.Valid = true;
        }

        private RecordedSegment FragmentEnvironment() => new()
        {
            Blend = _blendState, Stencil = _stencil, Sampler = _sampler,
            ScissorEnabled = _rasterizerState.ScissorTestEnable, Scissor = GraphicsDevice.ScissorRectangle,
            Transform = _transformMatrix, Viewport = GraphicsDevice.Viewport,
            Brightlight = _brightlight, CircleRadius = _circleRadius
        };

        public bool FragmentEnvironmentMatches(SpriteFragment fragment)
        {
            var current = FragmentEnvironment();
            return fragment.Environment.SameAs(current);
        }

        public void AppendFragment(SpriteFragment fragment)
        {
            RecordSegment();
            int start = _numSprites;
            while (_vertexInfo.Length < start + fragment.Count)
            {
                int capacity = _vertexInfo.Length + MAX_SPRITES;
                Array.Resize(ref _vertexInfo, capacity);
                Array.Resize(ref _textureInfo, capacity);
            }
            fragment.Vertices.AsSpan(0, fragment.Count).CopyTo(_vertexInfo.AsSpan(start));
            fragment.Textures.AsSpan(0, fragment.Count).CopyTo(_textureInfo.AsSpan(start));
            for (int i = 0; i < fragment.Segments.Count; i++)
            {
                var segment = fragment.Segments[i];
                segment.Start += start;
                if (i == 0) segment.AfterBegin = _recBegun;
                _recSegments.Add(segment);
            }
            if (fragment.Count > 0) _recBegun = false;
            _numSprites += fragment.Count;
            _recSegmentStart = _numSprites;
            // ResizePic balances its scissor stack, but ClipEnd also changes the device's scissor state.
            var end = fragment.EndEnvironment;
            _blendState = end.Blend; _stencil = end.Stencil; _sampler = end.Sampler;
            _transformMatrix = end.Transform; _brightlight = end.Brightlight; _circleRadius = end.CircleRadius;
            _rasterizerState.ScissorTestEnable = end.ScissorEnabled;
            GraphicsDevice.RasterizerState.ScissorTestEnable = end.ScissorEnabled;
            if (GraphicsDevice.ScissorRectangle != end.Scissor) GraphicsDevice.ScissorRectangle = end.Scissor;
        }

        // Checker-only callers restore the queue after an audited side-effect-free shadow walk.
        public void RewindFragment((int Sprite, int Segment, bool Begun, RecordedSegment Environment) mark)
        {
            Array.Clear(_textureInfo, mark.Sprite, _numSprites - mark.Sprite);
            _numSprites = _recSegmentStart = mark.Sprite;
            _recBegun = mark.Begun;
            _recSegments.RemoveRange(mark.Segment, _recSegments.Count - mark.Segment);
        }
    }
}
