using System.Numerics;
using ClassicUO.Renderer;
using Hexa.NET.ImGui;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>
    /// A browser of id-indexed sprites (art, gumps, texmaps): a filtered, virtualised grid on the left and the selected
    /// sprite's details on the right. Only the visible cells are requested from the atlas; the rest never decode.
    /// </summary>
    internal abstract class SpriteGridBrowser : Panel
    {
        private readonly List<int> _ids = new();
        private string _filter = "";
        private bool _dirty = true;
        private bool _scrollToSelection;
        private float _cell = 64f;
        private float _detailWidth = 360f;

        protected SpriteGridBrowser(StudioContext context, bool open) : base(context, open)
        {
        }

        protected int Selected { get; private set; } = -1;

        protected IReadOnlyList<int> Ids => _ids;

        /// <summary>Ids 0..SlotCount-1 are candidates; <see cref="Exists"/> picks the ones in the grid.</summary>
        protected abstract int SlotCount { get; }

        protected abstract bool Exists(int id);

        /// <summary>True when the sprite is in the atlas; false queues a worker decode.</summary>
        protected abstract bool Request(int id);

        protected abstract SpriteInfo Get(int id);

        protected virtual string NameOf(int id) => null;

        /// <summary>An extra filter besides the text box (tiledata flags, for example).</summary>
        protected virtual bool PassesFilter(int id) => true;

        protected virtual void DrawToolbarExtras()
        {
        }

        protected abstract void DrawDetails(int id);

        protected virtual void DrawContextMenu(int id)
        {
        }

        protected virtual float DefaultCell => 64f;

        public void Invalidate() => _dirty = true;

        public override void OnPackChanged()
        {
            _dirty = true;
            Selected = -1;
        }

        public void SelectId(int id)
        {
            Focus();
            Selected = id;
            _scrollToSelection = true;

            if (!_ids.Contains(id))
            {
                // the id may be filtered out: clear the filter so the selection is visible
                _filter = "";
                _dirty = true;
            }

            OnSelected(id);
        }

        protected virtual void OnSelected(int id)
        {
        }

        public override void SelectById(int id) => SelectId(id);

        protected override void DrawContent()
        {
            if (_dirty)
            {
                Rebuild();
            }

            DrawToolbar();

            float avail = ImGui.GetContentRegionAvail().X;
            float detail = MathF.Min(_detailWidth, avail * 0.5f);

            if (ImGui.BeginChild("grid", new Vector2(avail - detail - 6f, 0), ImGuiChildFlags.Borders))
            {
                DrawGrid();
            }

            ImGui.EndChild();

            ImGui.SameLine(0, 0);
            DrawSplitter(ref _detailWidth);
            ImGui.SameLine(0, 0);

            if (ImGui.BeginChild("details", new Vector2(0, 0), ImGuiChildFlags.Borders))
            {
                if (Selected >= 0 && Selected < SlotCount)
                {
                    DrawDetails(Selected);
                }
                else
                {
                    ImGui.TextDisabled("Select an entry.");
                }
            }

            ImGui.EndChild();
        }

        private static void DrawSplitter(ref float rightWidth)
        {
            ImGui.InvisibleButton("##split", new Vector2(6f, MathF.Max(1f, ImGui.GetContentRegionAvail().Y)));

            if (ImGui.IsItemActive())
            {
                rightWidth = Math.Clamp(rightWidth - ImGui.GetIO().MouseDelta.X, 200f, 1200f);
            }

            if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            }
        }

        private void DrawToolbar()
        {
            ImGui.SetNextItemWidth(220f);

            if (ImGui.InputTextWithHint("##filter", "id (0x1F4, 500) or name", ref _filter, 128))
            {
                _dirty = true;
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(140f);
            ImGui.SliderFloat("##cell", ref _cell, 24f, 256f, "cell %.0f px");

            DrawToolbarExtras();

            ImGui.SameLine();
            ImGui.TextDisabled($"{_ids.Count} entries");
        }

        protected void Rebuild()
        {
            _dirty = false;
            _ids.Clear();

            string filter = _filter.Trim();
            bool byId = Widgets.TryParseId(filter, out int filterId);

            for (int id = 0; id < SlotCount; id++)
            {
                if (!Exists(id) || !PassesFilter(id))
                {
                    continue;
                }

                if (filter.Length > 0)
                {
                    string name = NameOf(id);
                    bool nameMatch = name != null && name.Contains(filter, StringComparison.OrdinalIgnoreCase);

                    if (!(byId && id == filterId) && !nameMatch)
                    {
                        continue;
                    }
                }

                _ids.Add(id);
            }

            // an exact id jumps there
            if (byId && _ids.Count == 1)
            {
                Selected = _ids[0];
                OnSelected(Selected);
            }
        }

        private void DrawGrid()
        {
            var draw = ImGui.GetWindowDrawList();
            float spacing = 4f;
            float step = _cell + spacing;
            int columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / step));
            int rows = (_ids.Count + columns - 1) / columns;
            int selectedIndex = Selected >= 0 ? _ids.BinarySearch(Selected) : -1;

            HandleKeys(columns, selectedIndex);

            if (_scrollToSelection && selectedIndex >= 0)
            {
                float target = MathF.Max(0f, selectedIndex / columns * step - ImGui.GetWindowHeight() * 0.4f);
                ImGui.SetScrollY(target);

                // ImGui clamps to last frame's content height: keep asking until the grid is tall enough (new window, rebuilt list)
                _scrollToSelection = ImGui.GetScrollMaxY() < target;
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

                        if (index >= _ids.Count)
                        {
                            break;
                        }

                        if (col > 0)
                        {
                            ImGui.SameLine(0, spacing);
                        }

                        DrawCell(draw, _ids[index]);
                    }
                }
            }

            clipper.End();
        }

        private void DrawCell(ImDrawListPtr draw, int id)
        {
            ImGui.PushID(id);
            Vector2 min = ImGui.GetCursorScreenPos();
            Vector2 max = min + new Vector2(_cell, _cell);
            bool clicked = ImGui.InvisibleButton("cell", new Vector2(_cell, _cell));
            bool hovered = ImGui.IsItemHovered();

            draw.AddRectFilled(min, max, id == Selected ? Widgets.Col(0.26f, 0.42f, 0.66f, 0.55f) : Widgets.Col(0.12f, 0.12f, 0.13f));

            if (Request(id))
            {
                SpriteInfo sprite = Get(id);
                Widgets.DrawSprite(draw, Context.Renderer, sprite, min + new Vector2(2, 2), max - new Vector2(2, 2), MathF.Max(1f, _cell / 44f));
            }
            else
            {
                Widgets.Pending(draw, min, max);
            }

            if (hovered)
            {
                draw.AddRect(min, max, Widgets.Col(0.9f, 0.9f, 0.9f, 0.8f));
                string name = NameOf(id);
                ImGui.SetTooltip(string.IsNullOrEmpty(name) ? $"0x{id:X4} ({id})" : $"0x{id:X4} ({id})\n{name}");
            }

            if (clicked)
            {
                Selected = id;
                OnSelected(id);
            }

            if (ImGui.BeginPopupContextItem("ctx"))
            {
                Selected = id;

                if (ImGui.MenuItem("Copy id"))
                {
                    ImGui.SetClipboardText($"0x{id:X4}");
                }

                DrawContextMenu(id);
                ImGui.EndPopup();
            }

            ImGui.PopID();
        }

        private void HandleKeys(int columns, int selectedIndex)
        {
            if (!ImGui.IsWindowFocused() || selectedIndex < 0)
            {
                return;
            }

            int next = selectedIndex;

            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow)) next++;
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow)) next--;
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) next += columns;
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) next -= columns;
            if (ImGui.IsKeyPressed(ImGuiKey.PageDown)) next += columns * 8;
            if (ImGui.IsKeyPressed(ImGuiKey.PageUp)) next -= columns * 8;

            next = Math.Clamp(next, 0, _ids.Count - 1);

            if (next != selectedIndex)
            {
                Selected = _ids[next];
                _scrollToSelection = true;
                OnSelected(Selected);
            }
        }

        /// <summary>A zoomable preview of a sprite on a checkerboard.</summary>
        protected static void Preview(StudioContext context, in SpriteInfo sprite, ref float zoom, float height = 260f)
        {
            ImGui.SetNextItemWidth(160f);
            ImGui.SliderFloat("zoom", ref zoom, 1f, 8f, "%.0fx");

            var draw = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            var size = new Vector2(ImGui.GetContentRegionAvail().X, height);
            ImGui.Dummy(size);
            Widgets.Checker(draw, min, min + size);

            if (sprite.Texture != null)
            {
                draw.PushClipRect(min, min + size, true);
                Widgets.DrawSprite(draw, context.Renderer, sprite, min, min + size, zoom);
                draw.PopClipRect();
            }
        }
    }
}
