using System.Numerics;
using ClassicUO.Renderer;
using Hexa.NET.ImGui;

namespace ModernUO.Studio.Gui
{
    internal static class Widgets
    {
        public static readonly Vector4 Dim = new(0.6f, 0.6f, 0.6f, 1f);
        public static readonly Vector4 Warn = new(1f, 0.75f, 0.3f, 1f);
        public static readonly Vector4 Bad = new(1f, 0.4f, 0.4f, 1f);
        public static readonly Vector4 Good = new(0.5f, 0.9f, 0.5f, 1f);

        public static uint Col(float r, float g, float b, float a = 1f) => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));

        /// <summary>
        /// Draws a sprite into [min, max], scaled to fit (never above <paramref name="maxScale"/>) and centred. Returns the
        /// rectangle it covered. Mirror flips it horizontally (UO directions 5..7 are mirrors of 3..1).
        /// </summary>
        public static (Vector2 Min, Vector2 Max) DrawSprite(ImDrawListPtr draw, ImGuiRenderer renderer, in SpriteInfo sprite,
            Vector2 min, Vector2 max, float maxScale = 1f, bool mirror = false, uint tint = 0xFFFFFFFF)
        {
            if (sprite.Texture == null || sprite.UV.Width <= 0 || sprite.UV.Height <= 0)
            {
                return (min, min);
            }

            Vector2 box = max - min;
            float scale = MathF.Min(maxScale, MathF.Min(box.X / sprite.UV.Width, box.Y / sprite.UV.Height));
            var size = new Vector2(sprite.UV.Width * scale, sprite.UV.Height * scale);
            Vector2 p0 = min + (box - size) * 0.5f;
            p0 = new Vector2(MathF.Floor(p0.X), MathF.Floor(p0.Y));
            Vector2 p1 = p0 + size;

            GetUv(sprite, mirror, out Vector2 uv0, out Vector2 uv1);
            draw.AddImage(renderer.Bind(sprite.Texture), p0, p1, uv0, uv1, tint);

            return (p0, p1);
        }

        /// <summary>Draws a sprite with its top-left at <paramref name="pos"/> at the given scale.</summary>
        public static void DrawSpriteAt(ImDrawListPtr draw, ImGuiRenderer renderer, in SpriteInfo sprite, Vector2 pos, float scale,
            bool mirror = false, uint tint = 0xFFFFFFFF)
        {
            if (sprite.Texture == null)
            {
                return;
            }

            GetUv(sprite, mirror, out Vector2 uv0, out Vector2 uv1);
            draw.AddImage(renderer.Bind(sprite.Texture), pos, pos + new Vector2(sprite.UV.Width, sprite.UV.Height) * scale, uv0, uv1, tint);
        }

        public static void GetUv(in SpriteInfo sprite, bool mirror, out Vector2 uv0, out Vector2 uv1)
        {
            float tw = sprite.Texture.Width, th = sprite.Texture.Height;
            uv0 = new Vector2(sprite.UV.X / tw, sprite.UV.Y / th);
            uv1 = new Vector2((sprite.UV.X + sprite.UV.Width) / tw, (sprite.UV.Y + sprite.UV.Height) / th);

            if (mirror)
            {
                (uv0.X, uv1.X) = (uv1.X, uv0.X);
            }
        }

        /// <summary>A checkerboard behind transparent pixels.</summary>
        public static void Checker(ImDrawListPtr draw, Vector2 min, Vector2 max, float cell = 8f)
        {
            uint a = Col(0.16f, 0.16f, 0.17f), b = Col(0.21f, 0.21f, 0.22f);
            draw.AddRectFilled(min, max, a);
            draw.PushClipRect(min, max, true);

            for (float y = min.Y, row = 0; y < max.Y; y += cell, row++)
            {
                for (float x = min.X + (row % 2) * cell; x < max.X; x += cell * 2)
                {
                    draw.AddRectFilled(new Vector2(x, y), new Vector2(x + cell, y + cell), b);
                }
            }

            draw.PopClipRect();
        }

        /// <summary>A placeholder for a sprite that is still decoding on a worker.</summary>
        public static void Pending(ImDrawListPtr draw, Vector2 min, Vector2 max)
        {
            Vector2 c = (min + max) * 0.5f;
            float t = (float)ImGui.GetTime() * 4f;
            draw.AddCircle(c, MathF.Min(max.X - min.X, max.Y - min.Y) * 0.15f, Col(0.5f, 0.5f, 0.5f, 0.5f + 0.5f * MathF.Sin(t)), 12, 2f);
        }

        /// <summary>Parses "1234", "0x4D2" or "4D2h"; false for anything else.</summary>
        public static bool TryParseId(string text, out int id)
        {
            id = 0;
            text = text?.Trim();

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out id);
            }

            if (text.EndsWith('h') || text.EndsWith('H'))
            {
                return int.TryParse(text.AsSpan(0, text.Length - 1), System.Globalization.NumberStyles.HexNumber, null, out id);
            }

            return int.TryParse(text, out id);
        }

        public static void HelpMarker(string text)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");

            if (ImGui.BeginItemTooltip())
            {
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
                ImGui.TextUnformatted(text);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }

        public static void CopyableId(string label, int id)
        {
            ImGui.TextUnformatted($"{label}: 0x{id:X4} ({id})");

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                ImGui.SetClipboardText($"0x{id:X4}");
                StudioLog.Info($"Copied 0x{id:X4}");
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Right-click to copy");
            }
        }
    }
}
