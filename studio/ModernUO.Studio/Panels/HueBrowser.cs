using System.Numerics;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>
    /// hues_raw.bin: every hue's 32 shades and name, and a preview of the hue on any static or gump. Items are entry
    /// indexes; the hue id players and scripts use is the index + 1.
    /// </summary>
    internal sealed class HueBrowser : AssetBrowser
    {
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

        private Uo.Pack.HueEntry[] Entries => Context.Assets.HueEntries;
        private uint[] Lut => Context.Assets.Files.Hues.PackHues;

        protected override string Kind => "Hue";
        protected override string SearchHint => "hue id (33, 0x21) or name";
        protected override float DefaultCell => 48f;

        protected override int SlotCount => Context.Assets == null || Lut == null ? 0 : Entries.Length;

        protected override int IdOf(int item) => item + 1;

        protected override string FormatId(int item) => Inspector.Id(item + 1);

        protected override string CopyTextOf(int item) => (item + 1).ToString();

        protected override string NameOf(int item) => Entries[item].NameString();

        protected override int ExtraColumns => 1;

        protected override void SetupExtraColumns() => ImGui.TableSetupColumn("Shades", ImGuiTableColumnFlags.WidthStretch);

        protected override void DrawExtraColumns(int item)
        {
            ImGui.TableNextColumn();
            Vector2 pos = ImGui.GetCursorScreenPos();
            float width = MathF.Max(32f, ImGui.GetContentRegionAvail().X);
            DrawStrip(ImGui.GetWindowDrawList(), item, pos, new Vector2(width, ImGui.GetTextLineHeight()));
        }

        /// <summary>The cell: the 32 shades as a gradient, darkest on the left.</summary>
        protected override void DrawThumbnail(ImDrawListPtr draw, int item, Vector2 min, Vector2 max) => DrawStrip(draw, item, min, max - min);

        public override void OnPackChanged()
        {
            base.OnPackChanged();
            _preview?.Clear();
            _previewFor = (-1, -1, false, false);
        }

        /// <summary>Selects a hue by its 1-based id.</summary>
        public void Select(int hue) => SelectItem((hue & 0x3FFF) - 1);

        public override void SelectById(int id) => Select(id);

        private void DrawStrip(ImDrawListPtr draw, int item, Vector2 pos, Vector2 size)
        {
            float w = size.X / 32f;

            for (int s = 0; s < 32; s++)
            {
                // the LUT is RGBA with red in the low byte, the layout of ImGui's IM_COL32
                draw.AddRectFilled(new Vector2(pos.X + s * w, pos.Y), new Vector2(pos.X + (s + 1) * w, pos.Y + size.Y), Lut[item * 32 + s] | 0xFF000000);
            }
        }

        protected override void DrawPreview(int item)
        {
            var draw = ImGui.GetWindowDrawList();
            Vector2 pos = ImGui.GetCursorScreenPos();
            float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
            DrawStrip(draw, item, pos, new Vector2(width, 28f));
            ImGui.InvisibleButton("strip", new Vector2(width, 28f));

            if (ImGui.IsItemHovered())
            {
                int shade = Math.Clamp((int)((ImGui.GetMousePos().X - pos.X) / (width / 32f)), 0, 31);
                ReadOnlySpan<ushort> colors = Entries[item].Colors;
                ushort c = colors[shade];
                ImGui.SetTooltip($"shade {shade}\n15-bit 0x{c:X4}  R{(c >> 10) & 31} G{(c >> 5) & 31} B{c & 31}");
            }

            ImGui.Checkbox("gump", ref _previewGump);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110f);
            ImGui.InputInt(_previewGump ? "gump id" : "static id", ref _previewArt);
            _previewArt = Math.Clamp(_previewArt, 0, 0xFFFF);
            ImGui.SameLine();
            ImGui.Checkbox("partial", ref _partial);

            UpdatePreview(item + 1);

            Vector2 min = ImGui.GetCursorScreenPos();
            var size = new Vector2(width, 240f);
            ImGui.Dummy(size);
            Widgets.Checker(draw, min, min + size);

            if (_preview is { IsEmpty: false })
            {
                Widgets.DrawSprite(draw, Context.Renderer, _preview.Sprite, min, min + size, 3f);
            }
        }

        protected override void DrawProperties(int item)
        {
            ref var e = ref Entries[item];

            if (Inspector.BeginProperties())
            {
                Inspector.Property("Entry", $"{item} (hue id {item + 1})");
                Inspector.Property("Table start", $"0x{e.TableStart:X4}");
                Inspector.Property("Table end", $"0x{e.TableEnd:X4}");
                Inspector.EndProperties();
            }
        }

        protected override void DrawActions(int item)
        {
            if (ImGui.Button("Copy hue id"))
            {
                ImGui.SetClipboardText((item + 1).ToString());
            }

            ImGui.SameLine();

            if (ImGui.Button(_previewGump ? "Open gump" : "Open static"))
            {
                if (_previewGump)
                {
                    Context.ShowGump(_previewArt);
                }
                else
                {
                    Context.ShowArt(_previewArt, false);
                }
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
