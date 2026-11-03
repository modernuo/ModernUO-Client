using System.Numerics;
using System.Reflection;
using Hexa.NET.ImGui;

namespace ModernUO.Studio.Gui
{
    /// <summary>
    /// Studio's look: ModernUO's brand colours (modernuo.com: muo-gold #d5bf74 as the accent, muo-blue for links, warm
    /// near-black neutrals), Inter for text, JetBrains Mono for ids and Lucide icons, and the ImGui style that keeps
    /// ImGui's own look out of sight. Every colour in Studio comes from here.
    /// </summary>
    internal static class Theme
    {
        // ---- surfaces, darkest first
        public static readonly Vector4 Base = Rgb(0x141414);      // behind everything: dock space, top and status bars
        public static readonly Vector4 Panel = Rgb(0x1a1a1a);     // a docked window (modernuo.com background)
        public static readonly Vector4 Surface = Rgb(0x212121);   // cards, fields, inspector (modernuo.com surface)
        public static readonly Vector4 Raised = Rgb(0x2a2a2a);    // hover on a surface, buttons
        public static readonly Vector4 Pressed = Rgb(0x333333);
        public static readonly Vector4 Hairline = Rgb(0x2c2c2c);  // borders and separators

        // ---- text
        public static readonly Vector4 Text = Rgb(0xe3e3e3);
        public static readonly Vector4 TextSecondary = new(1f, 1f, 1f, 0.6f);
        public static readonly Vector4 TextMuted = new(1f, 1f, 1f, 0.35f);

        // ---- brand
        public static readonly Vector4 Gold = Rgb(0xd5bf74);
        public static readonly Vector4 GoldDark = Rgb(0xb09f4f);
        public static readonly Vector4 GoldLight = Rgb(0xe8d89e);
        public static readonly Vector4 GoldTint = new(0.835f, 0.749f, 0.455f, 0.14f);
        public static readonly Vector4 GoldTintStrong = new(0.835f, 0.749f, 0.455f, 0.24f);
        public static readonly Vector4 OnGold = Rgb(0x1a1a1a);
        public static readonly Vector4 Blue = Rgb(0x748ad5);
        public static readonly Vector4 Green = Rgb(0x77d574);
        public static readonly Vector4 Purple = Rgb(0xbf74d5);
        public static readonly Vector4 Red = Rgb(0xdb4455);
        public static readonly Vector4 Amber = Rgb(0xe0a458);

        public const float Rounding = 6f;
        public const float CardRounding = 8f;
        public const float FontSize = 15f;
        public const float SmallSize = 12.5f;
        public const float TitleSize = 19f;

        public static ImFontPtr Body { get; private set; }
        public static ImFontPtr Strong { get; private set; }
        public static ImFontPtr Mono { get; private set; }

        public static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

