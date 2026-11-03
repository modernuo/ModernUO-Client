using System.Numerics;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;
using Uo.Render;

namespace ModernUO.Studio.Panels
{
    internal enum MapTool
    {
        Select,
        Place,
        Raise,
        Lower,
        Flatten,
        Paint,
        Erase
    }

    /// <summary>
    /// The map document (studio/UX.md, "Workspaces and documents"): a facet drawn by the client's GPU world renderer, with
    /// a free camera, go-to, z filters, a tile grid and tile picking for the Tile inspector.
    /// </summary>
    internal sealed class MapPanel : Panel
    {
        private const float MinZoom = 0.125f, MaxZoom = 4f;
        private const ushort SelectHue = 0x0035, HoverHue = 0x0481;

        private int _facet = -1;
        private double _centerX, _centerY;
        private float _zoom = 1f;
        private int _maxZ = 127;
        private bool _hideRoofs;
        private bool _grid;
        private string _goto = "";
        private bool _hovering;
        private int _hoverX, _hoverY;
        private sbyte _hoverZ;
        private Vector2 _canvasSize;
        private bool _wantRender;
        private bool _dragged;
        private bool _leftDown;

        // editing (studio/UX.md, "Editing model"): a stroke is one undoable change
        private MapTool _tool = MapTool.Select;
        private int _brush;
        private int _amount = 1;
        private bool _stackOnTop;
        private MapEdits.Transaction _stroke;
        private readonly HashSet<(int X, int Y)> _strokeTiles = new();
        private sbyte _flattenZ;
        private Microsoft.Xna.Framework.Graphics.RenderTarget2D _boundTarget;

        public MapPanel(StudioContext context) : base(context, true)
        {
        }

        public override string Title => "Map###map";
        public override string MenuName => "Map";
        public override string Icon => Icons.Map;
        public override string MenuGroup => "Map";
        protected override Vector2 DefaultSize => new(1200, 800);
        protected override ImGuiWindowFlags WindowFlags => ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        public int Facet => _facet;
        public double CenterX => _centerX;
        public double CenterY => _centerY;
        public float Zoom => _zoom;
        public Vector2 CanvasSize => _canvasSize;

        /// <summary>The map was on screen last frame.</summary>
        public bool Visible { get; private set; }

        public override void OnPackChanged()
        {
            SaveView();
            _facet = -1;
            _wantRender = false;
            _boundTarget = null;
        }

        private void SaveView()
        {
            if (_facet < 0)
            {
                return;
            }

            var (x, y) = ScreenToTile(_canvasSize * 0.5f, 0);
            Context.Settings.MapFacet = _facet;
            Context.Settings.MapX = x;
            Context.Settings.MapY = y;
            Context.Settings.MapZoom = _zoom;
        }

        /// <summary>The first frame with a pack: the last view from studio.json, else Britain (or the facet's centre).</summary>
        private void InitView()
        {
            var assets = Context.Assets;
            var s = Context.Settings;
            int facet = s.MapFacet >= 0 && MapData.Info(assets, s.MapFacet) != null ? s.MapFacet : assets.Manifest.Facets[0].Index;
            var info = MapData.Info(assets, facet);
            bool saved = facet == s.MapFacet && s.MapX >= 0 && s.MapX < info.Width && s.MapY >= 0 && s.MapY < info.Height;
            int x = saved ? s.MapX : facet <= 1 ? 1496 : info.Width / 2;
            int y = saved ? s.MapY : facet <= 1 ? 1628 : info.Height / 2;
            _zoom = saved ? Math.Clamp(s.MapZoom, MinZoom, MaxZoom) : 1f;
            CenterOn(facet, x, y, select: false);
        }

        /// <summary>Centres the camera on a tile (at its land height) and, with <paramref name="select"/>, picks it.</summary>
        public void CenterOn(int facet, int x, int y, bool select)
        {
            var assets = Context.Assets;

            if (assets == null || MapData.Info(assets, facet) == null)
            {
                return;
            }

            Focus();
            _facet = facet;
            Context.MapWorld?.SetFacet(facet);
            MapData.TryGetLandZ(assets, facet, x, y, out sbyte z);
            _centerX = (x - y) * 22.0;
            _centerY = (x + y) * 22.0 - z * 4.0;

            if (select)
            {
                Context.SelectTile(facet, x, y);
            }
        }

