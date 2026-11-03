using System.Numerics;
using Hexa.NET.ImGui;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>
    /// The browser pattern of studio/UX.md: a toolbar (modes, search, filters, grid or list, size, count), a wide
    /// collection and a narrow inspector (header, preview, properties, relations, actions). Subclasses describe their
    /// items; layout, search, keys, selection and scrolling live here. Items are ints: an id, or an index for kinds whose
    /// ids are sparse (cliloc).
    /// </summary>
    internal abstract class AssetBrowser : Panel
    {
        /// <summary>The inspector sections a browser fills; the header is always drawn.</summary>
        [Flags]
        protected enum Parts
        {
            None = 0,
            Preview = 1,
            Properties = 2,
            Relations = 4,
            Actions = 8,
            All = Preview | Properties | Relations | Actions
        }

        private const float MinCollectionWidth = 320f;

        private readonly List<int> _items = new();
        private string _filter = "";
        private bool _dirty = true;
        private bool _scrollToSelection;
        private bool _focusSearch;
        private float _cell;
        private float _inspectorWidth;
        private bool _listView;
        private int _mode;

        protected AssetBrowser(StudioContext context, bool open) : base(context, open)
        {
            _cell = DefaultCell;
            _inspectorWidth = DefaultInspectorWidth;
            _listView = !HasGrid;
        }

        protected int Selected { get; private set; } = -1;

        protected IReadOnlyList<int> Items => _items;

        // ---- what the collection holds

        /// <summary>The inspector header's kind, per mode ("Static", "Land tile").</summary>
        protected abstract string Kind { get; }

        /// <summary>Items 0..SlotCount-1 are candidates; <see cref="Exists"/> keeps the real ones.</summary>
        protected abstract int SlotCount { get; }

        protected virtual bool Exists(int item) => true;

        /// <summary>Mutually exclusive sets the toolbar switches between (statics and land, sounds and music); null for one.</summary>
        protected virtual string[] Modes => null;

        protected int Mode => _mode;

        protected virtual string NameOf(int item) => null;

        /// <summary>The second line of a list row.</summary>
        protected virtual string DetailOf(int item) => null;

        protected virtual string FormatId(int item) => Inspector.Id(item);

        /// <summary>The id that "0x1F03"-style searches match and Ctrl+C copies.</summary>
        protected virtual int IdOf(int item) => item;

        /// <summary>What Ctrl+C and "Copy id" put on the clipboard.</summary>
        protected virtual string CopyTextOf(int item) => $"0x{IdOf(item):X4}";

        /// <summary>Filters besides the search box (a tiledata flag, for example).</summary>
        protected virtual bool PassesFilter(int item) => true;

        /// <summary>Text the search box also matches, besides the name (a body's type, for example).</summary>
        protected virtual string SearchTextOf(int item) => null;

        // ---- how it looks

        /// <summary>False for data without pictures: the collection is a table only.</summary>
        protected virtual bool HasGrid => true;

        /// <summary>List rows carry a thumbnail (true for every kind with a grid; tiledata shows its art).</summary>
        protected virtual bool HasThumbnails => HasGrid;

        protected virtual float DefaultCell => 64f;

        protected virtual float DefaultInspectorWidth => 360f;

        protected virtual Parts InspectorParts => Parts.Preview | Parts.Properties | Parts.Actions;

        protected virtual string SearchHint => "id (0x1F03, 7939) or name";

        /// <summary>The thumbnail of one item; only visible cells and rows call it, so only they decode.</summary>
        protected virtual void DrawThumbnail(ImDrawListPtr draw, int item, Vector2 min, Vector2 max)
        {
        }

        /// <summary>Extra list columns after id and name.</summary>
        protected virtual int ExtraColumns => 0;

        protected virtual void SetupExtraColumns()
        {
        }

        protected virtual void DrawExtraColumns(int item)
        {
        }

        protected virtual void DrawFilters()
        {
        }

        protected virtual void DrawContextMenu(int item)
        {
        }

        // ---- the inspector

        protected virtual void DrawPreview(int item)
        {
        }

        protected virtual void DrawProperties(int item)
        {
        }

        protected virtual void DrawRelations(int item)
        {
        }

        protected virtual void DrawActions(int item)
        {
        }

        // ---- events

        protected virtual void OnSelected(int item)
        {
        }

        /// <summary>Enter or a double-click on an item.</summary>
        protected virtual void OnActivated(int item)
        {
        }

        protected virtual void OnModeChanged()
        {
        }

        // ---- control from outside

        public void Invalidate() => _dirty = true;

        public override void OnPackChanged()
        {
            _dirty = true;
            Selected = -1;
        }

        public override void SelectById(int id) => SelectItem(id);

        /// <summary>Focuses the browser and selects an item, switching mode first when one is given.</summary>
        public void SelectItem(int item, int mode = -1)
        {
            Focus();

            if (mode >= 0 && mode != _mode && Modes != null && mode < Modes.Length)
            {
                SetMode(mode);
            }

            Selected = item;
            _scrollToSelection = true;

            if (!_dirty && _items.BinarySearch(item) < 0)
            {
                // filtered out: clear the search so the selection shows
                _filter = "";
                _dirty = true;
            }

            OnSelected(item);
        }

        private void SetMode(int mode)
        {
            _mode = mode;
            _dirty = true;
            Selected = -1;
            OnModeChanged();
        }

        private void Select(int item)
        {
            if (item != Selected)
            {
                Selected = item;
                OnSelected(item);
            }
        }

        // ---- drawing

        protected sealed override void DrawContent()
        {
            if (_dirty)
            {
                Rebuild();
            }

            DrawToolbar();
            HandleKeys();

            Vector2 avail = ImGui.GetContentRegionAvail();
            bool stacked = avail.X < _inspectorWidth + MinCollectionWidth;

            if (stacked)
            {
                // a narrow docked palette: the inspector moves under the collection
                float collectionHeight = MathF.Max(120f, avail.Y * 0.55f);

                if (Ui.BeginCard("collection", new Vector2(0, collectionHeight), padded: false, sunken: true))
                {
                    DrawCollection();
                }

                Ui.EndCard();

                if (Ui.BeginCard("inspector", Vector2.Zero))
                {
                    DrawInspector();
                }

                Ui.EndCard();

                return;
            }

            if (Ui.BeginCard("collection", new Vector2(avail.X - _inspectorWidth - 8f, 0), padded: false, sunken: true))
            {
                DrawCollection();
            }

            Ui.EndCard();
            ImGui.SameLine(0, 0);
            DrawSplitter(avail.X);
            ImGui.SameLine(0, 0);

            if (Ui.BeginCard("inspector", Vector2.Zero))
            {
                DrawInspector();
            }

            Ui.EndCard();
        }

        private void DrawSplitter(float total)
        {
            ImGui.InvisibleButton("##split", new Vector2(8f, MathF.Max(1f, ImGui.GetContentRegionAvail().Y)));

            if (ImGui.IsItemActive())
            {
                _inspectorWidth = Math.Clamp(_inspectorWidth - ImGui.GetIO().MouseDelta.X, 220f, MathF.Max(220f, total - MinCollectionWidth));
            }

            if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            }
        }

        private void DrawToolbar()
        {
            string[] modes = Modes;

            if (modes != null)
            {
                int mode = _mode;

                if (Ui.Segmented("modes", ref mode, modes))
                {
                    SetMode(mode);
                }

                Ui.Gap();
            }

            _dirty |= Ui.Search("##search", ref _filter, SearchHint, 240f, _focusSearch);
            _focusSearch = false;

            DrawFilters();

            if (HasGrid)
            {
                Ui.Gap();

                if (Ui.IconButton("grid", Icons.Grid, "Grid", !_listView))
                {
                    _listView = false;
                    _scrollToSelection = true;
                }

                ImGui.SameLine(0f, 2f);

                if (Ui.IconButton("list", Icons.List, "List", _listView))
                {
                    _listView = true;
                    _scrollToSelection = true;
                }
            }

            if (HasThumbnails)
            {
                Ui.Gap();
                ImGui.SetNextItemWidth(110f);
                ImGui.SliderFloat("##size", ref _cell, 24f, 256f, $"{Icons.ZoomIn}  %.0f");

                if (ImGui.IsItemHovered())
                {
                    Ui.Tooltip("Thumbnail size");
                }
            }

            // the count sits on the toolbar's right edge
            string count = $"{_items.Count:N0} entries";
            ImGui.SameLine(0f, 12f);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(count).X));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextMuted, count);
        }
        private void HandleKeys()
        {
            if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            {
                return;
            }

            var io = ImGui.GetIO();

            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F))
            {
                _focusSearch = true;
            }

            // typing in the search box keeps its own keys
            if (io.WantTextInput || _items.Count == 0)
            {
                return;
            }

            int index = Selected >= 0 ? _items.BinarySearch(Selected) : -1;

            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.C) && Selected >= 0)
            {
                ImGui.SetClipboardText(CopyTextOf(Selected));
                StudioLog.Info($"Copied {CopyTextOf(Selected)}");
            }

            if (ImGui.IsKeyPressed(ImGuiKey.Enter) && Selected >= 0)
            {
                OnActivated(Selected);
            }

            int columns = _listView ? 1 : _gridColumns;
            int page = _listView ? 16 : columns * 6;
            int next = index;

            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow) && !_listView) next++;
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow) && !_listView) next--;
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) next += columns;
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) next -= columns;
            if (ImGui.IsKeyPressed(ImGuiKey.PageDown)) next += page;
            if (ImGui.IsKeyPressed(ImGuiKey.PageUp)) next -= page;
            if (ImGui.IsKeyPressed(ImGuiKey.Home)) next = 0;
            if (ImGui.IsKeyPressed(ImGuiKey.End)) next = _items.Count - 1;

            if (next != index)
            {
                next = Math.Clamp(next, 0, _items.Count - 1);
                Select(_items[next]);
                _scrollToSelection = true;
            }
        }

        private void Rebuild()
        {
            _dirty = false;
            _items.Clear();

            string filter = _filter.Trim();
            bool byId = Widgets.TryParseId(filter, out int filterId);
            int count = SlotCount;

            for (int item = 0; item < count; item++)
            {
                if (!Exists(item) || !PassesFilter(item))
                {
                    continue;
                }

                if (filter.Length > 0 && !(byId && IdOf(item) == filterId) && !Contains(NameOf(item), filter) && !Contains(SearchTextOf(item), filter))
                {
                    continue;
                }

                _items.Add(item);
            }

            // an exact id jumps there
            if (byId && filter.Length > 0)
            {
                foreach (int item in _items)
                {
                    if (IdOf(item) == filterId)
                    {
                        Select(item);
                        _scrollToSelection = true;

                        break;
                    }
                }
            }
        }

        private static bool Contains(string text, string filter) => text != null && text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        private int _gridColumns = 1;

        private void DrawCollection()
        {
            if (_items.Count == 0)
            {
                EmptyState(Icons.Search, string.IsNullOrWhiteSpace(_filter) ? "Nothing here" : "No matches", string.IsNullOrWhiteSpace(_filter) ? "This section of the pack is empty." : "Try another id or name.");

                return;
            }

            if (_listView || !HasGrid)
            {
                DrawList();
            }
            else
            {
                DrawGrid();
            }
        }

        // ImGui clamps a scroll to last frame's content height: keep asking until the content is tall enough
        private void ScrollTo(float y)
        {
            float target = MathF.Max(0f, y - ImGui.GetWindowHeight() * 0.4f);
            ImGui.SetScrollY(target);
            _scrollToSelection = ImGui.GetScrollMaxY() < target;
        }

        private void DrawGrid()
        {
            var draw = ImGui.GetWindowDrawList();
            const float spacing = 4f;
            float step = _cell + spacing;
            int columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / step));
            _gridColumns = columns;
            int rows = (_items.Count + columns - 1) / columns;

            if (_scrollToSelection && Selected >= 0)
            {
                int index = _items.BinarySearch(Selected);

                if (index >= 0)
                {
                    ScrollTo(index / columns * step);
                }
                else
                {
                    _scrollToSelection = false;
                }
            }

            ImGuiListClipper clipper = new();
            clipper.Begin(rows, step);

            while (clipper.Step())
            {
                for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                {
                    for (int col = 0; col < columns; col++)
                    {
                        int index = row * columns + col;

                        if (index >= _items.Count)
                        {
                            break;
                        }

                        if (col > 0)
                        {
                            ImGui.SameLine(0, spacing);
                        }

                        DrawCell(draw, _items[index]);
                    }
                }
            }

            clipper.End();
        }

        private void DrawCell(ImDrawListPtr draw, int item)
        {
            ImGui.PushID(item);
            Vector2 min = ImGui.GetCursorScreenPos();
            Vector2 max = min + new Vector2(_cell, _cell);
            bool clicked = ImGui.InvisibleButton("cell", new Vector2(_cell, _cell));
            bool hovered = ImGui.IsItemHovered();

            Ui.CellBackground(draw, min, max, item == Selected, hovered, ImGui.GetItemID());
            DrawThumbnail(draw, item, min + new Vector2(4, 4), max - new Vector2(4, 4));

            if (hovered)
            {
                string name = NameOf(item);
                Ui.Tooltip(string.IsNullOrEmpty(name) ? FormatId(item) : $"{name}\n{FormatId(item)}");
            }

            if (clicked)
            {
                Select(item);
            }

            if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                OnActivated(item);
            }

            ItemContextMenu(item);
            ImGui.PopID();
        }

        private void ItemContextMenu(int item)
        {
            if (!ImGui.BeginPopupContextItem("ctx"))
            {
                return;
            }

            Select(item);

            if (ImGui.MenuItem("Copy id"))
            {
                ImGui.SetClipboardText(CopyTextOf(item));
            }

            DrawContextMenu(item);
            ImGui.EndPopup();
        }

        private void DrawList()
        {
            float thumb = HasThumbnails ? Math.Clamp(_cell * 0.6f, 24f, 128f) : 0f;
            float rowHeight = MathF.Max(thumb, ImGui.GetTextLineHeight() * (HasThumbnails ? 2f : 1f) + 2f);
            int columns = (HasThumbnails ? 1 : 0) + 2 + ExtraColumns;
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable
                | ImGuiTableFlags.SizingFixedFit;

            if (!ImGui.BeginTable("list", columns, flags))
            {
                return;
            }

            ImGui.TableSetupScrollFreeze(0, 1);

            if (HasThumbnails)
            {
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, thumb);
            }

            ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("Name", ExtraColumns == 0 ? ImGuiTableColumnFlags.WidthStretch : ImGuiTableColumnFlags.WidthFixed, 220f);
            SetupExtraColumns();
            ImGui.TableHeadersRow();

            float step = rowHeight + ImGui.GetStyle().CellPadding.Y * 2f;

            if (_scrollToSelection && Selected >= 0)
            {
                int index = _items.BinarySearch(Selected);

                if (index >= 0)
                {
                    ScrollTo(index * step);
                }
                else
                {
                    _scrollToSelection = false;
                }
            }

            var draw = ImGui.GetWindowDrawList();
            ImGuiListClipper clipper = new();
            clipper.Begin(_items.Count, step);

            while (clipper.Step())
            {
                for (int r = clipper.DisplayStart; r < clipper.DisplayEnd; r++)
                {
                    int item = _items[r];
                    ImGui.PushID(item);
                    ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                    ImGui.TableNextColumn();

                    Vector2 pos = ImGui.GetCursorScreenPos();

                    if (ImGui.Selectable("##row", item == Selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap,
                            new Vector2(0, rowHeight)))
                    {
                        Select(item);
                    }

                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        OnActivated(item);
                    }

                    ItemContextMenu(item);

                    if (HasThumbnails)
                    {
                        draw.AddRectFilled(pos, pos + new Vector2(thumb, thumb), Theme.U32(Theme.Panel), 4f);
                        DrawThumbnail(draw, item, pos + new Vector2(1, 1), pos + new Vector2(thumb - 1, thumb - 1));
                        ImGui.TableNextColumn();
                    }
                    else
                    {
                        // the row's selectable shares the id's cell
                        ImGui.SameLine();
                    }

                    Ui.MonoText(FormatId(item), Theme.TextSecondary);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(NameOf(item) ?? "");

                    string detail = HasThumbnails ? DetailOf(item) : null;

                    if (!string.IsNullOrEmpty(detail))
                    {
                        ImGui.TextColored(Widgets.Dim, detail);
                    }

                    if (ExtraColumns > 0)
                    {
                        DrawExtraColumns(item);
                    }

                    ImGui.PopID();
                }
            }

            clipper.End();
            ImGui.EndTable();
        }

        private void DrawInspector()
        {
            if (Selected < 0 || Selected >= SlotCount || !Exists(Selected))
            {
                EmptyState(Icons.Navigation, "Nothing selected", "Pick an entry to inspect it.");

                return;
            }

            int item = Selected;
            Inspector.Header(Kind, IdOf(item), NameOf(item), FormatId(item));
            Parts parts = InspectorParts;

            if ((parts & Parts.Preview) != 0)
            {
                Inspector.Section("Preview");
                DrawPreview(item);
            }

            if ((parts & Parts.Properties) != 0)
            {
                Inspector.Section("Properties");
                DrawProperties(item);
            }

            if ((parts & Parts.Relations) != 0)
            {
                Inspector.Section("Relations");
                DrawRelations(item);
            }

            if ((parts & Parts.Actions) != 0)
            {
                Inspector.Section("Actions");
                DrawActions(item);
            }
        }

        /// <summary>Zoom out and in buttons around the zoom factor (whole steps).</summary>
        protected static void ZoomStepper(ref float zoom, float min, float max)
        {
            if (Ui.IconButton("zoomout", Icons.ZoomOut, "Zoom out", enabled: zoom > min))
            {
                zoom = MathF.Max(min, zoom - 1f);
            }

            ImGui.SameLine(0f, 2f);
            ImGui.AlignTextToFramePadding();
            ImGui.PushFont(Theme.Mono, 0f);
            ImGui.TextColored(Theme.TextSecondary, $"{zoom,2:0}x");
            ImGui.PopFont();
            ImGui.SameLine(0f, 2f);

            if (Ui.IconButton("zoomin", Icons.ZoomIn, "Zoom in", enabled: zoom < max))
            {
                zoom = MathF.Min(max, zoom + 1f);
            }
        }

        /// <summary>A zoomable sprite preview on a checkerboard.</summary>
        protected void SpritePreview(in ClassicUO.Renderer.SpriteInfo sprite, ref float zoom, float height = 260f, bool mirror = false)
        {
            ZoomStepper(ref zoom, 1f, 8f);

            var draw = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            var size = new Vector2(MathF.Max(1f, ImGui.GetContentRegionAvail().X), height);
            ImGui.Dummy(size);
            Widgets.Checker(draw, min, min + size);

            if (sprite.Texture != null)
            {
                draw.PushClipRect(min, min + size, true);
                Widgets.DrawSprite(draw, Context.Renderer, sprite, min, min + size, zoom, mirror);
                draw.PopClipRect();
            }
        }
    }
}
