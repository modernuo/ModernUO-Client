using System.Numerics;
using Hexa.NET.ImGui;

namespace ModernUO.Studio.Gui
{
    internal enum ButtonKind
    {
        Primary,
        Secondary,
        Ghost
    }

    /// <summary>
    /// Studio's own widgets, drawn on ImGui's draw lists so nothing looks like stock ImGui: icon buttons, segmented
    /// controls, toggles, search fields, chips, cards and section labels, with short hover and selection fades.
    /// </summary>
    internal static class Ui
    {
        private static readonly Dictionary<uint, float> _anim = new();

        /// <summary>Eases a per-widget value towards <paramref name="target"/> (an exponential approach, frame-rate independent).</summary>
        public static float Animate(uint id, float target, float speed = 14f)
        {
            if (!_anim.TryGetValue(id, out float value))
            {
                value = target;
            }

            value += (target - value) * (1f - MathF.Exp(-speed * ImGui.GetIO().DeltaTime));

            if (MathF.Abs(value - target) < 0.001f)
            {
                value = target;
            }

            _anim[id] = value;

            return value;
        }

        public static float Height => ImGui.GetFrameHeight();

        // ---- buttons

        /// <summary>A square icon button with a soft hover background; <paramref name="active"/> shows it toggled on.</summary>
        public static bool IconButton(string id, string icon, string tooltip, bool active = false, bool enabled = true)
        {
            float size = Height;
            Vector2 min = ImGui.GetCursorScreenPos();

            if (!enabled)
            {
                ImGui.BeginDisabled();
            }

            bool clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
            bool hovered = ImGui.IsItemHovered();
            bool held = ImGui.IsItemActive();

            if (!enabled)
            {
                ImGui.EndDisabled();
            }

            float t = Animate(ImGui.GetItemID(), hovered ? 1f : 0f);
            var draw = ImGui.GetWindowDrawList();
            Vector4 bg = active ? Theme.GoldTint : Theme.Mix(new Vector4(Theme.Raised.X, Theme.Raised.Y, Theme.Raised.Z, 0f), Theme.Raised, t);

            if (held)
            {
                bg = Theme.Pressed;
            }

            draw.AddRectFilled(min, min + new Vector2(size, size), Theme.U32(bg), Theme.Rounding);
            Vector4 fg = !enabled ? Theme.TextMuted : active ? Theme.Gold : Theme.Mix(Theme.TextSecondary, Theme.Text, t);
            Vector2 textSize = ImGui.CalcTextSize(icon);
            draw.AddText(min + (new Vector2(size, size) - textSize) * 0.5f, Theme.U32(fg), icon);

            if (hovered && !string.IsNullOrEmpty(tooltip))
            {
                Tooltip(tooltip);
            }

            return clicked && enabled;
        }

        /// <summary>A text button; Primary is ModernUO gold, Secondary a raised surface, Ghost only shows on hover.</summary>
        public static bool Button(string label, ButtonKind kind = ButtonKind.Secondary, string tooltip = null, float width = 0f)
        {
            string text = VisibleLabel(label);
            Vector2 textSize = ImGui.CalcTextSize(text);
            var size = new Vector2(width > 0 ? width : textSize.X + 24f, Height);
            Vector2 min = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton(label, size);
            bool hovered = ImGui.IsItemHovered();
            bool held = ImGui.IsItemActive();
            float t = Animate(ImGui.GetItemID(), hovered ? 1f : 0f);

            Vector4 bg, fg;

            switch (kind)
            {
                case ButtonKind.Primary:
                    bg = held ? Theme.GoldDark : Theme.Mix(Theme.Gold, Theme.GoldLight, t * 0.6f);
                    fg = Theme.OnGold;

                    break;

                case ButtonKind.Ghost:
                    bg = held ? Theme.Pressed : new Vector4(Theme.Raised.X, Theme.Raised.Y, Theme.Raised.Z, t);
                    fg = Theme.Mix(Theme.TextSecondary, Theme.Text, t);

                    break;

                default:
                    bg = held ? Theme.Pressed : Theme.Mix(Theme.Raised, Theme.Pressed, t);
                    fg = Theme.Text;

                    break;
            }

            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(min, min + size, Theme.U32(bg), Theme.Rounding);
            ImGui.PushFont(kind == ButtonKind.Primary ? Theme.Strong : Theme.Body, 0f);
            draw.AddText(min + (size - ImGui.CalcTextSize(text)) * 0.5f, Theme.U32(fg), text);
            ImGui.PopFont();

            if (hovered && !string.IsNullOrEmpty(tooltip))
            {
                Tooltip(tooltip);
            }

            return clicked;
        }

        // "Text##id" draws "Text"
        private static string VisibleLabel(string label)
        {
            int hash = label.IndexOf("##", StringComparison.Ordinal);

            return hash < 0 ? label : label[..hash];
        }

        // ---- choices