        private void SetFacet(int facet)
        {
            SaveView();
            var info = MapData.Info(Context.Assets, facet);

            // Felucca and Trammel open on Britain, the other facets on their centre
            CenterOn(facet, facet <= 1 ? 1496 : info.Width / 2, facet <= 1 ? 1628 : info.Height / 2, select: false);
        }

        // ---- coordinates: world pixels X = (x - y) * 22, Y = (x + y) * 22 - z * 4 is the centre of tile (x, y, z)

        private Vector2 WorldToScreen(double wx, double wy) =>
            new((float)((wx - _centerX) * _zoom + _canvasSize.X * 0.5), (float)((wy - _centerY) * _zoom + _canvasSize.Y * 0.5));

        private Vector2 TileToScreen(int x, int y, int z) => WorldToScreen((x - y) * 22.0, (x + y) * 22.0 - z * 4.0);

        /// <summary>The tile under a canvas point, assuming height <paramref name="z"/>.</summary>
        private (int X, int Y) ScreenToTile(Vector2 p, int z)
        {
            double wx = _centerX + (p.X - _canvasSize.X * 0.5) / _zoom;
            double wy = _centerY + (p.Y - _canvasSize.Y * 0.5) / _zoom + z * 4.0;
            double u = wx / 22.0, v = wy / 22.0;

            return ((int)Math.Round((u + v) * 0.5), (int)Math.Round((v - u) * 0.5));
        }

        /// <summary>The land tile under a canvas point: picked flat, then again at the found tile's height (land is not flat).</summary>
        private bool PickTile(Vector2 p, out int x, out int y, out sbyte z)
        {
            z = 0;
            (x, y) = ScreenToTile(p, 0);

            for (int i = 0; i < 3; i++)
            {
                if (!MapData.TryGetLandZ(Context.Assets, _facet, x, y, out z))
                {
                    return false;
                }

                var (nx, ny) = ScreenToTile(p, z);

                if (nx == x && ny == y)
                {
                    break;
                }

                (x, y) = (nx, ny);
            }

            return MapData.TryGetLandZ(Context.Assets, _facet, x, y, out z);
        }

        // ---- drawing

