using System.Numerics;
using ClassicUO.Assets;
using Hexa.NET.ImGui;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>tiledata.bin as a table: every land and static entry with its flags, filterable by name, id and flag.</summary>
    internal sealed class TileDataBrowser : Panel
    {
        private readonly List<int> _rows = new();
        private bool _land;
        private string _filter = "";
        private int _flagFilter = -1;
        private bool _dirty = true;
        private int _selected = -1;
        private bool _scrollToSelection;

        public TileDataBrowser(StudioContext context) : base(context, false)
        {
        }

        public override string Title => "TileData###tiledata";
        public override string MenuName => "TileData";
        protected override Vector2 DefaultSize => new(1000, 600);

        public override void OnPackChanged()
        {
            _dirty = true;
            _selected = -1;
        }

        public void Select(int id, bool land)
        {
            Focus();
            _land = land;
            _filter = "";
            _dirty = true;
            _selected = id;
            _scrollToSelection = true;
        }

        protected override void DrawContent()
        {
            var tiles = Context.Assets.Files.TileData;

            if (tiles.StaticData == null)
            {
                ImGui.TextDisabled("tiledata.bin did not load (see Pack Info).");

                return;
            }

            if (ImGui.RadioButton("Statics", !_land))
            {
                _land = false;
                _dirty = true;
            }

            ImGui.SameLine();

            if (ImGui.RadioButton("Land", _land))
            {
                _land = true;
                _dirty = true;
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(220f);
            _dirty |= ImGui.InputTextWithHint("##filter", "id or name", ref _filter, 128);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160f);
            _dirty |= TileFlags.Combo("##flag", ref _flagFilter);

            if (_dirty)
            {
                Rebuild(tiles);
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"{_rows.Count} entries");

            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable
                | ImGuiTableFlags.SizingFixedFit;
            int columns = _land ? 4 : 10;

            if (!ImGui.BeginTable("tiles", columns, flags))
            {
                return;
            }

            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 200f);

            if (_land)
            {
                ImGui.TableSetupColumn("TexId", ImGuiTableColumnFlags.WidthFixed, 60f);
            }
            else
            {
                ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("Height", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("Layer", ImGuiTableColumnFlags.WidthFixed, 45f);
                ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 45f);
                ImGui.TableSetupColumn("Anim", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableSetupColumn("Hue", ImGuiTableColumnFlags.WidthFixed, 45f);
                ImGui.TableSetupColumn("Light", ImGuiTableColumnFlags.WidthFixed, 45f);
            }

            ImGui.TableSetupColumn("Flags", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            int selectedRow = _selected >= 0 ? _rows.BinarySearch(_selected) : -1;

            if (_scrollToSelection && selectedRow >= 0)
            {
                _scrollToSelection = false;
                ImGui.SetScrollY(selectedRow * ImGui.GetTextLineHeightWithSpacing() - ImGui.GetWindowHeight() * 0.4f);
            }

            ImGuiListClipper clipper = new();
            clipper.Begin(_rows.Count);

            while (clipper.Step())
            {
                for (int r = clipper.DisplayStart; r < clipper.DisplayEnd; r++)
                {
                    int id = _rows[r];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    if (ImGui.Selectable($"0x{id:X4}##{id}", id == _selected, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        _selected = id;
                    }

                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        Context.ShowArt(id, _land);
                    }

                    if (_land)
                    {
                        ref var t = ref tiles.LandData[id];
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(t.Name ?? "");
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"0x{t.TexID:X4}");
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(TileFlags.Describe((ulong)t.Flags));
                    }
                    else
                    {
                        ref var t = ref tiles.StaticData[id];
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(t.Name ?? "");
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(t.Weight.ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(t.Height.ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(t.Layer.ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(t.Count.ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(t.AnimID == 0 ? "" : $"0x{t.AnimID:X4}");
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(t.Hue == 0 ? "" : t.Hue.ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(t.LightIndex == 0 ? "" : t.LightIndex.ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(TileFlags.Describe((ulong)t.Flags));
                    }
                }
            }

            clipper.End();
            ImGui.EndTable();
        }

        private void Rebuild(TileDataLoader tiles)
        {
            _dirty = false;
            _rows.Clear();

            string filter = _filter.Trim();
            bool byId = Widgets.TryParseId(filter, out int filterId);
            int count = _land ? tiles.LandData.Length : tiles.StaticData.Length;
            ulong flag = _flagFilter < 0 ? 0 : 1UL << _flagFilter;

            for (int id = 0; id < count; id++)
            {
                string name = _land ? tiles.LandData[id].Name : tiles.StaticData[id].Name;
                ulong flags = _land ? (ulong)tiles.LandData[id].Flags : (ulong)tiles.StaticData[id].Flags;

                if (flag != 0 && (flags & flag) == 0)
                {
                    continue;
                }

                if (filter.Length > 0 && !(byId && id == filterId) && !(name != null && name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _rows.Add(id);
            }
        }

        /// <summary>The tiledata fields of one entry, for the art browser's details.</summary>
        public static void DrawFields(StudioContext context, int id, bool land)
        {
            var tiles = context.Assets.Files.TileData;

            if (land)
            {
                if (tiles.LandData == null || id >= tiles.LandData.Length)
                {
                    ImGui.TextDisabled("No tiledata entry.");

                    return;
                }

                ref var t = ref tiles.LandData[id];
                Field("Name", t.Name);
                Field("Texmap", $"0x{t.TexID:X4}");
                FlagList((ulong)t.Flags);

                return;
            }

            if (tiles.StaticData == null || id >= tiles.StaticData.Length)
            {
                ImGui.TextDisabled("No tiledata entry.");

                return;
            }

            ref var s = ref tiles.StaticData[id];
            Field("Name", s.Name);
            Field("Weight", s.Weight.ToString());
            Field("Height", s.Height.ToString());
            Field("Layer", s.Layer.ToString());
            Field("Count", s.Count.ToString());
            Field("Anim", $"0x{s.AnimID:X4}");
            Field("Hue", s.Hue.ToString());
            Field("Light", s.LightIndex.ToString());
            FlagList((ulong)s.Flags);
        }

        private static void Field(string name, string value)
        {
            ImGui.TextColored(Widgets.Dim, name);
            ImGui.SameLine(80f);
            ImGui.TextUnformatted(value ?? "");
        }

        private static void FlagList(ulong flags)
        {
            ImGui.TextColored(Widgets.Dim, "Flags");

            if (flags == 0)
            {
                ImGui.SameLine(80f);
                ImGui.TextUnformatted("None");

                return;
            }

            for (int bit = 0; bit < 64; bit++)
            {
                if ((flags & (1UL << bit)) != 0)
                {
                    ImGui.BulletText(TileFlags.Names[bit]);
                }
            }
        }
    }
}
