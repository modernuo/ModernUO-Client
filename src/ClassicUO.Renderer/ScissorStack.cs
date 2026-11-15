// SPDX-License-Identifier: BSD-2-Clause

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace ClassicUO.Renderer
{
    public static class ScissorStack
    {
        private static readonly Stack<Rectangle> _scissors = new Stack<Rectangle>();
        private static readonly RenderTargetBinding[] _targets = new RenderTargetBinding[8];

        public static bool HasScissors => _scissors.Count - 1 > 0;

        /// <summary>
        /// Clips <paramref name="scissor"/> to <paramref name="bounds"/> (the current render target). FNA rejects a scissor
        /// rect with a negative x, y, width or height ("The scissor rectangle is invalid"), which a gump partly off the
        /// top or left edge produced, e.g. the login gump centred in a window that was briefly smaller than it. Returns
        /// false when nothing is left to draw.
        /// </summary>
        public static bool ClipToBounds(Rectangle scissor, Rectangle bounds, out Rectangle result)
        {
            int minX = Math.Max(bounds.X, scissor.X);
            int minY = Math.Max(bounds.Y, scissor.Y);
            int maxX = Math.Min(bounds.X + bounds.Width, scissor.X + scissor.Width);
            int maxY = Math.Min(bounds.Y + bounds.Height, scissor.Y + scissor.Height);

            if (maxX - minX < 1 || maxY - minY < 1)
            {
                result = Rectangle.Empty;

                return false;
            }

            result = new Rectangle(minX, minY, maxX - minX, maxY - minY);

            return true;
        }

        /// <summary>Bounds of the bound render target, or of the back buffer when none is bound.</summary>
        public static Rectangle TargetBounds(GraphicsDevice device)
        {
            if (device.GetRenderTargetsNoAllocEXT(null) is > 0 and <= 8)
            {
                device.GetRenderTargetsNoAllocEXT(_targets);
                Texture target = _targets[0].RenderTarget;
                Array.Clear(_targets);

                if (target is Texture2D t2d)
                {
                    return new Rectangle(0, 0, t2d.Width, t2d.Height);
                }

                if (target is TextureCube cube)
                {
                    return new Rectangle(0, 0, cube.Size, cube.Size);
                }
            }

            PresentationParameters pp = device.PresentationParameters;

            return new Rectangle(0, 0, pp.BackBufferWidth, pp.BackBufferHeight);
        }

        public static bool PushScissors(GraphicsDevice device, Rectangle scissor)
        {
            if (!ClipToBounds(scissor, TargetBounds(device), out scissor))
            {
                return false;
            }

            if (_scissors.Count > 0)
            {
                Rectangle parent = _scissors.Peek();
                int minX = Math.Max(parent.X, scissor.X);
                int maxX = Math.Min(parent.X + parent.Width, scissor.X + scissor.Width);

                if (maxX - minX < 1)
                {
                    return false;
                }

                int minY = Math.Max(parent.Y, scissor.Y);
                int maxY = Math.Min(parent.Y + parent.Height, scissor.Y + scissor.Height);

                if (maxY - minY < 1)
                {
                    return false;
                }

                scissor.X = minX;
                scissor.Y = minY;
                scissor.Width = maxX - minX;
                scissor.Height = Math.Max(1, maxY - minY);
            }

            _scissors.Push(scissor);
            device.ScissorRectangle = scissor;

            return true;
        }

        public static Rectangle PopScissors(GraphicsDevice device)
        {
            Rectangle scissors = _scissors.Pop();

            if (_scissors.Count == 0)
            {
                device.ScissorRectangle = device.Viewport.Bounds;
            }
            else
            {
                device.ScissorRectangle = _scissors.Peek();
            }

            return scissors;
        }

        public static Rectangle CalculateScissors(Matrix batchTransform, int sx, int sy, int sw, int sh)
        {
            Vector2 tmp = new Vector2(sx, sy);
            Vector2.Transform(ref tmp, ref batchTransform, out tmp);

            Rectangle newScissor = new Rectangle
            {
                X = (int) tmp.X, Y = (int) tmp.Y
            };

            tmp.X = sx + sw;
            tmp.Y = sy + sh;
            Vector2.Transform(ref tmp, ref batchTransform, out tmp);
            newScissor.Width = (int) tmp.X - newScissor.X;
            newScissor.Height = (int) tmp.Y - newScissor.Y;

            return newScissor;
        }
    }
}