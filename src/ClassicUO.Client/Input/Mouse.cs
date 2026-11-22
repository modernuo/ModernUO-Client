// SPDX-License-Identifier: BSD-2-Clause

using Microsoft.Xna.Framework;
using SDL3;

namespace ClassicUO.Input
{
    internal static class Mouse
    {
        public const int MOUSE_DELAY_DOUBLE_CLICK = 350;

        /* Log a button press event at the given time. */
        public static void ButtonPress(MouseButtonType type)
        {
            CancelDoubleClick = false;

            switch (type)
            {
                case MouseButtonType.Left:
                    LButtonPressed = true;
                    LClickPosition = Position;

                    break;

                case MouseButtonType.Middle:
                    MButtonPressed = true;
                    MClickPosition = Position;

                    break;

                case MouseButtonType.Right:
                    RButtonPressed = true;
                    RClickPosition = Position;

                    break;

                case MouseButtonType.XButton1:
                case MouseButtonType.XButton2:
                    XButtonPressed = true;

                    break;
            }

            SDL.SDL_CaptureMouse(true);
        }

        /* Log a button release event at the given time */
        public static void ButtonRelease(MouseButtonType type)
        {
            switch (type)
            {
                case MouseButtonType.Left:
                    LButtonPressed = false;

                    break;

                case MouseButtonType.Middle:
                    MButtonPressed = false;

                    break;

                case MouseButtonType.Right:
                    RButtonPressed = false;

                    break;

                case MouseButtonType.XButton1:
                case MouseButtonType.XButton2:
                    XButtonPressed = false;

                    break;
            }

            if (!(LButtonPressed || RButtonPressed || MButtonPressed))
            {
                SDL.SDL_CaptureMouse(false);
            }
        }

        public static Point Position;

        /// <summary>The mouse in UI units before truncation (RenderGeometry.WindowToUi); a scripted position (Override) is its
        /// unit's centre.</summary>
        public static Vector2 PositionF;

        /// <summary>Dev (DevBench's ui scene): the position Update reports instead of the OS cursor's, without moving it.</summary>
        public static Point? Override;

        public static Point LClickPosition;

        public static Point RClickPosition;

        public static Point MClickPosition;

        public static uint LastLeftButtonClickTime { get; set; }

        public static uint LastMidButtonClickTime { get; set; }

        public static uint LastRightButtonClickTime { get; set; }

        public static bool CancelDoubleClick { get; set; }

        public static bool LButtonPressed { get; set; }

        public static bool RButtonPressed { get; set; }

        public static bool MButtonPressed { get; set; }

        public static bool XButtonPressed { get; set; }

        public static bool IsDragging { get; set; }

        public static Point LDragOffset => LButtonPressed ? Position - LClickPosition : Point.Zero;

        public static Point RDragOffset => RButtonPressed ? Position - RClickPosition : Point.Zero;

        public static Point MDragOffset => MButtonPressed ? Position - MClickPosition : Point.Zero;

        public static bool MouseInWindow { get; set; }

        public static void Update()
        {
            if (Override.HasValue)
            {
                if (Position != Override.Value)
                {
                    // visual-due tracking: a scripted move (DevBench, Input.Mouse.Override) has no input wake
                    ClassicUO.Loop.VisualDue.Now(ClassicUO.Loop.VisualSource.Cursor);
                }

                Position = Override.Value;
                PositionF = new Vector2(Position.X + 0.5f, Position.Y + 0.5f);
                IsDragging = LButtonPressed || RButtonPressed || MButtonPressed;

                return;
            }

            float mx, my;

            if (!MouseInWindow)
            {
                SDL.SDL_GetGlobalMouseState(out float x, out float y);
                SDL.SDL_GetWindowPosition(Client.Game.Window.Handle, out int winX, out int winY);
                mx = x - winX;
                my = y - winY;
            }
            else
            {
                SDL.SDL_GetMouseState(out mx, out my);
            }

            // window -> back buffer -> UI units (RenderGeometry): the inverse of the UI composite
            var geometry = Client.Game.Geometry;
            Vector2 b = geometry.WindowToBackBuffer(mx, my);
            PositionF = geometry.BackBufferToUi(b.X, b.Y);
            Position = new Point((int)System.MathF.Floor(PositionF.X), (int)System.MathF.Floor(PositionF.Y));

            IsDragging = LButtonPressed || RButtonPressed || MButtonPressed;
        }
    }
}