        /// <summary>A segmented control: one rounded track, the chosen segment raised. True when the choice changed.</summary>
        public static bool Segmented(string id, ref int value, params string[] labels)
        {
            ImGui.PushID(id);
            float pad = 12f;
            float segment = 0f;

            foreach (string label in labels)
            {
                segment = MathF.Max(segment, ImGui.CalcTextSize(label).X + pad * 2f);
            }

            float height = Height;
            Vector2 min = ImGui.GetCursorScreenPos();
            var size = new Vector2(segment * labels.Length + 4f, height);
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(min, min + size, Theme.U32(Theme.Surface), Theme.Rounding + 1f);

            uint trackId = ImGui.GetID("track");
            float x = Animate(trackId, value, 18f);
            Vector2 pillMin = min + new Vector2(2f + x * segment, 2f);
            draw.AddRectFilled(pillMin, pillMin + new Vector2(segment, height - 4f), Theme.U32(Theme.Pressed), Theme.Rounding);

            bool changed = false;

            for (int i = 0; i < labels.Length; i++)
            {
                ImGui.SetCursorScreenPos(min + new Vector2(2f + i * segment, 0f));

                if (ImGui.InvisibleButton($"s{i}", new Vector2(segment, height)) && value != i)
                {
                    value = i;
                    changed = true;
                }

                bool hovered = ImGui.IsItemHovered();
                Vector4 fg = i == value ? Theme.Text : hovered ? Theme.Mix(Theme.TextSecondary, Theme.Text, 0.6f) : Theme.TextSecondary;
                Vector2 textSize = ImGui.CalcTextSize(labels[i]);
                draw.AddText(min + new Vector2(2f + i * segment + (segment - textSize.X) * 0.5f, (height - textSize.Y) * 0.5f), Theme.U32(fg), labels[i]);
            }

            ImGui.SetCursorScreenPos(min);
            ImGui.Dummy(size);
            ImGui.PopID();

            return changed;
        }

        /// <summary>A switch with its label after it. True when flipped.</summary>
        public static bool Toggle(string label, ref bool value, string tooltip = null)
        {
            string text = VisibleLabel(label);
            float height = Height;
            var track = new Vector2(30f, 17f);
            Vector2 textSize = ImGui.CalcTextSize(text);
            Vector2 min = ImGui.GetCursorScreenPos();
            var size = new Vector2(track.X + (text.Length > 0 ? 8f + textSize.X : 0f), height);
            bool clicked = ImGui.InvisibleButton(label, size);

            if (clicked)
            {
                value = !value;
            }

            bool hovered = ImGui.IsItemHovered();
            float t = Animate(ImGui.GetItemID(), value ? 1f : 0f, 18f);
            var draw = ImGui.GetWindowDrawList();
            Vector2 trackMin = min + new Vector2(0f, (height - track.Y) * 0.5f);
            Vector4 trackColor = Theme.Mix(hovered ? Theme.Pressed : Theme.Raised, Theme.Gold, t);
            draw.AddRectFilled(trackMin, trackMin + track, Theme.U32(trackColor), track.Y * 0.5f);
            float knob = track.Y - 4f;
            Vector2 knobCentre = trackMin + new Vector2(2f + knob * 0.5f + t * (track.X - knob - 4f), track.Y * 0.5f);
            draw.AddCircleFilled(knobCentre, knob * 0.5f, Theme.U32(Theme.Mix(Theme.TextSecondary, Theme.OnGold, t)), 16);

            if (text.Length > 0)
            {
                draw.AddText(min + new Vector2(track.X + 8f, (height - textSize.Y) * 0.5f), Theme.U32(hovered ? Theme.Text : Theme.TextSecondary), text);
            }

            if (hovered && !string.IsNullOrEmpty(tooltip))
            {
                Tooltip(tooltip);
            }

            return clicked;
        }

        // ---- fields

        /// <summary>A rounded search field with a magnifier and a clear button. True when the text changed.</summary>
        public static bool Search(string id, ref string text, string hint, float width, bool focus = false)
        {
            float height = Height;
            Vector2 min = ImGui.GetCursorScreenPos();
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(30f, ImGui.GetStyle().FramePadding.Y));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, height * 0.5f);
            ImGui.SetNextItemWidth(width);

            if (focus)
            {
                ImGui.SetKeyboardFocusHere();
            }

            bool changed = ImGui.InputTextWithHint(id, hint, ref text, 256);
            bool active = ImGui.IsItemActive();
            ImGui.PopStyleVar(2);

            var draw = ImGui.GetWindowDrawList();

            if (active)
            {
                draw.AddRect(min, min + new Vector2(width, height), Theme.U32(Theme.GoldDark), height * 0.5f, ImDrawFlags.None, 1f);
            }

            Vector2 iconSize = ImGui.CalcTextSize(Icons.Search);
            draw.AddText(min + new Vector2(10f, (height - iconSize.Y) * 0.5f), Theme.U32(active ? Theme.Gold : Theme.TextMuted), Icons.Search);

