using System.Numerics;
using System.Text;
using Hexa.NET.ImGui;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>The cliloc string tables (text/cliloc.bin): search by number or text.</summary>
    internal sealed class ClilocBrowser : Panel
    {
        private readonly List<int> _rows = new();
        private string _filter = "";
        private bool _dirty = true;
        private bool _english = true;

        public ClilocBrowser(StudioContext context) : base(context, false)
        {
        }

        public override string Title => "Cliloc###cliloc";
        public override string MenuName => "Cliloc";
        protected override Vector2 DefaultSize => new(900, 560);

        public override void OnPackChanged() => _dirty = true;

        protected override void DrawContent()
        {
            var clilocs = Context.Assets.Files.Clilocs;
            // with English as the language the English strings are the language table and EnglishTable stays empty
            var table = _english ? clilocs.EnglishTable ?? clilocs.LanguageTable : clilocs.LanguageTable;

            if (table == null)
            {
                ImGui.TextDisabled("text/cliloc.bin did not load (see Pack Info).");

                return;
            }

            ImGui.SetNextItemWidth(300f);
            _dirty |= ImGui.InputTextWithHint("##filter", "number or text", ref _filter, 256);

            if (clilocs.LanguageTable != null && clilocs.EnglishTable != null && !ReferenceEquals(clilocs.LanguageTable, clilocs.EnglishTable))
            {
                ImGui.SameLine();
                _dirty |= ImGui.Checkbox("English", ref _english);
            }

            if (_dirty)
            {
                Rebuild(table);
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"{_rows.Count} of {table.Count} strings ({clilocs.Language})");

            if (!ImGui.BeginTable("cliloc", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
            {
                return;
            }

            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Number", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Text", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            ImGuiListClipper clipper = new();
            clipper.Begin(_rows.Count);

            while (clipper.Step())
            {
                for (int r = clipper.DisplayStart; r < clipper.DisplayEnd; r++)
                {
                    int index = _rows[r];
                    int number = table.IdAt(index);
                    string text = Encoding.UTF8.GetString(table.Utf8At(index));
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    if (ImGui.Selectable($"{number}##{index}", false, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        ImGui.SetClipboardText(number.ToString());
                        StudioLog.Info($"Copied cliloc {number}");
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(text);
                }
            }

            clipper.End();
            ImGui.EndTable();
        }

        private void Rebuild(Uo.Pack.ClilocTable table)
        {
            _dirty = false;
            _rows.Clear();
            string filter = _filter.Trim();
            bool byNumber = int.TryParse(filter, out int number);

            for (int i = 0; i < table.Count; i++)
            {
                if (filter.Length > 0)
                {
                    bool match = byNumber && table.IdAt(i).ToString().StartsWith(filter, StringComparison.Ordinal);

                    if (!match && !Encoding.UTF8.GetString(table.Utf8At(i)).Contains(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                _rows.Add(i);
            }
        }
    }
}