        protected override void DrawContent()
        {
            var world = Context.MapWorld;

            if (world == null)
            {
                ImGui.TextColored(Widgets.Bad, $"The map can't be shown: {Context.MapError ?? "the map renderer did not start"}.");

                return;
            }

            if (_facet < 0)
            {
                InitView();
            }

            DrawToolbar();
            DrawTools();

            Vector2 min = ImGui.GetCursorScreenPos();
            Vector2 avail = ImGui.GetContentRegionAvail();
            _canvasSize = new Vector2(MathF.Max(1f, MathF.Floor(avail.X)), MathF.Max(1f, MathF.Floor(avail.Y - ImGui.GetTextLineHeightWithSpacing())));

            if (!world.EnsureTarget((int)_canvasSize.X, (int)_canvasSize.Y))
            {
                return;
            }

            if (!ReferenceEquals(_boundTarget, world.Target))
            {
                // a resized target is a new texture: drop the old one's ImGui id
                Context.Renderer.Unbind(_boundTarget);
                _boundTarget = world.Target;
            }

            // an image is not interactive: a button takes the input and the target is drawn under it
            ImGui.InvisibleButton("canvas", _canvasSize,
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
            var draw = ImGui.GetWindowDrawList();
            draw.AddImage(Context.Renderer.Bind(world.Target), min, min + _canvasSize);
            HandleInput(min);

            draw.PushClipRect(min, min + _canvasSize, true);

            if (_grid && _zoom >= 0.5f)
            {
                DrawGrid(draw, min);
            }

            if (_hovering)
            {
                if (_tool == MapTool.Select)
                {
                    Diamond(draw, min, _hoverX, _hoverY, _hoverZ, Widgets.Col(1f, 1f, 1f, 0.9f), 1.5f);
                }
                else
                {
                    DrawBrush(draw, min);
                }
            }

            var selected = Context.SelectedTile;

            if (selected != null && selected.Facet == _facet)
            {
                Diamond(draw, min, selected.X, selected.Y, selected.LandZ, Widgets.Col(1f, 0.8f, 0.2f), 2f);
            }

            draw.PopClipRect();
            DrawStatusLine(world);

            _wantRender = true;
        }

        private void DrawToolbar()
        {
            var facets = Context.Assets.Manifest.Facets;
            ImGui.SetNextItemWidth(130f);

            if (ImGui.BeginCombo("##facet", $"{_facet} {MapData.FacetName(_facet)}"))
            {
                foreach (var f in facets)
                {
                    if (ImGui.Selectable($"{f.Index} {MapData.FacetName(f.Index)}  ({f.Width} x {f.Height})", f.Index == _facet) && f.Index != _facet)
                    {
                        SetFacet(f.Index);
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(130f);
            bool go = ImGui.InputTextWithHint("##goto", "x y (1496 1628)", ref _goto, 32, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            go |= Ui.IconButton("go", Icons.ArrowRight, "Go to the tile");

            if (go && TryParseCoordinates(_goto, out int gx, out int gy))
            {
                CenterOn(_facet, gx, gy, select: true);
            }

            ImGui.SameLine();
            Ui.Gap();

            if (Ui.IconButton("zoomout", Icons.ZoomOut, "Zoom out (-)", enabled: _zoom > MinZoom))
            {
                _zoom = Math.Clamp(_zoom * 0.8f, MinZoom, MaxZoom);
            }

            ImGui.SameLine(0f, 2f);
            ImGui.AlignTextToFramePadding();
            ImGui.PushFont(Theme.Mono, 0f);
            ImGui.TextColored(Theme.TextSecondary, $"{_zoom * 100f,4:0}%");
            ImGui.PopFont();
            ImGui.SameLine(0f, 2f);

            if (Ui.IconButton("zoomin", Icons.ZoomIn, "Zoom in (+)", enabled: _zoom < MaxZoom))
            {
                _zoom = Math.Clamp(_zoom * 1.25f, MinZoom, MaxZoom);
            }
            Ui.Gap();
            ImGui.SetNextItemWidth(140f);
            ImGui.SliderInt("##maxz", ref _maxZ, -128, 127, _maxZ >= 127 ? "max z: all" : "max z %d");
            Ui.Gap();
            Ui.Toggle("Roofs off", ref _hideRoofs, "Hide roofs, as the client does indoors");
            Ui.Gap();
            Ui.Toggle("Grid", ref _grid, "Outline every tile (zoom 50% or more)");
        }

        private static bool TryParseCoordinates(string text, out int x, out int y)
        {
            x = y = 0;
            var parts = text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);

            return parts.Length >= 2 && Widgets.TryParseId(parts[0], out x) && Widgets.TryParseId(parts[1], out y);
        }

        private void HandleInput(Vector2 min)
        {
            var io = ImGui.GetIO();
            bool hovered = ImGui.IsItemHovered();
            Vector2 mouse = io.MousePos - min;

            bool editing = _tool != MapTool.Select;

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                _leftDown = true;
                _dragged = false;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Middle))
            {
                _dragged = false;
            }

            // the middle button always pans; the left button pans in Select and paints strokes in the editing tools
            bool panLeft = !editing && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3f);

            if (ImGui.IsItemActive() && (panLeft || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 3f)))
            {
                _dragged = true;
                _centerX -= io.MouseDelta.X / _zoom;
                _centerY -= io.MouseDelta.Y / _zoom;
            }

            if (hovered && io.MouseWheel != 0)
            {
                // zoom about the cursor: the world point under it stays put
                double wx = _centerX + (mouse.X - _canvasSize.X * 0.5) / _zoom;
                double wy = _centerY + (mouse.Y - _canvasSize.Y * 0.5) / _zoom;
                _zoom = Math.Clamp(_zoom * (io.MouseWheel > 0 ? 1.25f : 0.8f), MinZoom, MaxZoom);
                _centerX = wx - (mouse.X - _canvasSize.X * 0.5) / _zoom;
                _centerY = wy - (mouse.Y - _canvasSize.Y * 0.5) / _zoom;
            }

            _hovering = hovered && PickTile(mouse, out _hoverX, out _hoverY, out _hoverZ);

            if (editing && _leftDown && ImGui.IsMouseDown(ImGuiMouseButton.Left) && _hovering)
            {
                Stroke(_hoverX, _hoverY, _hoverZ);
            }

            if (_leftDown && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                _leftDown = false;

                if (editing)
                {
                    EndStroke();
                }
                else if (_hovering && !_dragged)
                {
                    Context.SelectTile(_facet, _hoverX, _hoverY);
                    Context.Get<TileInspectorPanel>()?.Show();
                }
            }

            if (ImGui.IsWindowFocused() && !io.WantTextInput && !io.KeyCtrl)
            {
                // tool keys, as in most editors: V select, A place, R raise, L lower, F flatten, P paint, X erase; [ ] brush
                if (ImGui.IsKeyPressed(ImGuiKey.V)) _tool = MapTool.Select;
                if (ImGui.IsKeyPressed(ImGuiKey.A)) _tool = MapTool.Place;
                if (ImGui.IsKeyPressed(ImGuiKey.R)) _tool = MapTool.Raise;
                if (ImGui.IsKeyPressed(ImGuiKey.L)) _tool = MapTool.Lower;
                if (ImGui.IsKeyPressed(ImGuiKey.F)) _tool = MapTool.Flatten;
                if (ImGui.IsKeyPressed(ImGuiKey.P)) _tool = MapTool.Paint;
                if (ImGui.IsKeyPressed(ImGuiKey.X)) _tool = MapTool.Erase;
                if (ImGui.IsKeyPressed(ImGuiKey.LeftBracket)) _brush = Math.Max(0, _brush - 1);
                if (ImGui.IsKeyPressed(ImGuiKey.RightBracket)) _brush = Math.Min(8, _brush + 1);
            }
            if (ImGui.IsWindowFocused() && !io.WantTextInput)
            {
                float step = 44f * 4f / _zoom;

                if (ImGui.IsKeyDown(ImGuiKey.LeftArrow)) _centerX -= step * io.DeltaTime * 4f;
                if (ImGui.IsKeyDown(ImGuiKey.RightArrow)) _centerX += step * io.DeltaTime * 4f;
                if (ImGui.IsKeyDown(ImGuiKey.UpArrow)) _centerY -= step * io.DeltaTime * 4f;
                if (ImGui.IsKeyDown(ImGuiKey.DownArrow)) _centerY += step * io.DeltaTime * 4f;
                if (ImGui.IsKeyPressed(ImGuiKey.Equal) || ImGui.IsKeyPressed(ImGuiKey.KeypadAdd)) _zoom = Math.Clamp(_zoom * 1.25f, MinZoom, MaxZoom);
                if (ImGui.IsKeyPressed(ImGuiKey.Minus) || ImGui.IsKeyPressed(ImGuiKey.KeypadSubtract)) _zoom = Math.Clamp(_zoom * 0.8f, MinZoom, MaxZoom);
            }

            if (ImGui.BeginPopupContextItem("mapctx"))
            {
                if (_hovering || Context.SelectedTile != null)
                {
                    var t = Context.SelectedTile;
                    string coords = t != null ? $"{t.X} {t.Y} {t.LandZ}" : $"{_hoverX} {_hoverY} {_hoverZ}";

                    if (ImGui.MenuItem($"Copy \"{coords}\""))
                    {
                        ImGui.SetClipboardText(coords);
                    }
                }

                if (ImGui.MenuItem("Show tile inspector"))
                {
                    Context.Get<TileInspectorPanel>()?.Show();
                }

                ImGui.EndPopup();
            }
        }

