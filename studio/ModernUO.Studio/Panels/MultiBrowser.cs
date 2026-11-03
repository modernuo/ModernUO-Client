using System.Numerics;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Hexa.NET.ImGui;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>Multis (multis.bin): houses, boats and the like, drawn from their components' static art.</summary>
    internal sealed class MultiBrowser : AssetBrowser
    {
        /// <summary>A multi's components in draw order and their pixel bounds, from the sprite index (no decode).</summary>
        private sealed class Composite
        {
            public MultiInfo[] Visible;
            public MultiInfo[] All;
            public Vector2 Min, Max;
            public int TilesX, TilesY;
            public (int Graphic, int Count)[] Graphics;
            public int[] GraphicIds;
            public Dictionary<int, int> GraphicCounts;
        }

        private readonly Dictionary<int, Composite> _composites = new();
        private float _zoom = 1f;
        private Vector2 _pan;
        private bool _hideInvisible = true;
        private int _maxZ = 127;
        private bool _fitPending;
        private MultiInfo[] _order;
        private (int, bool, int) _orderFor = (-1, false, 0);

        public MultiBrowser(StudioContext context) : base(context, false)
        {
        }

        public override string Title => "Multis###multis";
        public override string MenuName => "Multis";

        private MultiLoader Loader => Context.Assets.Files.Multis;

        protected override string Kind => "Multi";
        protected override string SearchHint => "multi id (0x64, 100)";
        protected override float DefaultCell => 128f;
        protected override float DefaultInspectorWidth => 560f;
        protected override Parts InspectorParts => Parts.All;

        protected override int SlotCount => Context.Assets?.Arts == null || !Context.Assets.Loaded("multis") ? 0 : Loader.Count;

        protected override bool Exists(int id) => GetComposite(id) != null;

        protected override string DetailOf(int id)
        {
            var c = GetComposite(id);

            return c == null ? null : $"{c.TilesX} x {c.TilesY}, {c.All.Length} parts";
        }

        public override void OnPackChanged()
        {
            base.OnPackChanged();
            _composites.Clear();
            _order = null;
        }

        protected override void OnSelected(int id)
        {
            _pan = Vector2.Zero;
            _maxZ = 127;
            _fitPending = true;
        }

        private Composite GetComposite(int id)
        {
            if (_composites.TryGetValue(id, out var composite))
            {
                return composite;
            }

            var list = Loader.GetMultis((uint)id);

            if (list is not { Count: > 0 })
            {
                _composites[id] = null;

                return null;
            }

            var sprites = Context.Assets.Files.Arts.PackSpriteEntries;
            var visible = list.Where(c => c.IsVisible).OrderBy(c => c.X + c.Y).ThenBy(c => c.Z).ToArray();
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;

            foreach (var c in visible)
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
                Visible = visible,
                All = list.ToArray(),
                Min = visible.Length == 0 ? Vector2.Zero : min,
                Max = visible.Length == 0 ? Vector2.One : max,
                TilesX = visible.Length == 0 ? 0 : maxX - minX + 1,
                TilesY = visible.Length == 0 ? 0 : maxY - minY + 1,
                Graphics = list.GroupBy(c => (int)c.ID).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).ToArray()
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

        /// <summary>The whole multi scaled to fit; components still decoding fill in on later frames.</summary>
        protected override void DrawThumbnail(ImDrawListPtr draw, int id, Vector2 min, Vector2 max)
        {
            var composite = GetComposite(id);

            if (composite == null)
            {
                return;
            }

            Vector2 size = Vector2.Max(composite.Max - composite.Min, Vector2.One);
            Vector2 box = max - min;
            float scale = MathF.Min(1f, MathF.Min(box.X / size.X, box.Y / size.Y));
            Vector2 origin = min + (box - size * scale) * 0.5f - composite.Min * scale;

            draw.PushClipRect(min, max, true);
            DrawComponents(draw, composite.Visible, origin, scale);
            draw.PopClipRect();
        }

        private void DrawComponents(ImDrawListPtr draw, MultiInfo[] components, Vector2 origin, float scale)
        {
            var art = Context.Assets.Arts;

            foreach (var c in components)
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
        }

        protected override void DrawPreview(int id)
        {
            var composite = GetComposite(id);
            ImGui.Checkbox("hide invisible", ref _hideInvisible);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160f);
            ImGui.SliderInt("max z", ref _maxZ, -128, 127);
            ImGui.SameLine();

            if (ImGui.Button("Fit"))
            {
                _fitPending = true;
            }

            ImGui.TextColored(Widgets.Dim, "Drag to pan, wheel to zoom.");

            var draw = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            // a docked window has no width on its first frame, and ImGui asserts on an empty button
            var size = new Vector2(MathF.Max(1f, ImGui.GetContentRegionAvail().X), 380f);
            ImGui.InvisibleButton("canvas", size);

            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                _pan += ImGui.GetIO().MouseDelta;
            }

            if (ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0)
            {
                _zoom = Math.Clamp(_zoom * (ImGui.GetIO().MouseWheel > 0 ? 1.25f : 0.8f), 0.1f, 8f);
            }

            // a newly selected multi opens zoomed to fit and centred on its bounds, once the canvas has a real width (a
            // docked window has none on its first frame)
            if (_fitPending && size.X > 64f)
            {
                _fitPending = false;
                Vector2 extent = Vector2.Max(composite.Max - composite.Min, Vector2.One);
                _zoom = Math.Clamp(MathF.Min(size.X / extent.X, size.Y / extent.Y) * 0.9f, 0.1f, 2f);
                _pan = -(composite.Min + composite.Max) * 0.5f * _zoom;
            }

            var key = (id, _hideInvisible, _maxZ);

            if (_order == null || _orderFor != key)
            {
                // painter's order: back to front by tile row, then height
                _orderFor = key;
                _order = composite.All
                    .Where(c => (c.IsVisible || !_hideInvisible) && c.Z <= _maxZ)
                    .OrderBy(c => c.X + c.Y).ThenBy(c => c.Z).ToArray();
            }

            Widgets.Checker(draw, min, min + size, 16f);
            draw.PushClipRect(min, min + size, true);
            DrawComponents(draw, _order, min + size * 0.5f + _pan, _zoom);
            draw.PopClipRect();
        }

        protected override void DrawProperties(int id)
        {
            var c = GetComposite(id);

            if (Inspector.BeginProperties())
            {
                Inspector.Property("Footprint", $"{c.TilesX} x {c.TilesY} tiles");
                Inspector.Property("Components", $"{c.All.Length} ({c.Visible.Length} visible)");
                Inspector.Property("Graphics", $"{c.Graphics.Length} distinct");
                Inspector.EndProperties();
            }
        }

        protected override void DrawRelations(int id)
        {
            var c = GetComposite(id);
            var tiles = Context.Assets.Files.TileData.StaticData;
            c.GraphicIds ??= c.Graphics.Select(g => g.Graphic).ToArray();
            c.GraphicCounts ??= c.Graphics.ToDictionary(g => g.Graphic, g => g.Count);

            Inspector.LinkList("Uses statics", c.GraphicIds,
                g => $"{Inspector.Id(g)} x{c.GraphicCounts[g]}  {(tiles != null && g < tiles.Length ? tiles[g].Name : "")}", g => Context.ShowArt(g, false));
        }

        protected override void DrawActions(int id)
        {
            if (ImGui.Button("Copy component list"))
            {
                var c = GetComposite(id);
                ImGui.SetClipboardText(string.Join("\n", c.All.Select(m => $"0x{m.ID:X4} {m.X} {m.Y} {m.Z} {(m.IsVisible ? 1 : 0)}")));
                StudioLog.Info($"Copied {c.All.Length} components of multi 0x{id:X4}");
            }
        }
    }
}
