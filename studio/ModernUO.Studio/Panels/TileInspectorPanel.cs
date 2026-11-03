using System.Numerics;
using ClassicUO.Assets;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>The map's selected tile: its land and its statics, lowest first, each linked to its art and tiledata.
    /// Hovering a static highlights it on the map.</summary>
    internal sealed class TileInspectorPanel : Panel
    {
        private const float Thumb = 44f;

        public TileInspectorPanel(StudioContext context) : base(context, true)
        {
        }

        public override string Title => "Tile###tile";
        public override string MenuName => "Tile Inspector";
        public override string Icon => Icons.Crosshair;
        public override string MenuGroup => "Map";
        protected override Vector2 DefaultSize => new(360, 600);

        public void Show() => IsOpen = true;

        protected override void DrawContent()
        {
            var tile = Context.SelectedTile;

            if (tile == null)
            {
                ImGui.TextDisabled("Click a tile on the map.");

                return;
            }

            var tiles = Context.Assets.Files.TileData;
            Inspector.Header("Tile", 0, $"{MapData.FacetName(tile.Facet)}", $"{tile.X}, {tile.Y}");

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                Context.ShowOnMap(tile.Facet, tile.X, tile.Y);
            }

            if (Ui.Button($"{Icons.Copy}  Copy x y z"))
            {
                ImGui.SetClipboardText($"{tile.X} {tile.Y} {tile.LandZ}");
            }

            ImGui.SameLine();

            if (Ui.Button($"{Icons.Locate}  Centre map"))
            {
                Context.ShowOnMap(tile.Facet, tile.X, tile.Y);
            }

            Inspector.Section("Land");
            string landName = tiles.LandData != null && tile.Land < tiles.LandData.Length ? tiles.LandData[tile.Land].Name : "";

            if (Row(tile.Land & 0x3FFF, true, $"0x{tile.Land:X4}  {landName}", $"z {tile.LandZ}"))
            {
                Context.ShowArt(tile.Land & 0x3FFF, true);
            }

            Inspector.Section($"Statics ({tile.Statics.Count})");

            if (tile.Statics.Count == 0)
            {
                ImGui.TextColored(Widgets.Dim, "None.");
            }

            int hovered = -1;

            // highest first, as the stack reads top to bottom
            for (int i = tile.Statics.Count - 1; i >= 0; i--)
            {
                var s = tile.Statics[i];
                ImGui.PushID(i);
                string name = tiles.StaticData != null && s.Graphic < tiles.StaticData.Length ? tiles.StaticData[s.Graphic].Name : "";
                int height = tiles.StaticData != null && s.Graphic < tiles.StaticData.Length ? tiles.StaticData[s.Graphic].Height : 0;
                string detail = s.Hue != 0 ? $"z {s.Z}, height {height}, hue {s.Hue}" : $"z {s.Z}, height {height}";

                if (Row(s.Graphic, false, $"0x{s.Graphic:X4}  {name}", detail))
                {
                    Context.ShowArt(s.Graphic, false);
                }

                if (ImGui.IsItemHovered())
                {
                    hovered = i;
                }

                if (ImGui.BeginPopupContextItem("st"))
                {
                    if (ImGui.MenuItem("Show art"))
                    {
                        Context.ShowArt(s.Graphic, false);
                    }

                    if (ImGui.MenuItem("Show tiledata"))
                    {
                        Context.ShowTileData(s.Graphic, false);
                    }

                    if (s.Hue != 0 && ImGui.MenuItem("Show hue"))
                    {
                        Context.ShowHue(s.Hue);
                    }

                    ImGui.EndPopup();
                }

                ImGui.PopID();
            }

            Context.HoveredStatic = hovered;

            if (tile.Statics.Count > 0)
            {
                int top = tile.Statics.Max(s => s.Z + (tiles.StaticData != null && s.Graphic < tiles.StaticData.Length ? tiles.StaticData[s.Graphic].Height : 0));
                ImGui.Spacing();
                ImGui.TextColored(Widgets.Dim, $"Top of the stack: z {top}");
            }
        }

        /// <summary>A selectable row with the tile's art; true when clicked.</summary>
        private bool Row(int id, bool land, string line1, string line2)
        {
            Vector2 pos = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.Selectable("##row", false, ImGuiSelectableFlags.None, new Vector2(0, Thumb));
            var draw = ImGui.GetWindowDrawList();
            var art = Context.Assets.Arts;
            draw.AddRectFilled(pos, pos + new Vector2(Thumb, Thumb), Widgets.Col(0.12f, 0.12f, 0.13f));

            if (art != null && (land ? art.RequestLand((uint)id) : art.RequestArt((uint)id)))
            {
                Widgets.DrawSprite(draw, Context.Renderer, land ? art.GetLand((uint)id) : art.GetArt((uint)id), pos + Vector2.One, pos + new Vector2(Thumb - 1, Thumb - 1), 1f);
            }

            float x = pos.X + Thumb + 8f;
            draw.AddText(new Vector2(x, pos.Y + 4f), Widgets.Col(0.9f, 0.9f, 0.9f), line1);
            draw.AddText(new Vector2(x, pos.Y + 6f + ImGui.GetTextLineHeight()), Widgets.Col(0.6f, 0.6f, 0.6f), line2);

            return clicked;
        }
    }
}
