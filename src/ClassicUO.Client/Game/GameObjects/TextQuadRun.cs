// SPDX-License-Identifier: BSD-2-Clause
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game
{
    internal readonly record struct TextQuadCacheKey(object Layout, object Atlas, int Width, byte Font,
        bool Unicode, bool Html, FontStyle Style, ushort Hue, byte Cell)
    {
        internal bool CanReuse(bool enabled, bool valid, in TextQuadCacheKey previous) => enabled && valid && this == previous;
    }

    // Relative, unscaled geometry: projection must keep the legacy path's two separate truncations.
    internal struct TextQuadRun
    {
        internal Texture2D Texture;
        internal Rectangle UV;
        internal int X, Y, Width, Height;
        internal bool Underline;

        internal bool Visible(int clipX, int clipY, int clipWidth, int clipHeight) => Underline
            ? Y >= clipY && Y < clipY + clipHeight
            : X + Width > clipX && X < clipX + clipWidth && Y + Height > clipY && Y < clipY + clipHeight;

        internal Rectangle Project(int destX, int destY, float scale, int clipX, int clipY) => new Rectangle(
            destX + (int)(X * scale) - (int)(clipX * scale),
            destY + (int)(Y * scale) - (int)(clipY * scale),
            (int)(Width * scale), Underline ? 1 : (int)(Height * scale));

        internal bool Matches(in TextQuadRun other) => ReferenceEquals(Texture, other.Texture)
            && UV == other.UV && X == other.X && Y == other.Y && Width == other.Width
            && Height == other.Height && Underline == other.Underline;
    }
}
