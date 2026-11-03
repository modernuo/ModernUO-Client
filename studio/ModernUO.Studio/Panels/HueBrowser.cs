using System.Numerics;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>hues_raw.bin: every hue's 32 shades and name, and a preview of the hue on any static or gump.</summary>
    internal sealed class HueBrowser : Panel
    {
        private readonly List<int> _rows = new();
        private string _filter = "";
        private bool _dirty = true;
        private int _selected = -1;
        private bool _scrollToSelection;
        private int _previewArt = 0x1F03; // a robe: large and grey, shows a hue well
        private bool _previewGump;
        private bool _partial;
        private CpuImage _preview;
        private (int Hue, int Art, bool Gump, bool Partial) _previewFor = (-1, -1, false, false);

        public HueBrowser(StudioContext context) : base(context, true)
        {
        }

        public override string Title => "Hues###hues";
        public override string MenuName => "Hues";
        protected override Vector2 DefaultSize => new(820, 600);

        public override void OnPackChanged()
        {
            _dirty = true;
            _selected = -1;
            _preview?.Clear();
            _previewFor = (-1, -1, false, false);
        }

        public override void SelectById(int id) => Select(id);

        /// <summary>Selects a hue by its 1-based id.</summary>
        public void Select(int hue)
        {
            Focus();
            _filter = "";
            _dirty = true;
            _selected = (hue & 0x3FFF) - 1;
            _scrollToSelection = true;
        }

        protected override void DrawContent()
        {
            var entries = Context.Assets.HueEntries;
            uint[] lut = Context.Assets.Files.Hues.PackHues;

            if (entries.Length == 0 || lut == null)
            {
                ImGui.TextDisabled("hues_raw.bin did not load (see Pack Info).");

                return;
            }

            ImGui.SetNextItemWidth(220f);
            _dirty |= ImGui.InputTextWithHint("##filter", "hue id or name", ref _filter, 64);

            if (_dirty)
            {
                Rebuild(entries);
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"{_rows.Count} hues. Ids are 1-based: hue N is entry N-1.");

            float detail = 320f;

            if (ImGui.BeginChild("list", new Vector2(ImGui.GetContentRegionAvail().X - detail, 0), ImGuiChildFlags.Borders))
            {
                DrawList(entries, lut);
            }

            ImGui.EndChild();
            ImGui.SameLine();

            if (ImGui.BeginChild("detail", Vector2.Zero, ImGuiChildFlags.Borders))
            {
                if (_selected >= 0 && _selected < entries.Length)
                {
                    DrawDetail(entries, lut, _selected);
                }
                else
                {
                    ImGui.TextDisabled("Select a hue.");
                }
            }

            ImGui.EndChild();
        }

        private void Rebuild(Uo.Pack.HueEntry[] entries)
        {
            _dirty = false;
            _rows.Clear();
            string filter = _filter.Trim();
            bool byId = Widgets.TryParseId(filter, out int id);

            for (int i = 0; i < entries.Length; i++)
            {
                if (filter.Length > 0 && !(byId && i + 1 == id) && !entries[i].NameString().Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _rows.Add(i);
            }
        }

        private void DrawList(Uo.Pack.HueEntry[] entries, uint[] lut)
        {
            var draw = ImGui.GetWindowDrawList();
            float rowHeight = 22f;
            int selectedRow = _selected >= 0 ? _rows.BinarySearch(_selected) : -1;

            if (_scrollToSelection && selectedRow >= 0)
            {
                _scrollToSelection = false;
                ImGui.SetScrollY(selectedRow * rowHeight - ImGui.GetWindowHeight() * 0.4f);
            }

            ImGuiListClipper clipper = new();
            clipper.Begin(_rows.Count, rowHeight);

            while (clipper.Step())
            {
                for (int r = clipper.DisplayStart; r < clipper.DisplayEnd; r++)
                {
                    int i = _rows[r];
                    Vector2 pos = ImGui.GetCursorScreenPos();

                    if (ImGui.Selectable($"##hue{i}", i == _selected, ImGuiSelectableFlags.None, new Vector2(0, rowHeight - 4f)))
                    {
                        _selected = i;
                    }

                    if (ImGui.BeginPopupContextItem())
                    {
                        if (ImGui.MenuItem("Copy hue id"))
                        {
                            ImGui.SetClipboardText((i + 1).ToString());
                        }

                        ImGui.EndPopup();
                    }

                    draw.AddText(pos + new Vector2(4, 2), Widgets.Col(0.85f, 0.85f, 0.85f), $"{i + 1,5}");
                    draw.AddText(pos + new Vector2(56, 2), Widgets.Col(0.6f, 0.6f, 0.6f), entries[i].NameString());
                    DrawStrip(draw, lut, i, pos + new Vector2(220, 1), new Vector2(MathF.Max(64f, ImGui.GetContentRegionAvail().X - 224f), rowHeight - 6f));
                }
            }

            clipper.End();
        }

        private static void DrawStrip(ImDrawListPtr draw, uint[] lut, int entry, Vector2 pos, Vector2 size)
        {
            float w = size.X / 32f;

            for (int s = 0; s < 32; s++)
            {
                // the LUT is RGBA with red in the low byte, the layout of ImGui's IM_COL32
                draw.AddRectFilled(new Vector2(pos.X + s * w, pos.Y), new Vector2(pos.X + (s + 1) * w, pos.Y + size.Y), lut[entry * 32 + s] | 0xFF000000);
            }
        }

        private void DrawDetail(Uo.Pack.HueEntry[] entries, uint[] lut, int i)
        {
            ref var e = ref entries[i];
            ImGui.TextUnformatted($"Hue {i + 1}  (0x{i + 1:X4})");
            ImGui.TextColored(Widgets.Dim, string.IsNullOrEmpty(e.NameString()) ? "(no name)" : e.NameString());
            ImGui.TextUnformatted($"Table start 0x{e.TableStart:X4}, end 0x{e.TableEnd:X4}");

            var draw = ImGui.GetWindowDrawList();
            Vector2 pos = ImGui.GetCursorScreenPos();
            float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
            DrawStrip(draw, lut, i, pos, new Vector2(width, 28f));
            ImGui.InvisibleButton("strip", new Vector2(width, 28f));

            if (ImGui.IsItemHovered())
            {
                int shade = Math.Clamp((int)((ImGui.GetMousePos().X - pos.X) / (width / 32f)), 0, 31);
                ReadOnlySpan<ushort> colors = e.Colors;
                ushort c = colors[shade];
                ImGui.SetTooltip($"shade {shade}\n15-bit 0x{c:X4}  R{(c >> 10) & 31} G{(c >> 5) & 31} B{c & 31}");
            }

            if (ImGui.Button("Copy id"))
            {
                ImGui.SetClipboardText((i + 1).ToString());
            }

            ImGui.Separator();
            ImGui.TextColored(Widgets.Dim, "Preview");
            ImGui.Checkbox("gump", ref _previewGump);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110f);
            ImGui.InputInt(_previewGump ? "gump id" : "static id", ref _previewArt);
            _previewArt = Math.Clamp(_previewArt, 0, 0xFFFF);
            ImGui.Checkbox("partial hue", ref _partial);

            UpdatePreview(i + 1);

            Vector2 min = ImGui.GetCursorScreenPos();
            var size = new Vector2(ImGui.GetContentRegionAvail().X, MathF.Max(120f, ImGui.GetContentRegionAvail().Y - 4f));
            ImGui.Dummy(size);
            Widgets.Checker(draw, min, min + size);

            if (_preview is { IsEmpty: false })
            {
                Widgets.DrawSprite(draw, Context.Renderer, _preview.Sprite, min, min + size, 3f);
            }
        }

        private void UpdatePreview(int hue)
        {
            var key = (hue, _previewArt, _previewGump, _partial);

            if (_previewFor == key)
            {
                return;
            }

            _previewFor = key;
            _preview ??= new CpuImage(Context.Device, Context.Renderer);
            var files = Context.Assets.Files;

            if (_previewGump)
            {
                var info = files.Gumps.GetGump((uint)_previewArt);

                if (info.Pixels.IsEmpty)
                {
                    _preview.Clear();

                    return;
                }

                Images.ApplyHue(info.Pixels, files.Hues.PackHues, hue, _partial);
                _preview.Set(info.Pixels, info.Width, info.Height);
            }
            else
            {
                var info = files.Arts.GetArt((uint)(_previewArt + ClassicUO.Assets.ArtLoader.MAX_LAND_DATA_INDEX_COUNT));

                if (info.Pixels.IsEmpty)
                {
                    _preview.Clear();

                    return;
                }

                Images.ApplyHue(info.Pixels, files.Hues.PackHues, hue, _partial);
                _preview.Set(info.Pixels, info.Width, info.Height);
            }
        }
    }
}
