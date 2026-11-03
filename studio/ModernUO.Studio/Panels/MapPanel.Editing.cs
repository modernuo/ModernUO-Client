using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;
using Uo.Pack;
using Vector2 = System.Numerics.Vector2;

namespace ModernUO.Studio.Panels
{
    internal enum MapTool
    {
        Select,
        Draw,
        Terrain,
        Erase
    }

    internal enum TerrainOp
    {
        Raise,
        Lower,
        Flatten
    }

    /// <summary>
    /// The map's editing tools (studio/UX.md, "Editing model"). Draw uses the Art browser's brush (a static places it, a
    /// land tile paints it), Terrain raises, lowers or flattens land, Erase clears statics. The tool shows its result
    /// under the cursor as a preview; a click commits it, a drag keeps painting, and one stroke is one change.
    /// </summary>
    internal sealed partial class MapPanel
    {
        private const ushort PreviewHue = 0x0035;
        private const int PreviewAlpha = 150;

        private MapTool _tool = MapTool.Select;
        private TerrainOp _terrain = TerrainOp.Raise;
        private int _brush;
        private int _amount = 1;
        private bool _stackOnTop;
        private MapEdits.Transaction _stroke;
        private MapEdits.Transaction _preview;
        private (int, int, MapTool, TerrainOp, int, int, bool, int, bool) _previewKey;
        private readonly HashSet<(int X, int Y)> _strokeTiles = new();
        private readonly List<(int X, int Y, sbyte Z, ushort Graphic)> _placed = new();
        private sbyte _flattenZ;

        /// <summary>Picking art picks the brush: the map switches to Draw (unless a stroke is in progress).</summary>
        public void UseDrawTool()
        {
            if (_stroke == null)
            {
                _tool = MapTool.Draw;
            }
        }

        private ArtBrowser Art => Context.Get<ArtBrowser>();

        /// <summary>-tool: the active tool at startup (smoke tests).</summary>
        public void SetTool(MapTool tool) => _tool = tool;

        private static readonly (MapTool Tool, string Icon, string Tip)[] _tools =
        {
            (MapTool.Select, Icons.Pointer, "Select (V): click picks a tile, drag pans"),
            (MapTool.Draw, Icons.Paintbrush, "Draw (D): places the static or paints the land tile picked in Art (Alt+click on the map picks it)"),
            (MapTool.Terrain, Icons.Mountain, "Terrain: raise (R), lower (L) or flatten (F) land"),
            (MapTool.Erase, Icons.Eraser, "Erase statics (X)")
        };

