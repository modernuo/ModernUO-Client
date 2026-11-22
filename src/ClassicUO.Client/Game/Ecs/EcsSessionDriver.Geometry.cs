// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
using System.Globalization;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game
{
    // Render geometry checks: viewport <w> <h> [x y] sets the game viewport's size in UI units (and its position), geom <tag>
    // logs the frame's render geometry (GeometryProbe), zoom <z> sets the camera zoom, title <text> the window title.
    internal sealed partial class EcsSessionDriver
    {
        private bool ExecuteGeometry(string command, string[] args)
        {
            switch (command)
            {
                case "viewport" when args.Length > 1 && Client.Game.Scene is GameScene vs:
                {
                    int w = int.Parse(args[0], CultureInfo.InvariantCulture), h = int.Parse(args[1], CultureInfo.InvariantCulture);

                    if (ProfileManagerFullSizeOff() && UIManager.GetGump<WorldViewportGump>() is { } vp)
                    {
                        vp.SetViewportSize(new Point(w, h));

                        if (args.Length > 3)
                        {
                            vp.SetGameWindowPosition(new Point(int.Parse(args[2], CultureInfo.InvariantCulture) - WorldViewportGump.BORDER_WIDTH,
                                int.Parse(args[3], CultureInfo.InvariantCulture) - WorldViewportGump.BORDER_WIDTH));
                        }
                    }

                    Log.Info($"[ECS] driver: viewport {w}x{h} -> camera {vs.Camera.Bounds}");

                    return true;
                }
                case "geom":
                    GeometryProbe.Force(args.Length > 0 ? args[0] : "");

                    return true;
                // title <text>: the window title, so a person can tell a bench window apart
                case "title" when args.Length > 0:
                    Client.Game.Window.Title = string.Join(' ', args);

                    return true;
                case "zoom" when args.Length > 0 && Client.Game.Scene is GameScene zs:
                    zs.Camera.Zoom = float.Parse(args[0], CultureInfo.InvariantCulture);

                    return true;
            }

            return false;
        }

        private static bool ProfileManagerFullSizeOff()
        {
            if (Configuration.ProfileManager.CurrentProfile is { } profile)
            {
                profile.GameWindowFullSize = false;
            }

            return true;
        }
    }
}
#endif