        private static readonly (MapTool Tool, string Icon, string Tip)[] _tools =
        {
            (MapTool.Select, Icons.Pointer, "Select (V): click picks a tile, drag pans"),
            (MapTool.Place, Icons.Stamp, "Place static (A): the static picked in Art, on the ground or on top of the stack"),
            (MapTool.Raise, Icons.ChevronsUp, "Raise land (R)"),
            (MapTool.Lower, Icons.ChevronsDown, "Lower land (L)"),
            (MapTool.Flatten, Icons.Equal, "Flatten (F): to the height of the first tile of the stroke"),
            (MapTool.Paint, Icons.Paintbrush, "Paint land (P): the land tile picked in Art"),
            (MapTool.Erase, Icons.Eraser, "Erase statics (X)")
        };

        /// <summary>The tool palette and the active tool's options.</summary>
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

            if (_tool == MapTool.Select)
            {
                return;
            }

            Ui.Gap(16f);

            if (_tool != MapTool.Place)
            {
                Stepper("brush", ref _brush, 0, 8, v => v == 0 ? "1 tile" : $"{v * 2 + 1}x{v * 2 + 1}", "Brush size ([ and ])");
            }

            if (_tool is MapTool.Raise or MapTool.Lower)
            {
                Ui.Gap();
                Stepper("amount", ref _amount, 1, 20, v => $"{v} z", "Height step per tile");
            }

