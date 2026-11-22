// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Globalization;
using Microsoft.Xna.Framework;

namespace ClassicUO
{
    /// <summary>One SDL display: its id, name and usable bounds (desktop coordinates; a monitor left of or above the primary
    /// has negative ones).</summary>
    internal readonly record struct DisplayInfo(uint Id, string Name, Rectangle Usable);

    /// <summary>
    /// Where the window opens (GameController.SetWindowPositionBySettings). <c>window_display</c> / <c>-display</c> pick a
    /// monitor by SDL display index (0-based, SDL_GetDisplays order) or name; otherwise <c>window_position</c> is kept when it
    /// lies on a display. Positions are the window's client-area top-left in desktop coordinates, which are negative on a
    /// monitor left of or above the primary. <paramref name="left"/>/<paramref name="top"/> in the methods are the window's
    /// border sizes, kept inside the display so the title bar stays reachable.
    /// </summary>
    internal static class WindowPlacement
    {
        /// <summary>
        /// The display index <paramref name="choice"/> names: an index into <paramref name="displays"/>, else the first display
        /// whose name equals it (ignoring case), else the first whose name contains it. -1 when there is no choice; the primary
        /// (<paramref name="primaryIndex"/>) when the choice matches nothing (<paramref name="fellBack"/> true).
        /// </summary>
        public static int ResolveDisplay(string choice, ReadOnlySpan<DisplayInfo> displays, int primaryIndex, out bool fellBack)
        {
            fellBack = false;

            if (string.IsNullOrWhiteSpace(choice) || displays.Length == 0)
            {
                return -1;
            }

            choice = choice.Trim();
            int primary = primaryIndex >= 0 && primaryIndex < displays.Length ? primaryIndex : 0;

            if (int.TryParse(choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                if (index >= 0 && index < displays.Length)
                {
                    return index;
                }

                fellBack = true;

                return primary;
            }

            for (int i = 0; i < displays.Length; i++)
            {
                if (string.Equals(displays[i].Name, choice, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            for (int i = 0; i < displays.Length; i++)
            {
                if (displays[i].Name != null && displays[i].Name.Contains(choice, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            fellBack = true;

            return primary;
        }

        /// <summary>The index of the display whose usable bounds hold <paramref name="point"/>, or -1.</summary>
        public static int DisplayAt(Point point, ReadOnlySpan<DisplayInfo> displays)
        {
            for (int i = 0; i < displays.Length; i++)
            {
                if (displays[i].Usable.Contains(point))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// window_position without a display choice: <paramref name="saved"/> (the window's outer top-left, as saved at exit)
        /// on whatever display holds it, negative coordinates included; a coordinate off that display moves to its edge; a
        /// position on no display opens at the primary's top-left.
        /// </summary>
        public static Point PlaceSaved(Point saved, int left, int top, ReadOnlySpan<DisplayInfo> displays, int primaryIndex)
        {
            Point p = new Point(saved.X + left, saved.Y + top);
            int d = DisplayAt(p, displays);

            if (d < 0)
            {
                if (displays.Length == 0)
                {
                    return p;
                }

                Rectangle primary = displays[primaryIndex >= 0 && primaryIndex < displays.Length ? primaryIndex : 0].Usable;

                return new Point(primary.X + left, primary.Y + top);
            }

            return KeepInside(p, left, top, displays[d].Usable);
        }

        /// <summary>
        /// A chosen display: <paramref name="saved"/> when it lies on that display (the player's spot on it), otherwise the
        /// window (<paramref name="width"/> x <paramref name="height"/>, client area) centred in its usable bounds, its title
        /// bar inside.
        /// </summary>
        public static Point PlaceOnDisplay(Point? saved, int left, int top, int width, int height, in DisplayInfo display)
        {
            Rectangle u = display.Usable;

            if (saved.HasValue)
            {
                Point p = new Point(saved.Value.X + left, saved.Value.Y + top);

                if (u.Contains(p))
                {
                    return KeepInside(p, left, top, u);
                }
            }

            int x = u.X + Math.Max(left, (u.Width - width) / 2);
            int y = u.Y + Math.Max(top, (u.Height - height) / 2);

            return new Point(x, y);
        }

        // the client area's top-left at least the border sizes inside the display's top and left edges
        private static Point KeepInside(Point p, int left, int top, Rectangle u)
            => new Point(Math.Max(p.X, u.X + left), Math.Max(p.Y, u.Y + top));
    }
}