        public static uint U32(Vector4 c, float alpha) => ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, c.W * alpha));

        public static Vector4 Mix(Vector4 a, Vector4 b, float t) => Vector4.Lerp(a, b, Math.Clamp(t, 0f, 1f));

        private static Vector4 Rgb(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

        public static void Apply(float scale)
        {
            ImGui.StyleColorsDark();
            var s = ImGui.GetStyle();

            s.WindowPadding = new Vector2(12, 12);
            s.FramePadding = new Vector2(10, 6);
            s.ItemSpacing = new Vector2(8, 8);
            s.ItemInnerSpacing = new Vector2(6, 6);
            s.CellPadding = new Vector2(8, 5);
            s.IndentSpacing = 16f;
            s.ScrollbarSize = 10f;
            s.GrabMinSize = 10f;

            s.WindowRounding = CardRounding;
            s.ChildRounding = CardRounding;
            s.PopupRounding = CardRounding;
            s.FrameRounding = Rounding;
            s.GrabRounding = Rounding;
            s.TabRounding = Rounding;
            s.ScrollbarRounding = 8f;

            s.WindowBorderSize = 0f;
            s.ChildBorderSize = 0f;
            s.PopupBorderSize = 1f;
            s.FrameBorderSize = 0f;
            s.TabBorderSize = 0f;
            s.TabBarBorderSize = 1f;
            s.TabBarOverlineSize = 2f;
            s.SeparatorTextBorderSize = 1f;
            s.SeparatorTextPadding = new Vector2(0, 4);
            s.DockingSeparatorSize = 2f;
            s.ImageBorderSize = 0f;

            // no ImGui window chrome: no collapse triangle, close buttons only on the active tab
            s.WindowMenuButtonPosition = ImGuiDir.None;
            // FLT_MAX: never on an unselected tab; 0: on the selected tab when hovered
            s.TabCloseButtonMinWidthUnselected = float.MaxValue;
            s.TabCloseButtonMinWidthSelected = 0f;
            s.WindowTitleAlign = new Vector2(0f, 0.5f);
            s.SelectableTextAlign = new Vector2(0f, 0.5f);

            var c = s.Colors;
            Set(c, ImGuiCol.Text, Text);
            Set(c, ImGuiCol.TextDisabled, TextMuted);
            Set(c, ImGuiCol.WindowBg, Panel);
            Set(c, ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0));
            Set(c, ImGuiCol.PopupBg, Surface);
            Set(c, ImGuiCol.Border, Hairline);
            Set(c, ImGuiCol.BorderShadow, new Vector4(0, 0, 0, 0));
            Set(c, ImGuiCol.FrameBg, Surface);
            Set(c, ImGuiCol.FrameBgHovered, Raised);
            Set(c, ImGuiCol.FrameBgActive, Pressed);
            Set(c, ImGuiCol.TitleBg, Base);
            Set(c, ImGuiCol.TitleBgActive, Base);
            Set(c, ImGuiCol.TitleBgCollapsed, Base);
            Set(c, ImGuiCol.MenuBarBg, Base);
            Set(c, ImGuiCol.ScrollbarBg, new Vector4(0, 0, 0, 0));
            Set(c, ImGuiCol.ScrollbarGrab, Raised);
            Set(c, ImGuiCol.ScrollbarGrabHovered, Pressed);
            Set(c, ImGuiCol.ScrollbarGrabActive, GoldDark);
            Set(c, ImGuiCol.CheckMark, Gold);
            Set(c, ImGuiCol.SliderGrab, GoldDark);
            Set(c, ImGuiCol.SliderGrabActive, Gold);
            Set(c, ImGuiCol.Button, Raised);
            Set(c, ImGuiCol.ButtonHovered, Pressed);
            Set(c, ImGuiCol.ButtonActive, Rgb(0x3b3b3b));
            Set(c, ImGuiCol.Header, GoldTint);
            Set(c, ImGuiCol.HeaderHovered, new Vector4(1f, 1f, 1f, 0.05f));
            Set(c, ImGuiCol.HeaderActive, GoldTintStrong);
            Set(c, ImGuiCol.Separator, Hairline);
            Set(c, ImGuiCol.SeparatorHovered, GoldDark);
            Set(c, ImGuiCol.SeparatorActive, Gold);
            Set(c, ImGuiCol.ResizeGrip, new Vector4(0, 0, 0, 0));
            Set(c, ImGuiCol.ResizeGripHovered, GoldDark);
            Set(c, ImGuiCol.ResizeGripActive, Gold);
            Set(c, ImGuiCol.InputTextCursor, Gold);
            // unselected tabs have no shape at all (only their text); the selected one joins the panel under it
            Set(c, ImGuiCol.Tab, new Vector4(0, 0, 0, 0));
            Set(c, ImGuiCol.TabHovered, Surface);
            Set(c, ImGuiCol.TabSelected, Panel);
            Set(c, ImGuiCol.TabSelectedOverline, Gold);
            Set(c, ImGuiCol.TabDimmed, new Vector4(0, 0, 0, 0));
            Set(c, ImGuiCol.TabDimmedSelected, Panel);
            Set(c, ImGuiCol.TabDimmedSelectedOverline, new Vector4(0, 0, 0, 0));
            Set(c, ImGuiCol.DockingPreview, new Vector4(Gold.X, Gold.Y, Gold.Z, 0.35f));
            Set(c, ImGuiCol.DockingEmptyBg, Base);
            Set(c, ImGuiCol.TableHeaderBg, Surface);
            Set(c, ImGuiCol.TableBorderStrong, Hairline);
            Set(c, ImGuiCol.TableBorderLight, Rgb(0x242424));
            Set(c, ImGuiCol.TableRowBg, new Vector4(0, 0, 0, 0));
            Set(c, ImGuiCol.TableRowBgAlt, new Vector4(1f, 1f, 1f, 0.018f));
            Set(c, ImGuiCol.TextLink, Blue);
            Set(c, ImGuiCol.TextSelectedBg, new Vector4(Gold.X, Gold.Y, Gold.Z, 0.3f));
            Set(c, ImGuiCol.DragDropTarget, Gold);
            Set(c, ImGuiCol.NavCursor, Gold);
            Set(c, ImGuiCol.ModalWindowDimBg, new Vector4(0, 0, 0, 0.6f));
            Set(c, ImGuiCol.PlotHistogram, Gold);
            Set(c, ImGuiCol.PlotLines, Blue);

            if (scale != 1f)
            {
                s.ScaleAllSizes(scale);
            }
        }

        private static void Set(Span<Vector4> colors, ImGuiCol col, Vector4 value) => colors[(int)col] = value;

        /// <summary>Inter (regular and semibold) and JetBrains Mono, each with Lucide merged in, from the embedded Fonts/.</summary>
        public static void LoadFonts(float scale)
        {
            float size = FontSize * scale;
            Body = AddFont("Inter-Regular.ttf", size);
            Strong = AddFont("Inter-SemiBold.ttf", size);
            Mono = AddFont("JetBrainsMono-Regular.ttf", size);
            ImGui.GetIO().FontDefault = Body;
        }

        private static unsafe ImFontPtr AddFont(string resource, float size)
        {
            var atlas = ImGui.GetIO().Fonts;

            // Inter has glyphs of its own in the private use area, where Lucide's icons live: the text font leaves that
            // range to the icons (the first source with a glyph wins)
            var text = ImGui.ImFontConfig();
            text.Handle->GlyphExcludeRanges = PrivateUseRange();
            ImFontPtr font = atlas.AddFontFromMemoryTTF(Native(resource, out int length), length, size, text);

            // icons sit on the text's baseline and centre in a square cell, so a label like "<icon> Art" lines up
            var icons = ImGui.ImFontConfig();
            icons.MergeMode = true;
            icons.PixelSnapH = true;
            icons.GlyphMinAdvanceX = size * 1.15f;
            icons.GlyphOffset = new Vector2(0f, size * 0.12f);
            atlas.AddFontFromMemoryTTF(Native("lucide.ttf", out int iconLength), iconLength, size, icons);

            return font;
        }

        private static unsafe uint* _privateUse;

        // a zero-terminated range list ImGui keeps a pointer to for the atlas's lifetime
        private static unsafe uint* PrivateUseRange()
        {
            if (_privateUse == null)
            {
                _privateUse = (uint*)System.Runtime.InteropServices.NativeMemory.Alloc(3, sizeof(uint));
                _privateUse[0] = 0xE000;
                _privateUse[1] = 0xF8FF;
                _privateUse[2] = 0;
            }

            return _privateUse;
        }

        // the atlas frees the font data (FontDataOwnedByAtlas), so it must live in ImGui's own allocator
        private static unsafe void* Native(string resource, out int length)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                ?? throw new FileNotFoundException($"embedded font {resource}");
            length = (int)stream.Length;
            void* memory = ImGui.MemAlloc((nuint)length);
            stream.ReadExactly(new Span<byte>(memory, length));

            return memory;
        }
    }
}
