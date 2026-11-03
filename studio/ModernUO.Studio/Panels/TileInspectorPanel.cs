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

            // land edits: height, and the Art browser's land tile as a brush
            RowButtons(out bool landUp, out bool landDown, out bool _, false);
            int brushLand = Context.Get<ArtBrowser>()?.BrushLand ?? -1;

            if (landUp || landDown)
            {
                int step = (ImGui.GetIO().KeyShift ? 5 : 1) * (landUp ? 1 : -1);
                Edit(tile, $"Land z {(step > 0 ? "+" : "")}{step}", tx => tx.SetLand(tile.X, tile.Y, tile.Land, (sbyte)Math.Clamp(tile.LandZ + step, -128, 127)));
            }

            if (brushLand >= 0 && brushLand != tile.Land && Ui.Button($"{Icons.Paintbrush}  Paint with 0x{brushLand:X4}", ButtonKind.Ghost))
            {
                Edit(tile, $"Paint land 0x{brushLand:X4}", tx => tx.SetLand(tile.X, tile.Y, (ushort)brushLand, tile.LandZ));
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

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenOverlappedByItem))
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

                    ImGui.Separator();

                    if (ImGui.MenuItem($"{Icons.Trash}  Delete"))
                    {
                        EditStatic(tile, s, $"Delete 0x{s.Graphic:X4}", null);
                    }

                    ImGui.EndPopup();
                }

                RowButtons(out bool up, out bool down, out bool delete, true);

                if (up || down)
                {
                    int step = (ImGui.GetIO().KeyShift ? 5 : 1) * (up ? 1 : -1);
                    EditStatic(tile, s, $"0x{s.Graphic:X4} z {(step > 0 ? "+" : "")}{step}", c =>
                    {
                        c.Z = (sbyte)Math.Clamp(c.Z + step, -128, 127);

                        return c;
                    });
                }

                if (delete)
                {
                    EditStatic(tile, s, $"Delete 0x{s.Graphic:X4}", null);
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

        // the buttons on the right of the row just drawn: z up, z down and (for statics) delete; Shift steps by 5
        private static void RowButtons(out bool up, out bool down, out bool delete, bool canDelete)
        {
            Vector2 after = ImGui.GetCursorScreenPos();
            Vector2 rowMin = ImGui.GetItemRectMin(), rowMax = ImGui.GetItemRectMax();
            float w = Ui.Height, gap = 2f;
            int count = canDelete ? 3 : 2;
            ImGui.SetCursorScreenPos(new Vector2(rowMax.X - count * (w + gap), rowMin.Y + (rowMax.Y - rowMin.Y - w) * 0.5f));
            up = Ui.IconButton("zup", Icons.ArrowUp, "Raise z (Shift: 5)");
            ImGui.SameLine(0f, gap);
            down = Ui.IconButton("zdown", Icons.ArrowDown, "Lower z (Shift: 5)");
            delete = false;

            if (canDelete)
            {
                ImGui.SameLine(0f, gap);
                delete = Ui.IconButton("del", Icons.Trash, "Delete");
            }

            ImGui.SetCursorScreenPos(after);
        }

        private void Edit(MapTile tile, string label, Action<MapEdits.Transaction> apply)
        {
            var tx = Context.Assets.Edits.Begin(label, tile.Facet);
            apply(tx);
            tx.Commit();
        }

        /// <summary>Changes (or with a null <paramref name="change"/>, deletes) one static of the tile: the first stored
        /// static with its graphic, z and hue.</summary>
        private void EditStatic(MapTile tile, MapStatic target, string label, Func<Uo.Pack.StaticCell, Uo.Pack.StaticCell> change)
        {
            Edit(tile, label, tx =>
            {
                if (!tx.TryGet(tile.X, tile.Y, out _, out var statics))
                {
                    return;
                }

                var list = statics.ToList();
                int i = list.FindIndex(c => c.Graphic == target.Graphic && c.Z == target.Z && c.Hue == target.Hue);

                if (i < 0)
                {
                    return;
                }

                if (change == null)
                {
                    list.RemoveAt(i);
                }
                else
                {
                    list[i] = change(list[i]);
                }

                tx.SetStatics(tile.X, tile.Y, list);
            });
        }

        /// <summary>A selectable row with the tile's art; true when clicked. The buttons drawn over its right end take their own clicks.</summary>
        private bool Row(int id, bool land, string line1, string line2)
        {
            Vector2 pos = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.Selectable("##row", false, ImGuiSelectableFlags.AllowOverlap, new Vector2(0, Thumb));
            var draw = ImGui.GetWindowDrawList();
            var art = Context.Assets.Arts;
            draw.AddRectFilled(pos, pos + new Vector2(Thumb, Thumb), Theme.U32(Theme.Surface), 4f);

            if (art != null && (land ? art.RequestLand((uint)id) : art.RequestArt((uint)id)))
            {
                Widgets.DrawSprite(draw, Context.Renderer, land ? art.GetLand((uint)id) : art.GetArt((uint)id), pos + Vector2.One, pos + new Vector2(Thumb - 1, Thumb - 1), 1f);
            }

            float x = pos.X + Thumb + 8f;
            draw.AddText(new Vector2(x, pos.Y + 4f), Theme.U32(Theme.Text), line1);
            draw.AddText(new Vector2(x, pos.Y + 6f + ImGui.GetTextLineHeight()), Theme.U32(Theme.TextSecondary), line2);

            return clicked;
        }
    }
}
