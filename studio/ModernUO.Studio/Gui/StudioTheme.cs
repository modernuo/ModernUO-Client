using System.Numerics;
using Hexa.NET.ImGui;

namespace ModernUO.Studio.Gui
{
    internal static class StudioTheme
    {
        public static void Apply(float scale)
        {
            ImGui.StyleColorsDark();
            var style = ImGui.GetStyle();

            style.WindowRounding = 4f;
            style.ChildRounding = 3f;
            style.FrameRounding = 3f;
            style.PopupRounding = 3f;
            style.GrabRounding = 3f;
            style.TabRounding = 3f;
            style.ScrollbarRounding = 6f;
            style.WindowPadding = new Vector2(8, 8);
            style.FramePadding = new Vector2(6, 4);
            style.ItemSpacing = new Vector2(8, 5);
            style.WindowBorderSize = 1f;
            style.FrameBorderSize = 0f;
            style.TabBorderSize = 0f;

            var c = style.Colors;
            Vector4 bg = new(0.105f, 0.11f, 0.12f, 1f);
            Vector4 panel = new(0.135f, 0.14f, 0.155f, 1f);
            Vector4 frame = new(0.18f, 0.19f, 0.21f, 1f);
            Vector4 accent = new(0.30f, 0.52f, 0.82f, 1f);
            Vector4 accentDim = new(0.24f, 0.40f, 0.62f, 1f);

            c[(int)ImGuiCol.WindowBg] = bg;
            c[(int)ImGuiCol.ChildBg] = new Vector4(0, 0, 0, 0);
            c[(int)ImGuiCol.PopupBg] = panel;
            c[(int)ImGuiCol.MenuBarBg] = panel;
            c[(int)ImGuiCol.Border] = new Vector4(0.22f, 0.23f, 0.26f, 1f);
            c[(int)ImGuiCol.FrameBg] = frame;
            c[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.23f, 0.25f, 0.28f, 1f);
            c[(int)ImGuiCol.FrameBgActive] = new Vector4(0.26f, 0.29f, 0.33f, 1f);
            c[(int)ImGuiCol.TitleBg] = panel;
            c[(int)ImGuiCol.TitleBgActive] = panel;
            c[(int)ImGuiCol.TitleBgCollapsed] = panel;
            c[(int)ImGuiCol.Header] = new Vector4(accentDim.X, accentDim.Y, accentDim.Z, 0.45f);
            c[(int)ImGuiCol.HeaderHovered] = new Vector4(accent.X, accent.Y, accent.Z, 0.55f);
            c[(int)ImGuiCol.HeaderActive] = accent;
            c[(int)ImGuiCol.Button] = frame;
            c[(int)ImGuiCol.ButtonHovered] = accentDim;
            c[(int)ImGuiCol.ButtonActive] = accent;
            c[(int)ImGuiCol.CheckMark] = accent;
            c[(int)ImGuiCol.SliderGrab] = accentDim;
            c[(int)ImGuiCol.SliderGrabActive] = accent;
            c[(int)ImGuiCol.Tab] = panel;
            c[(int)ImGuiCol.TabHovered] = accentDim;
            c[(int)ImGuiCol.TabSelected] = new Vector4(0.20f, 0.30f, 0.45f, 1f);
            c[(int)ImGuiCol.TabDimmed] = panel;
            c[(int)ImGuiCol.TabDimmedSelected] = new Vector4(0.17f, 0.22f, 0.30f, 1f);
            c[(int)ImGuiCol.DockingPreview] = new Vector4(accent.X, accent.Y, accent.Z, 0.6f);
            c[(int)ImGuiCol.TableHeaderBg] = panel;
            c[(int)ImGuiCol.TableRowBgAlt] = new Vector4(1f, 1f, 1f, 0.025f);
            c[(int)ImGuiCol.SeparatorHovered] = accentDim;
            c[(int)ImGuiCol.SeparatorActive] = accent;
            c[(int)ImGuiCol.ResizeGrip] = new Vector4(accent.X, accent.Y, accent.Z, 0.2f);
            c[(int)ImGuiCol.ResizeGripHovered] = accentDim;
            c[(int)ImGuiCol.ResizeGripActive] = accent;

            if (scale != 1f)
            {
                style.ScaleAllSizes(scale);
            }
        }

        /// <summary>A UI font: Segoe UI on Windows, DejaVu or Helvetica elsewhere, ImGui's built-in font when none is found.</summary>
        public static unsafe void LoadFont(float size)
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"),
                "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                "/usr/share/fonts/TTF/DejaVuSans.ttf",
                "/System/Library/Fonts/Helvetica.ttc"
            };

            var io = ImGui.GetIO();

            foreach (string path in candidates)
            {
                if (File.Exists(path))
                {
                    io.Fonts.AddFontFromFileTTF(path, size);

                    return;
                }
            }

            io.Fonts.AddFontDefault();
        }
    }
}
