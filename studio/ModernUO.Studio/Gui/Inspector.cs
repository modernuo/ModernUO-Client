using System.Numerics;
using Hexa.NET.ImGui;

namespace ModernUO.Studio.Gui
{
    /// <summary>The pieces every inspector is built from (studio/UX.md, "The inspector shape").</summary>
    internal static class Inspector
    {
        public static readonly Vector4 LinkColor = new(0.45f, 0.68f, 1f, 1f);

        /// <summary>Ids are shown hex first: 0x1F03 (7939).</summary>
        public static string Id(int id) => $"0x{id:X4} ({id})";

        /// <summary>Kind, id (right-click copies) and name.</summary>
        public static void Header(string kind, int id, string name, string idText = null)
        {
            ImGui.TextColored(Widgets.Dim, kind);
            ImGui.SameLine();
            ImGui.TextUnformatted(idText ?? Id(id));

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                ImGui.SetClipboardText(idText ?? $"0x{id:X4}");
                StudioLog.Info($"Copied {idText ?? $"0x{id:X4}"}");
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Right-click to copy");
            }

            if (!string.IsNullOrEmpty(name))
            {
                ImGui.TextWrapped(name);
            }
        }

        public static void Section(string title)
        {
            ImGui.Spacing();
            ImGui.SeparatorText(title);
        }

        /// <summary>Starts a two-column key/value table; pair with <see cref="EndProperties"/>.</summary>
        public static bool BeginProperties(string id = "props")
        {
            if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
            {
                return false;
            }

            ImGui.TableSetupColumn("key", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);

            return true;
        }

        public static void Property(string key, string value)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Widgets.Dim, key);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(value ?? "");
        }

        /// <summary>A property whose value is a link; true when clicked.</summary>
        public static bool PropertyLink(string key, string value)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Widgets.Dim, key);
            ImGui.TableNextColumn();

            return Link(value);
        }

        public static void EndProperties() => ImGui.EndTable();

        /// <summary>
        /// A labelled list of links ("Used in 3 multis"), scrolling past a few rows. <paramref name="open"/> runs for the
        /// clicked entry.
        /// </summary>
        public static void LinkList(string label, IReadOnlyList<int> ids, Func<int, string> text, Action<int> open)
        {
            if (ids.Count == 0)
            {
                ImGui.TextColored(Widgets.Dim, $"{label}: none");

                return;
            }

            ImGui.TextColored(Widgets.Dim, $"{label} ({ids.Count})");
            float rows = MathF.Min(ids.Count, 8);

            if (ImGui.BeginChild(label, new Vector2(0, rows * ImGui.GetTextLineHeightWithSpacing() + 4f)))
            {
                int clicked = -1;

                for (int i = 0; i < ids.Count; i++)
                {
                    ImGui.PushID(i);

                    if (Link(text(ids[i])))
                    {
                        clicked = ids[i];
                    }

                    ImGui.PopID();
                }

                if (clicked >= 0)
                {
                    open(clicked);
                }
            }

            ImGui.EndChild();
        }

        /// <summary>Accent-coloured text that behaves as a link; true when clicked.</summary>
        public static bool Link(string text)
        {
            ImGui.TextColored(LinkColor, text);

            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                ImGui.GetWindowDrawList().AddLine(new Vector2(min.X, max.Y), max, ImGui.ColorConvertFloat4ToU32(LinkColor));
            }

            return ImGui.IsItemClicked(ImGuiMouseButton.Left);
        }
    }
}
