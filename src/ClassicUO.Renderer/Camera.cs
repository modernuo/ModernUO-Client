// SPDX-License-Identifier: BSD-2-Clause

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Runtime.CompilerServices;

namespace ClassicUO.Renderer
{
    public class Camera
    {
        private const float MAX_PEEK_DISTANCE = 250f;
        private const float MIN_PEEK_SPEED = 0.01f;
        private const float PEEK_TIME_FACTOR = 5;

        private Matrix _transform = Matrix.Identity;
        private Matrix _inverseTransform = Matrix.Identity;
        private bool _updateMatrixes = true;
        private float _lerpZoom;
        private float _zoom;
        private Vector2 _lerpOffset;


        public Camera(float minZoomValue = 1f, float maxZoomValue = 1f, float zoomStep = 0.1f)
        {
            ZoomMin = minZoomValue;
            ZoomMax = maxZoomValue;
            ZoomStep = zoomStep;
            Zoom = _lerpZoom = 1f;
        }


        public Rectangle Bounds;

        public Matrix ViewTransformMatrix
        {
            get
            {
                UpdateMatrices();

                return _transform;
            }
        }

        public float ZoomStep { get; private set; }
        public float ZoomMin { get; private set; }
        public float ZoomMax { get; private set; }
        public float Zoom
        {
            get => _zoom;
            set
            {
                _zoom = MathHelper.Clamp(value, ZoomMin, ZoomMax);
                _updateMatrixes = true;
            }
        }



        public void ZoomIn() => Zoom -= ZoomStep;

        public void ZoomOut() => Zoom += ZoomStep;


        public Vector2 Offset => _lerpOffset;

        private int _worldOffsetX, _worldOffsetY;

        /// <summary>
        /// The world's draw offset (the world pixel drawn at the viewport's top-left before zoom and peek). The matrix snaps the
        /// world's screen origin (its translation minus offset x scale) to whole target pixels, so the FNA passes (Legacy world,
        /// lights) and the Gpu world, which derives its centre from this matrix (<see cref="GpuCenter"/>), draw a world pixel at
        /// the same target pixel at any viewport size, zoom and peek.
        /// </summary>
        public void SetWorldOffset(int x, int y)
        {
            if (x != _worldOffsetX || y != _worldOffsetY)
            {
                _worldOffsetX = x;
                _worldOffsetY = y;
                _updateMatrixes = true;
            }
        }

        /// <summary>The world pixel at the centre of a <paramref name="targetWidth"/> x <paramref name="targetHeight"/> target
        /// drawn with <see cref="ViewTransformMatrix"/>, and the scale (target pixels per world pixel).</summary>
        public (double X, double Y, float Scale) GpuCenter(int targetWidth, int targetHeight)
        {
            UpdateMatrices();

            double scale = _transform.M11;

            return (_worldOffsetX + (targetWidth * 0.5 - _transform.M41) / scale, _worldOffsetY + (targetHeight * 0.5 - _transform.M42) / scale, (float)scale);
        }

        public bool PeekingToMouse;

        public bool PeekBackwards;

        private float _timeDelta = 0;
        private Point _mousePos;

        public void Update(bool force, float timeDelta, Point mousePos)
        {
            if (force)
            {
                _updateMatrixes = true;
            }

            _timeDelta= timeDelta;
            _mousePos = mousePos;

            UpdateMatrices();
        }

        public Point ScreenToWorld(Point point, bool withOffset = false)
        {
            UpdateMatrices();

            int offsetX = 0;
            int offsetY = 0;
            if (withOffset)
            {
                offsetX = -Bounds.X;
                offsetY = -Bounds.Y;
            }

            Transform(ref point, ref _inverseTransform, out point, offsetX, offsetY);

            return point;
        }

        /// <summary>
        ///     Returns screen coordinates from world coordinates.
        ///     There are two variants for screen coordinates:
        ///     1. Relative to the game window (withOffset = true)
        ///     2. Relative to the camera viewport (withOffset = false)
        ///     Because of the fact that the camera viewport content now
        ///     fully scales with the zoom level, everything that
        ///     is not supposed to zoom (UI, etc.) is drawn
        ///     in the UI render target now.
        ///     Only those aspects need to adjust for case 1 above.
        /// </summary>
        public Point WorldToScreen(Point point, bool withOffset = false)
        {
            UpdateMatrices();

            int offsetX = 0;
            int offsetY = 0;
            if (withOffset)
            {
                offsetX = Bounds.X;
                offsetY = Bounds.Y;
            }

            Transform(ref point, ref _transform, out point, offsetX, offsetY);

            return point;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Transform(ref Point position, ref Matrix matrix, out Point result, int offsetX, int offsetY)
        {
            float x = position.X * matrix.M11 + position.Y * matrix.M21 + matrix.M41 + offsetX;
            float y = position.X * matrix.M12 + position.Y * matrix.M22 + matrix.M42 + offsetY;
            result.X = (int) x;
            result.Y = (int) y;
        }

        private void UpdateMatrices()
        {
            if (!_updateMatrixes)
            {
                return;
            }

            Matrix temp;

            var origin = new Vector2(Bounds.Width * 0.5f, Bounds.Height * 0.5f);

            Matrix.CreateTranslation(-origin.X, -origin.Y, 0f, out _transform);

            CalculateLerpZoom();

            Matrix.CreateScale(_lerpZoom, _lerpZoom, 1f, out temp);
            Matrix.Multiply(ref _transform, ref temp, out _transform);

            CalculatePeek(origin);

            Matrix.CreateTranslation(origin.X - _lerpOffset.X, origin.Y - _lerpOffset.Y, 0f, out temp);
            Matrix.Multiply(ref _transform, ref temp, out _transform);

            double ox = _transform.M41 - (double)_worldOffsetX * _lerpZoom, oy = _transform.M42 - (double)_worldOffsetY * _lerpZoom;
            _transform.M41 += (float)(Math.Round(ox) - ox);
            _transform.M42 += (float)(Math.Round(oy) - oy);


            Matrix.Invert(ref _transform, out _inverseTransform);

            _updateMatrixes = false;
        }

        private void CalculateLerpZoom()
        {
            float zoom = 1f / Zoom;

            _lerpZoom = zoom;
        }

        private void CalculatePeek(Vector2 origin)
        {
            Vector2 target_offset = new Vector2();

            if (PeekingToMouse)
            {
                Vector2 target = new Vector2(_mousePos.X - Bounds.X, _mousePos.Y - Bounds.Y);

                if (PeekBackwards)
                {
                    target.X = 2 * origin.X - target.X;
                    target.Y = 2 * origin.Y - target.Y;
                }

                target_offset = target - origin;
                float length = target_offset.Length();

                if (length > 0)
                {
                    float length_factor = Math.Min(length / (Bounds.Height >> 1), 1f);
                    target_offset = Vector2.Normalize(target_offset) * Utility.Easings.OutQuad(length_factor) * MAX_PEEK_DISTANCE / Zoom;
                }
            }

            float dist = Vector2.Distance(target_offset, _lerpOffset);

            if (dist > 1f)
            {
                float time = Math.Max(Utility.Easings.OutQuart(dist / MAX_PEEK_DISTANCE) * _timeDelta * PEEK_TIME_FACTOR, MIN_PEEK_SPEED);
                _lerpOffset = Vector2.Lerp(_lerpOffset, target_offset, time);
            }
            else
            {
                _lerpOffset = target_offset;
            }
        }
    }
}