// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using System;
using static SDL3.SDL;

namespace ClassicUO
{
    /// <summary>
    /// FNALoggerEXT.LogInfo hook (Client.Run): FNA3D names the adapter it opened only in its own info log ("D3D11 Adapter:",
    /// "OpenGL Renderer:"); keep that line for the startup display line and print every line to the console as FNA does.
    /// </summary>
    internal static class FnaInfoLog
    {
        public static volatile string Adapter;

        public static void Info(string message)
        {
            if (message != null)
            {
                if (message.StartsWith("D3D11 Adapter: ", StringComparison.Ordinal))
                {
                    Adapter = message.Substring("D3D11 Adapter: ".Length);
                }
                else if (message.StartsWith("OpenGL Renderer: ", StringComparison.Ordinal))
                {
                    Adapter = message.Substring("OpenGL Renderer: ".Length);
                }
            }

            Console.WriteLine(message);
        }
    }

    internal unsafe partial class GameController
    {
        private string _displayChoice = "default";

        private static DisplayInfo[] GetDisplays(out int primaryIndex)
        {
            primaryIndex = 0;
            IntPtr list = SDL_GetDisplays(out int count);

            if (list == IntPtr.Zero || count <= 0)
            {
                return Array.Empty<DisplayInfo>();
            }

            uint primary = SDL_GetPrimaryDisplay();
            var displays = new DisplayInfo[count];

            try
            {
                for (int i = 0; i < count; i++)
                {
                    uint id = ((uint*)list)[i];
                    Rectangle usable = SDL_GetDisplayUsableBounds(id, out SDL_Rect r) ? new Rectangle(r.x, r.y, r.w, r.h) : Rectangle.Empty;
                    displays[i] = new DisplayInfo(id, SDL_GetDisplayName(id), usable);

                    if (id == primary)
                    {
                        primaryIndex = i;
                    }
                }
            }
            finally
            {
                SDL_free(list);
            }

            return displays;
        }

        /// <summary>
        /// At startup: the monitor from window_display (-display) if set, else window_position on whatever monitor holds it
        /// (negative coordinates included), else where FNA opened the window (WindowPlacement).
        /// </summary>
        public void SetWindowPositionBySettings()
        {
            if (!SDL_GetWindowBordersSize(Window.Handle, out int top, out int left, out _, out _))
            {
                top = 0;
                left = 0;
            }

            DisplayInfo[] displays = GetDisplays(out int primary);

            if (displays.Length == 0)
            {
                return;
            }

            string choice = Settings.GlobalSettings.WindowDisplay;
            Point? saved = Settings.GlobalSettings.WindowPosition;
            int chosen = WindowPlacement.ResolveDisplay(choice, displays, primary, out bool fellBack);
            Point position;

            if (chosen >= 0)
            {
                SDL_GetWindowSize(Window.Handle, out int width, out int height);
                position = WindowPlacement.PlaceOnDisplay(saved, left, top, width, height, displays[chosen]);
                _displayChoice = fellBack ? $"window_display \"{choice}\" matches no display: primary" : $"window_display \"{choice}\"";
            }
            else if (saved.HasValue)
            {
                position = WindowPlacement.PlaceSaved(saved.Value, left, top, displays, primary);
                _displayChoice = "window_position";
            }
            else
            {
                return;
            }

            SDL_SetWindowPosition(Window.Handle, position.X, position.Y);
            SDL_SyncWindow(Window.Handle);
        }

        // one startup line: the display the window is on and the adapter and driver drawing it
        private void LogDisplayAndAdapter()
        {
            DisplayInfo[] displays = GetDisplays(out _);
            uint id = SDL_GetDisplayForWindow(Window.Handle);
            int index = Array.FindIndex(displays, d => d.Id == id);
            string name = index >= 0 ? displays[index].Name : "unknown";
            Rectangle usable = index >= 0 ? displays[index].Usable : Rectangle.Empty;

            string sdlDriver = GpuInterop.GetDriverName(_gpuDevice);
            string adapter = _gpuDevice != IntPtr.Zero ? GpuInterop.GetAdapterName(_gpuDevice) : FnaInfoLog.Adapter;
            string driver = sdlDriver != null ? $"FNA3D SDLGPU {sdlDriver}" : $"FNA3D {GpuInterop.GetFna3dDriverName(GraphicsDevice) ?? "unknown"}";

            Log.Info($"[display] display {index} \"{name}\" (id {id}, usable {usable.Width}x{usable.Height} at {usable.X},{usable.Y}; " +
                     $"{displays.Length} displays; by {_displayChoice}), adapter \"{adapter ?? "unknown"}\" ({driver}; " +
                     $"FNA3D_PREFER_LOW_POWER={(SDL_GetHintBoolean("FNA3D_PREFER_LOW_POWER", false) ? 1 : 0)})");
        }
    }
}
