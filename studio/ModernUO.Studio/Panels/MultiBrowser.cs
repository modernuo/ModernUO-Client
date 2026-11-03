using System.Numerics;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Hexa.NET.ImGui;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>Multis (multis.bin): houses, boats and the like, drawn from their components' static art.</summary>
    internal sealed class MultiBrowser : Panel
    {
        private readonly List<(int Id, int Components)> _multis = new();
        private List<MultiInfo> _components;
        private string _filter = "";
        private bool _dirty = true;
        private int _selected = -1;
        private float _zoom = 1f;
        private Vector2 _pan;
        private bool _hideInvisible = true;
        private int _maxZ = 127;
        private List<MultiInfo> _order;
        private (int, bool, int) _orderFor;
        private readonly List<int> _rows = new();
        private readonly Dictionary<int, Composite> _composites = new();
        private float _thumb = 96f;
        private bool _fitPending;

        public MultiBrowser(StudioContext context) : base(context, false)
        {
        }

        public override string Title => "Multis###multis";
        public override string MenuName => "Multis";
        protected override Vector2 DefaultSize => new(1000, 680);

        public override void OnPackChanged()
        {
            _dirty = true;
            _selected = -1;
            _components = null;
            _order = null;
            _composites.Clear();
        }

        public override void SelectById(int id)
        {
            if (Context.Assets != null && id < Context.Assets.Files.Multis.Count)
            {
                _selected = id;
                _components = Context.Assets.Files.Multis.GetMultis((uint)id);
                _pan = Vector2.Zero;
                _maxZ = 127;
                _fitPending = true;
            }
        }

        protected override void DrawContent()
        {
            var multis = Context.Assets.Files.Multis;

            if (Context.Assets.Arts == null || !Context.Assets.Loaded("multis"))
            {
                ImGui.TextDisabled("multis.bin or the art did not load (see Pack Info).");

                return;
            }

            if (_dirty)
            {
                Rebuild(multis);
            }

            if (ImGui.BeginChild("list", new Vector2(_thumb + 170f, 0), ImGuiChildFlags.Borders))
            {
                ImGui.SetNextItemWidth(-1f);

                if (ImGui.InputTextWithHint("##filter", "multi id", ref _filter, 16))
                {
                    FilterRows();
                }

                ImGui.SetNextItemWidth(-1f);
                ImGui.SliderFloat("##thumb", ref _thumb, 48f, 240f, "thumb %.0f px");

                if (ImGui.BeginChild("rows"))
                {
                    var draw = ImGui.GetWindowDrawList();
                    ImGuiListClipper clipper = new();
                    clipper.Begin(_rows.Count, Widgets.ThumbRowHeight(_thumb));

                    while (clipper.Step())
                    {
                        for (int r = clipper.DisplayStart; r < clipper.DisplayEnd; r++)
                        {
                            var (id, count) = _multis[_rows[r]];
                            var composite = GetComposite(multis, id);
                            string size = composite == null ? $"{count} parts" : $"{composite.Tiles.X} x {composite.Tiles.Y}, {count} parts";

                            if (Widgets.ThumbRow(id, id == _selected, _thumb, $"0x{id:X4}", size, out Vector2 thumbMin))
                            {
                                SelectById(id);
                            }

                            if (composite != null)
                            {
                                DrawComposite(draw, composite, thumbMin + new Vector2(2, 2), thumbMin + new Vector2(_thumb - 2, _thumb - 2));
                            }
                        }
                    }

                    clipper.End();
                }

                ImGui.EndChild();
            }

            ImGui.EndChild();
            ImGui.SameLine();

            if (ImGui.BeginChild("view", Vector2.Zero, ImGuiChildFlags.Borders))
            {
                if (_components != null)
                {
                    DrawMulti();
                }
                else
                {
                    ImGui.TextDisabled("Select a multi.");
                }
            }

            ImGui.EndChild();
        }

        private void Rebuild(MultiLoader multis)
        {
            _dirty = false;
            _multis.Clear();

            for (int id = 0; id < multis.Count; id++)
            {
                var list = multis.GetMultis((uint)id);

                if (list is { Count: > 0 })
                {
                    _multis.Add((id, list.Count));
                }
            }

            FilterRows();
        }

        private void FilterRows()
        {
            _rows.Clear();
            bool byId = Widgets.TryParseId(_filter, out int filterId);

            for (int i = 0; i < _multis.Count; i++)
            {
                if (!byId || _multis[i].Id == filterId)
                {
                    _rows.Add(i);
                }
            }
        }

        /// <summary>A multi's visible components in draw order and their pixel bounds, from the sprite index (no decode).</summary>
        private sealed class Composite
        {
            public MultiInfo[] Order;
            public Vector2 Min, Max;
            public Point Tiles;
        }

        private struct Point
        {
            public int X, Y;
        }

        private Composite GetComposite(MultiLoader multis, int id)
        {
            if (_composites.TryGetValue(id, out var composite))
            {
                return composite;
            }

            var list = multis.GetMultis((uint)id);
            var sprites = Context.Assets.Files.Arts.PackSpriteEntries;
            var order = list.Where(c => c.IsVisible).OrderBy(c => c.X + c.Y).ThenBy(c => c.Z).ToArray();

            if (order.Length == 0)
            {
                _composites[id] = null;

                return null;
            }

            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;

            foreach (var c in order)
            {
                int index = c.ID + ArtLoader.MAX_LAND_DATA_INDEX_COUNT;
                int w = index < sprites.Length ? sprites[index].OrigW : 44;
                int h = index < sprites.Length ? sprites[index].OrigH : 44;
                Vector2 p = ComponentPos(c, w, h);
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p + new Vector2(w, h));
                minX = Math.Min(minX, c.X);
                maxX = Math.Max(maxX, c.X);
                minY = Math.Min(minY, c.Y);
                maxY = Math.Max(maxY, c.Y);
            }

            composite = new Composite
            {
                Order = order,
                Min = min,
                Max = max,
                Tiles = new Point { X = maxX - minX + 1, Y = maxY - minY + 1 }
            };
            _composites[id] = composite;

            return composite;
        }

        // (sx, sy) is the top corner of the component's 44x44 tile diamond; a static's bottom centre sits on its bottom
        // corner, 44 px lower
        private static Vector2 ComponentPos(in MultiInfo c, float w, float h)
        {
            float sx = (c.X - c.Y) * 22f;
            float sy = (c.X + c.Y) * 22f - c.Z * 4f;

            return new Vector2(sx - w * 0.5f, sy + 44f - h);
        }

        /// <summary>The whole multi scaled to fit [min, max]; components still decoding fill in on later frames.</summary>
        private void DrawComposite(ImDrawListPtr draw, Composite composite, Vector2 min, Vector2 max)
        {
            Vector2 size = composite.Max - composite.Min;
            Vector2 box = max - min;
            float scale = MathF.Min(1f, MathF.Min(box.X / MathF.Max(1f, size.X), box.Y / MathF.Max(1f, size.Y)));
            Vector2 origin = min + (box - size * scale) * 0.5f - composite.Min * scale;
            var art = Context.Assets.Arts;

            draw.PushClipRect(min, max, true);

            foreach (var c in composite.Order)
            {
                if (!art.RequestArt(c.ID))
                {
                    continue;
                }

                SpriteInfo sprite = art.GetArt(c.ID);

                if (sprite.Texture != null)
                {
                    Widgets.DrawSpriteAt(draw, Context.Renderer, sprite, origin + ComponentPos(c, sprite.UV.Width, sprite.UV.Height) * scale, scale);
                }
            }

            draw.PopClipRect();
        }

        private void DrawMulti()
        {
            Widgets.CopyableId("Multi", _selected);
            ImGui.SameLine();
            ImGui.TextColored(Widgets.Dim, $"{_components.Count} components. Drag to pan, wheel to zoom.");
            ImGui.Checkbox("hide invisible", ref _hideInvisible);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200f);
            ImGui.SliderInt("max z", ref _maxZ, -128, 127);
            ImGui.SameLine();

            if (ImGui.Button("Fit"))
            {
                _fitPending = true;
            }

            float tableHeight = 180f;
            var draw = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            // a docked window has no width on its first frame, and ImGui asserts on an empty button
            var size = new Vector2(MathF.Max(1f, ImGui.GetContentRegionAvail().X), MathF.Max(200f, ImGui.GetContentRegionAvail().Y - tableHeight));
            ImGui.InvisibleButton("canvas", size);

            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                _pan += ImGui.GetIO().MouseDelta;
            }

            if (ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0)
            {
                _zoom = Math.Clamp(_zoom * (ImGui.GetIO().MouseWheel > 0 ? 1.25f : 0.8f), 0.25f, 8f);
            }

            // a newly selected multi opens zoomed to fit and centred on its bounds
            if (_fitPending && GetComposite(Context.Assets.Files.Multis, _selected) is { } fit)
            {
                _fitPending = false;
                Vector2 extent = Vector2.Max(fit.Max - fit.Min, Vector2.One);
                _zoom = Math.Clamp(MathF.Min(size.X / extent.X, size.Y / extent.Y) * 0.9f, 0.25f, 2f);
                _pan = -(fit.Min + fit.Max) * 0.5f * _zoom;
            }

            Widgets.Checker(draw, min, min + size, 16f);
            draw.PushClipRect(min, min + size, true);

            var key = (_selected, _hideInvisible, _maxZ);

            if (_order == null || _orderFor != key)
            {
                // painter's order: back to front by tile row, then height
                _orderFor = key;
                _order = _components
                    .Where(c => (c.IsVisible || !_hideInvisible) && c.Z <= _maxZ)
                    .OrderBy(c => c.X + c.Y).ThenBy(c => c.Z).ToList();
            }

            var order = _order;
            Vector2 origin = min + size * 0.5f + _pan;
            var art = Context.Assets.Arts;

            foreach (var c in order)
            {
                if (!art.RequestArt(c.ID))
                {
                    continue;
                }

                SpriteInfo sprite = art.GetArt(c.ID);

                if (sprite.Texture == null)
                {
                    continue;
                }

                Widgets.DrawSpriteAt(draw, Context.Renderer, sprite, origin + ComponentPos(c, sprite.UV.Width, sprite.UV.Height) * _zoom, _zoom);
            }

            draw.PopClipRect();

            if (ImGui.BeginTable("components", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV, new Vector2(0, tableHeight - 8f)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Graphic");
                ImGui.TableSetupColumn("X");
                ImGui.TableSetupColumn("Y");
                ImGui.TableSetupColumn("Z");
                ImGui.TableSetupColumn("Visible");
                ImGui.TableHeadersRow();

                for (int i = 0; i < _components.Count; i++)
                {
                    var c = _components[i];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    if (ImGui.Selectable($"0x{c.ID:X4}##{i}", false, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        Context.ShowArt(c.ID, false);
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(c.X.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(c.Y.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(c.Z.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(c.IsVisible ? "yes" : "no");
                }

                ImGui.EndTable();
            }
        }
    }
}