            if (text.Length > 0)
            {
                Vector2 after = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(min + new Vector2(width - height, 0f));

                if (ImGui.InvisibleButton(id + "_clear", new Vector2(height, height)))
                {
                    text = "";
                    changed = true;
                }

                bool hovered = ImGui.IsItemHovered();
                Vector2 xSize = ImGui.CalcTextSize(Icons.X);
                draw.AddText(min + new Vector2(width - height + (height - xSize.X) * 0.5f, (height - xSize.Y) * 0.5f),
                    Theme.U32(hovered ? Theme.Text : Theme.TextMuted), Icons.X);
                ImGui.SetCursorScreenPos(after);
            }

            return changed;
        }

        // ---- display

        /// <summary>A small rounded label, tinted with <paramref name="color"/>.</summary>
        public static void Chip(string text, Vector4 color)
        {
            Vector2 textSize = ImGui.CalcTextSize(text);
            var size = new Vector2(textSize.X + 16f, Height);
            Vector2 min = ImGui.GetCursorScreenPos();
            ImGui.Dummy(size);
            var draw = ImGui.GetWindowDrawList();
            float h = textSize.Y + 6f;
            Vector2 boxMin = min + new Vector2(0f, (size.Y - h) * 0.5f);
            draw.AddRectFilled(boxMin, boxMin + new Vector2(size.X, h), Theme.U32(color, 0.14f), h * 0.5f);
            draw.AddText(boxMin + new Vector2(8f, 3f), Theme.U32(color), text);
        }

        /// <summary>A section label inside a panel: small, semibold, uppercase, muted.</summary>
        public static void SectionLabel(string text)
        {
            ImGui.Dummy(new Vector2(0f, 2f));
            ImGui.PushFont(Theme.Strong, Theme.SmallSize);
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
            ImGui.TextUnformatted(text.ToUpperInvariant());
            ImGui.PopStyleColor();
            ImGui.PopFont();
        }

        public static void Title(string text)
        {
            ImGui.PushFont(Theme.Strong, Theme.TitleSize);
            ImGui.TextUnformatted(text);
            ImGui.PopFont();
        }

        public static void Muted(string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextSecondary);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }

        public static void MonoText(string text, Vector4? color = null)
        {
            ImGui.PushFont(Theme.Mono, 0f);
            ImGui.PushStyleColor(ImGuiCol.Text, color ?? Theme.Text);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
            ImGui.PopFont();
        }

        public static void Tooltip(string text)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 6f));

            if (ImGui.BeginTooltip())
            {
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
                ImGui.TextUnformatted(text);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }

            ImGui.PopStyleVar();
        }

        /// <summary>
        /// A rounded surface to group content in (pair with <see cref="EndCard"/>). Fields and buttons inside it sit one
        /// step lighter, so they stay visible on the card.
        /// </summary>
        public static bool BeginCard(string id, Vector2 size, bool padded = true, ImGuiWindowFlags flags = ImGuiWindowFlags.None, bool sunken = false,
            bool autoHeight = false)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, sunken ? Theme.Base : Theme.Surface);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.CardRounding);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padded ? new Vector2(14f, 12f) : new Vector2(6f, 6f));
            // autoHeight: the card is as tall as its content (child windows take AutoResizeY, not the window flag)
            var childFlags = ImGuiChildFlags.AlwaysUseWindowPadding | (autoHeight ? ImGuiChildFlags.AutoResizeY : ImGuiChildFlags.None);
            bool open = ImGui.BeginChild(id, size, childFlags, flags);
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();

            ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.Raised);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Theme.Pressed);
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.Raised);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Pressed);

            return open;
        }

        public static void EndCard()
        {
            ImGui.PopStyleColor(4);
            ImGui.EndChild();
        }

        /// <summary>The background of a grid cell: a rounded card that lifts on hover; gold when selected.</summary>
        public static void CellBackground(ImDrawListPtr draw, Vector2 min, Vector2 max, bool selected, bool hovered, uint id)
        {
            float t = Animate(id, hovered ? 1f : 0f, 16f);
            Vector4 bg = selected ? Theme.GoldTint : Theme.Mix(Theme.Surface, Theme.Raised, t);
            draw.AddRectFilled(min, max, Theme.U32(bg), Theme.Rounding);

            if (selected)
            {
                draw.AddRect(min, max, Theme.U32(Theme.Gold), Theme.Rounding, ImDrawFlags.None, 1.5f);
            }
        }

        /// <summary>A keyboard shortcut drawn as a key cap.</summary>
        public static void Kbd(string keys)
        {
            ImGui.PushFont(Theme.Mono, Theme.SmallSize);
            Vector2 textSize = ImGui.CalcTextSize(keys);
            Vector2 min = ImGui.GetCursorScreenPos();
            var size = new Vector2(textSize.X + 10f, textSize.Y + 4f);
            ImGui.Dummy(size);
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(min, min + size, Theme.U32(Theme.Raised), 4f);
            draw.AddText(min + new Vector2(5f, 2f), Theme.U32(Theme.TextSecondary), keys);
            ImGui.PopFont();
        }

        /// <summary>Space between toolbar groups.</summary>
        public static void Gap(float width = 12f)
        {
            ImGui.SameLine(0f, width);
        }
    }
}
