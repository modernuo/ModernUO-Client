// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer
{
    public enum SpriteCaptureMode : byte
    {
        Normal,
        Shadow,
        Sitted
    }

    /// <summary>
    /// Receives the batcher's sprite draws while <see cref="UltimaBatcher2D.Capture"/> is set, so a view's draw logic can
    /// feed another renderer. Returning false lets the batcher draw the sprite itself.
    /// </summary>
    public interface ISpriteCapture
    {
        bool Draw(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, bool flip, bool scaled, float depth, SpriteCaptureMode mode);
    }

    /// <summary>
    /// Maps atlas sprites back to the pack sprite they were decoded from: art sprites by their sprite index, animation
    /// frames by their frame index with <see cref="AnimFrame"/> set.
    /// </summary>
    public static class PackSpriteMap
    {
        public const uint AnimFrame = 0x8000_0000;

        private struct Entry
        {
            public int Y, Width, Height;
            public uint Ref;
        }

        private static readonly Dictionary<Texture2D, Dictionary<int, List<Entry>>> _map = new Dictionary<Texture2D, Dictionary<int, List<Entry>>>();

        public static void Register(Texture2D texture, Rectangle uv, uint packRef)
        {
            if (texture == null)
            {
                return;
            }

            if (!_map.TryGetValue(texture, out var columns))
            {
                _map[texture] = columns = new Dictionary<int, List<Entry>>();
            }

            if (!columns.TryGetValue(uv.X, out var list))
            {
                columns[uv.X] = list = new List<Entry>(1);
            }

            list.Add(new Entry { Y = uv.Y, Width = uv.Width, Height = uv.Height, Ref = packRef });
        }

        /// <summary>The pack sprite whose atlas rectangle contains <paramref name="source"/>, and that rectangle's origin.</summary>
        public static bool TryFind(Texture2D texture, Rectangle source, out uint packRef, out Point origin)
        {
            packRef = 0;
            origin = Point.Zero;

            if (texture == null || !_map.TryGetValue(texture, out var columns) || !columns.TryGetValue(source.X, out var list))
            {
                return false;
            }

            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];

                if (source.Y >= e.Y && source.Bottom <= e.Y + e.Height && source.Width <= e.Width)
                {
                    packRef = e.Ref;
                    origin = new Point(source.X, e.Y);

                    return true;
                }
            }

            return false;
        }

        public static void Clear() => _map.Clear();
    }
}