            if (_tool == MapTool.Place)
            {
                Ui.Toggle("On top", ref _stackOnTop, "Place on top of the tile's statics instead of on the ground");
            }

            if (_tool is MapTool.Place or MapTool.Paint)
            {
                // the brush is whatever the Art browser last picked
                var art = Context.Get<ArtBrowser>();
                int id = _tool == MapTool.Place ? art?.BrushStatic ?? -1 : art?.BrushLand ?? -1;
                Ui.Gap();

                if (id < 0)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.Amber, _tool == MapTool.Place ? "Pick a static in Art" : "Pick a land tile in Art (Land)");
                }
                else
                {
                    var names = Context.Assets.Files.TileData;
                    string name = _tool == MapTool.Place
                        ? id < names.StaticData.Length ? names.StaticData[id].Name : ""
                        : id < names.LandData.Length ? names.LandData[id].Name : "";
                    Ui.Chip($"{Icons.Image}  0x{id:X4} {name}", Theme.Gold);
                }
            }
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

        private IEnumerable<(int X, int Y)> BrushTiles(int x, int y)
        {
            int r = _tool == MapTool.Place ? 0 : _brush;

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
                MapTool.Raise or MapTool.Lower or MapTool.Flatten => Theme.Blue,
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

        /// <summary>Applies the tool under the cursor; each tile changes once per stroke, and a stroke is one change.</summary>
        private void Stroke(int x, int y, sbyte z)
        {
            var edits = Context.Assets.Edits;
            var art = Context.Get<ArtBrowser>();
            int brushStatic = art?.BrushStatic ?? -1, brushLand = art?.BrushLand ?? -1;

            if ((_tool == MapTool.Place && brushStatic < 0) || (_tool == MapTool.Paint && brushLand < 0))
            {
                return;
            }

            if (_stroke == null)
            {
                string label = _tool switch
                {
                    MapTool.Place => $"Place 0x{brushStatic:X4}",
                    MapTool.Raise => $"Raise land by {_amount}",
                    MapTool.Lower => $"Lower land by {_amount}",
                    MapTool.Flatten => $"Flatten to z {z}",
                    MapTool.Paint => $"Paint land 0x{brushLand:X4}",
                    _ => "Erase statics"
                };

                _stroke = edits.Begin(label, _facet);
                _strokeTiles.Clear();
                _flattenZ = z;
            }

            var tiles = Context.Assets.Files.TileData;

            foreach (var (tx, ty) in BrushTiles(x, y))
            {
                if (!_strokeTiles.Add((tx, ty)) || !_stroke.TryGet(tx, ty, out var land, out var statics))
                {
                    continue;
                }

                switch (_tool)
                {
                    case MapTool.Raise:
                    case MapTool.Lower:
                        int step = _tool == MapTool.Raise ? _amount : -_amount;
                        _stroke.SetLand(tx, ty, land.TileId, (sbyte)Math.Clamp(land.Z + step, -128, 127));

                        break;

                    case MapTool.Flatten:
                        _stroke.SetLand(tx, ty, land.TileId, _flattenZ);

                        break;

                    case MapTool.Paint:
                        _stroke.SetLand(tx, ty, (ushort)brushLand, land.Z);

                        break;

                    case MapTool.Erase:
                        if (statics.Length > 0)
                        {
                            _stroke.SetStatics(tx, ty, Array.Empty<Uo.Pack.StaticCell>());
                        }

                        break;

                    case MapTool.Place:
                        int placeZ = land.Z;

                        if (_stackOnTop && statics.Length > 0)
                        {
                            placeZ = statics.Max(s => s.Z + (s.Graphic < tiles.StaticData.Length ? tiles.StaticData[s.Graphic].Height : 0));
                        }

                        var placed = new Uo.Pack.StaticCell { Graphic = (ushort)brushStatic, Z = (sbyte)Math.Clamp(placeZ, -128, 127) };
                        _stroke.SetStatics(tx, ty, statics.Append(placed));

                        break;
                }
            }
        }

        private void EndStroke()
        {
            var change = _stroke?.Commit();
            _stroke = null;
            _strokeTiles.Clear();

            if (change != null)
            {
                StudioLog.Info($"{change.Label}: {change.Tiles} tile(s)");
            }
        }
        private void Diamond(ImDrawListPtr draw, Vector2 min, int x, int y, int z, uint color, float thickness)
        {
            Vector2 c = min + TileToScreen(x, y, z);
            float h = 22f * _zoom;
            draw.AddQuad(c + new Vector2(0, -h), c + new Vector2(h, 0), c + new Vector2(0, h), c + new Vector2(-h, 0), color, thickness);
        }

        private void DrawGrid(ImDrawListPtr draw, Vector2 min)
        {
            // the tiles under the canvas corners bound the visible ones; land heights shift tiles up to 512 px, so pad
            var corners = new[] { ScreenToTile(Vector2.Zero, 0), ScreenToTile(new Vector2(_canvasSize.X, 0), 0),
                ScreenToTile(new Vector2(0, _canvasSize.Y), 0), ScreenToTile(_canvasSize, 0) };
            int pad = 4;
            int x0 = corners.Min(c => c.X) - pad, x1 = corners.Max(c => c.X) + pad;
            int y0 = corners.Min(c => c.Y) - pad, y1 = corners.Max(c => c.Y) + pad;

            if ((long)(x1 - x0) * (y1 - y0) > 40000)
            {
                return;
            }

            uint color = Widgets.Col(1f, 1f, 1f, 0.12f);

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (MapData.TryGetLandZ(Context.Assets, _facet, x, y, out sbyte z))
                    {
                        Diamond(draw, min, x, y, z, color, 1f);
                    }
                }
            }
        }

        private void DrawStatusLine(MapWorld world)
        {
            string hover = _hovering ? $"{_hoverX}, {_hoverY}, z {_hoverZ}" : "-";
            string loading = world.VisibleReady ? "" : "  streaming...";
            ImGui.TextColored(Widgets.Dim, $"{MapData.FacetName(_facet)}  tile {hover}  zoom {_zoom:0.00}x  {world.Chunks} chunks, {world.Instances} sprites{loading}");
        }

        /// <summary>Draws the world into the target: after ImGui built the frame, before FNA submits it (StudioGame.Draw).</summary>
        public void RenderWorld()
        {
            var world = Context.MapWorld;

            // the map tab was drawn this frame (not hidden behind another tab): the status bar reads it next frame
            Visible = _wantRender && world != null && IsOpen;

            if (!Visible)
            {
                _wantRender = false;

                return;
            }

            _wantRender = false;
            var selected = Context.SelectedTile;

            if (selected != null && selected.Facet == _facet)
            {
                int hovered = Context.HoveredStatic;

                if (hovered >= 0 && hovered < selected.Statics.Count)
                {
                    var s = selected.Statics[hovered];
                    world.HighlightStatic(selected.X, selected.Y, s.Z, s.Graphic, HoverHue);
                }
                else
                {
                    world.HighlightLand(selected.X, selected.Y, SelectHue);
                }
            }

            var camera = new WorldCamera
            {
                CenterX = _centerX,
                CenterY = _centerY,
                Zoom = _zoom,
                ViewportWidth = (int)_canvasSize.X,
                ViewportHeight = (int)_canvasSize.Y
            };

            world.Render(camera, _maxZ, _hideRoofs);
        }

        /// <summary>The tile at the canvas centre (the radar's view marker).</summary>
        public (int X, int Y) CenterTile() => ScreenToTile(_canvasSize * 0.5f, 0);

        /// <summary>The tiles under the canvas corners, clockwise from top-left (flat, z 0).</summary>
        public (int X, int Y)[] ViewCorners() => new[]
        {
            ScreenToTile(Vector2.Zero, 0), ScreenToTile(new Vector2(_canvasSize.X, 0), 0),
            ScreenToTile(_canvasSize, 0), ScreenToTile(new Vector2(0, _canvasSize.Y), 0)
        };

        public void SaveOnExit() => SaveView();
    }
}
