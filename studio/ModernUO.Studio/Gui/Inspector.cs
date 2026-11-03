using System.Numerics;
using Hexa.NET.ImGui;

namespace ModernUO.Studio.Gui
{
    /// <summary>The pieces every inspector is built from (studio/UX.md, "The inspector shape").</summary>
    internal static class Inspector
    {
        public static Vector4 LinkColor => Theme.Blue;

        /// <summary>Ids are shown hex first: 0x1F03 (7939).</summary>
        public static string Id(int id) => $"0x{id:X4} ({id})";

        /// <summary>Kind (small, muted), the name as the title, and the id in monospace (right-click copies).</summary>
        public static void Header(string kind, int id, string name, string idText = null)
        {
            Ui.SectionLabel(kind);

            if (!string.IsNullOrEmpty(name))
            {
                ImGui.PushFont(Theme.Strong, Theme.TitleSize);
                ImGui.PushTextWrapPos(0f);
                ImGui.TextUnformatted(name);
                ImGui.PopTextWrapPos();
                ImGui.PopFont();
            }

            string text = idText ?? Id(id);
            Ui.MonoText(text, string.IsNullOrEmpty(name) ? Theme.Text : Theme.Gold);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                ImGui.SetClipboardText(idText ?? $"0x{id:X4}");
                StudioLog.Info($"Copied {idText ?? $"0x{id:X4}"}");
            }

            if (ImGui.IsItemHovered())
            {
                Ui.Tooltip("Right-click to copy");
            }
        }

        public static void Section(string title)
        {
            ImGui.Dummy(new Vector2(0f, 6f));
            Ui.SectionLabel(title);
        }

        /// <summary>Starts a two-column key/value table; pair with <see cref="EndProperties"/>.</summary>
        public static bool BeginProperties(string id = "props")
        {
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(0f, 4f));

            if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.PopStyleVar();

                return false;
            }

            ImGui.TableSetupColumn("key", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);

            return true;
        }

        public static void Property(string key, string value)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextSecondary, key);
            ImGui.TableNextColumn();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(value ?? "");
            ImGui.PopTextWrapPos();
        }

        /// <summary>A property whose value is a link; true when clicked.</summary>
        public static bool PropertyLink(string key, string value)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextSecondary, key);
            ImGui.TableNextColumn();

            return Link(value);
        }

        public static void EndProperties()
        {
            ImGui.EndTable();
            ImGui.PopStyleVar();
        }

        /// <summary>
        /// A labelled list of links ("Used in 3 multis"), scrolling past a few rows. <paramref name="open"/> runs for the
        /// clicked entry.
        /// </summary>
        public static void LinkList(string label, IReadOnlyList<int> ids, Func<int, string> text, Action<int> open)
        {
            if (ids.Count == 0)
            {
                ImGui.TextColored(Theme.TextMuted, $"{label}: none");

                return;
            }

            ImGui.TextColored(Theme.TextSecondary, $"{label}  ·  {ids.Count}");
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

        /// <summary>muo-blue text that behaves as a link; true when clicked.</summary>
        public static bool Link(string text)
        {
            ImGui.TextColored(LinkColor, text);

            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                ImGui.GetWindowDrawList().AddLine(new Vector2(min.X, max.Y), max, Theme.U32(LinkColor, 0.6f));
            }

            return ImGui.IsItemClicked(ImGuiMouseButton.Left);
        }
    }
}