        /// <summary>The tool palette and the active tool's options, in a pill sized to its contents.</summary>
        private void DrawToolsOverlay()
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Panel with { W = 0.92f });
            ImGui.PushStyleColor(ImGuiCol.Border, Theme.Hairline);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.Rounding + 2f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4f, 4f));

            if (ImGui.BeginChild("tools", Vector2.Zero,
                ImGuiChildFlags.AutoResizeX | ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding | ImGuiChildFlags.Borders,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings))
            {
                DrawTools();
            }

            ImGui.EndChild();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
        }

        private void DrawTools()
        {
            foreach (var (tool, icon, tip) in _tools)
            {
                if (Ui.IconButton($"tool{tool}", icon, tip, _tool == tool))
                {
                    _tool = tool;
                }

                ImGui.SameLine(0f, 2f);
            }

            var edits = Context.Assets.Edits;
            Ui.Gap(16f);

            if (Ui.IconButton("undo", Icons.Undo, edits.CanUndo ? $"Undo {edits.NextUndo.Label} (Ctrl+Z)" : "Nothing to undo", enabled: edits.CanUndo))
            {
                edits.Undo();
            }

            ImGui.SameLine(0f, 2f);

            if (Ui.IconButton("redo", Icons.Redo, edits.CanRedo ? $"Redo {edits.NextRedo.Label} (Ctrl+Y)" : "Nothing to redo", enabled: edits.CanRedo))
            {
                edits.Redo();
            }

            // each tool's options continue the row; Select has none, so the row ends at redo (a dangling SameLine would
            // put the map canvas on this line)
            switch (_tool)
            {
                case MapTool.Draw:
                    Ui.Gap(16f);
                    DrawBrushChip();

                    if (Art is { HasBrush: true, BrushIsLand: false })
                    {
                        Ui.Gap();
                        Ui.Toggle("On top", ref _stackOnTop, "Place on top of the tile's statics instead of on the ground");
                    }
                    else if (Art is { HasBrush: true, BrushIsLand: true })
                    {
                        Ui.Gap();
                        Stepper("brush", ref _brush, 0, 8, v => v == 0 ? "1 tile" : $"{v * 2 + 1}x{v * 2 + 1}", "Brush size ([ and ])");
                    }

                    break;

                case MapTool.Terrain:
                    Ui.Gap(16f);
                    int op = (int)_terrain;

                    if (Ui.Segmented("terrain", ref op, $"{Icons.ChevronsUp}  Raise", $"{Icons.ChevronsDown}  Lower", $"{Icons.Equal}  Flatten"))
                    {
                        _terrain = (TerrainOp)op;
                    }

                    Ui.Gap();
                    Stepper("brush", ref _brush, 0, 8, v => v == 0 ? "1 tile" : $"{v * 2 + 1}x{v * 2 + 1}", "Brush size ([ and ])");

                    if (_terrain != TerrainOp.Flatten)
                    {
                        Ui.Gap();
                        Stepper("amount", ref _amount, 1, 20, v => $"{v} z", "Height step per tile");
                    }

                    break;

                case MapTool.Erase:
                    Ui.Gap(16f);
                    Stepper("brush", ref _brush, 0, 8, v => v == 0 ? "1 tile" : $"{v * 2 + 1}x{v * 2 + 1}", "Brush size ([ and ])");

                    break;
            }
        }

        private void DrawBrushChip()
        {
            var art = Art;

            if (art is not { HasBrush: true })
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Amber, "Pick a static or land tile in Art, or Alt+click the map");

                return;
            }

            var tiles = Context.Assets.Files.TileData;
            int id = art.BrushIsLand ? art.BrushLand : art.BrushStatic;
            string name = art.BrushIsLand
                ? tiles.LandData != null && id < tiles.LandData.Length ? tiles.LandData[id].Name : ""
                : tiles.StaticData != null && id < tiles.StaticData.Length ? tiles.StaticData[id].Name : "";

            // the brush's art in a small square, then what it is
            var draw = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            float size = Ui.Height;
            ImGui.Dummy(new Vector2(size, size));
            draw.AddRectFilled(min, min + new Vector2(size, size), Theme.U32(Theme.Surface), Theme.Rounding);
            var atlas = Context.Assets.Arts;

            if (atlas != null && (art.BrushIsLand ? atlas.RequestLand((uint)id) : atlas.RequestArt((uint)id)))
            {
                Widgets.DrawSprite(draw, Context.Renderer, art.BrushIsLand ? atlas.GetLand((uint)id) : atlas.GetArt((uint)id),
                    min + new Vector2(2f, 2f), min + new Vector2(size - 2f, size - 2f), 1f);
            }

            ImGui.SameLine(0f, 6f);
            Ui.Chip($"{(art.BrushIsLand ? "Land" : "Static")} 0x{id:X4} {name}", Theme.Gold);
        }

        private static void Stepper(string id, ref int value, int min, int max, Func<int, string> text, string tooltip)
        {
            if (Ui.IconButton(id + "-", Icons.Minus, tooltip, enabled: value > min))
            {
                value--;
            }

            ImGui.SameLine(0f, 2f);
            ImGui.AlignTextToFramePadding();
            ImGui.PushFont(Theme.Mono, 0f);
            ImGui.TextColored(Theme.TextSecondary, text(value));
            ImGui.PopFont();
            ImGui.SameLine(0f, 2f);

            if (Ui.IconButton(id + "+", Icons.Plus, tooltip, enabled: value < max))
            {
                value++;
            }
        }

        // tool keys, as in most editors: V select, D draw, R raise, L lower, F flatten, X erase; [ and ] brush size
        private void ToolKeys()
        {
            if (ImGui.IsKeyPressed(ImGuiKey.V)) _tool = MapTool.Select;
            if (ImGui.IsKeyPressed(ImGuiKey.D)) _tool = MapTool.Draw;
            if (ImGui.IsKeyPressed(ImGuiKey.X)) _tool = MapTool.Erase;

            if (ImGui.IsKeyPressed(ImGuiKey.R)) (_tool, _terrain) = (MapTool.Terrain, TerrainOp.Raise);
            if (ImGui.IsKeyPressed(ImGuiKey.L)) (_tool, _terrain) = (MapTool.Terrain, TerrainOp.Lower);
            if (ImGui.IsKeyPressed(ImGuiKey.F)) (_tool, _terrain) = (MapTool.Terrain, TerrainOp.Flatten);

            if (ImGui.IsKeyPressed(ImGuiKey.LeftBracket)) _brush = Math.Max(0, _brush - 1);
            if (ImGui.IsKeyPressed(ImGuiKey.RightBracket)) _brush = Math.Min(8, _brush + 1);
        }

        private bool PlacesStatics => _tool == MapTool.Draw && Art is { HasBrush: true, BrushIsLand: false };

        private IEnumerable<(int X, int Y)> BrushTiles(int x, int y)
        {
            int r = PlacesStatics ? 0 : _brush;

            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    yield return (x + dx, y + dy);
                }
            }
        }

        private void DrawBrush(ImDrawListPtr draw, Vector2 min)
        {
            var color = _tool switch
            {
                MapTool.Erase => Theme.Red,
                MapTool.Terrain => Theme.Blue,
                _ => Theme.Gold
            };

            foreach (var (x, y) in BrushTiles(_hoverX, _hoverY))
            {
                if (MapData.TryGetLandZ(Context.Assets, _facet, x, y, out sbyte z))
                {
                    Diamond(draw, min, x, y, z, Theme.U32(color, 0.85f), 1.5f);
                }
            }
        }

        /// <summary>
        /// Per frame in an editing tool: Alt+click picks the brush; otherwise the preview follows the cursor, a click
        /// turns it into the stroke, and a drag adds the tiles it crosses.
        /// </summary>
        private void UpdateEditing()
        {
            var edits = Context.Assets.Edits;
            var io = ImGui.GetIO();

            if (_hovering && io.KeyAlt && ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                PickBrush();
                _leftDown = false;

                return;
            }

            if (!_hovering)
            {
                return;
            }

            if (_stroke == null && _leftDown && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                // the click commits what the preview shows: the preview becomes the stroke's first tiles
                if (_preview == null || _preview.IsDone)
                {
                    BuildPreview();
                }

                _stroke = edits.PromotePreview();
                _preview = null;
            }

            if (_stroke != null)
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && !_stroke.IsDone)
                {
                    ApplyAt(_stroke, _hoverX, _hoverY);
                }

                return;
            }

            var art = Art;
            var key = (_hoverX, _hoverY, _tool, _terrain, _brush, _amount, _stackOnTop,
                art == null ? -1 : art.BrushIsLand ? art.BrushLand : art.BrushStatic, art?.BrushIsLand ?? false);

            if (_preview == null || _preview.IsDone || key != _previewKey)
            {
                _previewKey = key;
                BuildPreview();
            }
        }

        private string Label()
        {
            var art = Art;

            return _tool switch
            {
                MapTool.Draw when art is { BrushIsLand: true } => $"Paint land 0x{art.BrushLand:X4}",
                MapTool.Draw => $"Place 0x{art?.BrushStatic ?? 0:X4}",
                MapTool.Terrain => _terrain switch
                {
                    TerrainOp.Raise => $"Raise land by {_amount}",
                    TerrainOp.Lower => $"Lower land by {_amount}",
                    _ => $"Flatten to z {_flattenZ}"
                },
                _ => "Erase statics"
            };
        }

        private void BuildPreview()
        {
            CancelPreview();

            if (_tool == MapTool.Draw && Art is not { HasBrush: true })
            {
                return;
            }

            _flattenZ = _hoverZ;
            _strokeTiles.Clear();
            _placed.Clear();
            _preview = Context.Assets.Edits.BeginPreview(Label(), _facet);
            ApplyAt(_preview, _hoverX, _hoverY);
        }

        private void CancelPreview()
        {
            if (_preview != null)
            {
                _preview.Rollback();
                _preview = null;
            }

            if (_stroke == null)
            {
                _placed.Clear();
            }
        }

        /// <summary>The tool on the brush around (x, y); each tile changes once per stroke.</summary>
        private void ApplyAt(MapEdits.Transaction tx, int x, int y)
        {
            var art = Art;
            var tiles = Context.Assets.Files.TileData;

            foreach (var (tx2, ty2) in BrushTiles(x, y))
            {
                if (!_strokeTiles.Add((tx2, ty2)) || !tx.TryGet(tx2, ty2, out var land, out var statics))
                {
                    continue;
                }

                switch (_tool)
                {
                    case MapTool.Terrain when _terrain == TerrainOp.Flatten:
                        tx.SetLand(tx2, ty2, land.TileId, _flattenZ);

                        break;

                    case MapTool.Terrain:
                        int step = _terrain == TerrainOp.Raise ? _amount : -_amount;
                        tx.SetLand(tx2, ty2, land.TileId, (sbyte)Math.Clamp(land.Z + step, -128, 127));

                        break;

                    case MapTool.Draw when art is { BrushIsLand: true }:
                        tx.SetLand(tx2, ty2, (ushort)art.BrushLand, land.Z);

                        break;

                    case MapTool.Draw when art is { HasBrush: true }:
                        int z = land.Z;

                        if (_stackOnTop && statics.Length > 0)
                        {
                            z = statics.Max(s => s.Z + (s.Graphic < tiles.StaticData.Length ? tiles.StaticData[s.Graphic].Height : 0));
                        }

                        var placed = new StaticCell { Graphic = (ushort)art.BrushStatic, Z = (sbyte)Math.Clamp(z, -128, 127) };
                        tx.SetStatics(tx2, ty2, statics.Append(placed));
                        _placed.Add((tx2, ty2, placed.Z, placed.Graphic));

                        break;

                    case MapTool.Erase:
                        if (statics.Length > 0)
                        {
                            tx.SetStatics(tx2, ty2, Array.Empty<StaticCell>());
                        }

                        break;
                }
            }
        }

        private void EndStroke()
        {
            var change = _stroke?.Commit();
            _stroke = null;
            _strokeTiles.Clear();
            _placed.Clear();
            _previewKey = default;

            if (change != null)
            {
                StudioLog.Info($"{change.Label}: {change.Tiles} tile(s)");
            }
        }

        /// <summary>Alt+click: the brush becomes the highest static under the cursor, else its land tile.</summary>
        private void PickBrush()
        {
            var tile = new MapTile();

            if (Art == null || !MapData.TryGetTile(Context.Assets, _facet, _hoverX, _hoverY, tile))
            {
                return;
            }

            if (tile.Statics.Count > 0)
            {
                Art.SetBrush(tile.Statics[^1].Graphic, land: false);
            }
            else
            {
                Art.SetBrush(tile.Land & 0x3FFF, land: true);
            }

            _tool = MapTool.Draw;
            StudioLog.Info($"Brush: {(Art.BrushIsLand ? "land" : "static")} 0x{(Art.BrushIsLand ? Art.BrushLand : Art.BrushStatic):X4}");
        }

        /// <summary>Before the world renders: the preview's tiles tinted, its placed statics as translucent ghosts.</summary>
        private void HighlightPreview(MapWorld world)
        {
            if (_preview == null || _preview.IsDone)
            {
                return;
            }

            foreach (var (x, y, z, graphic) in _placed)
            {
                world.HighlightStatic(x, y, z, graphic, 0, PreviewAlpha);
            }

            if (_placed.Count == 0)
            {
                foreach (var (x, y) in _preview.Tiles)
                {
                    world.HighlightLand(x, y, PreviewHue);
                }
            }
        }
    }
}
