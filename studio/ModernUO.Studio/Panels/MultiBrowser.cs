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
        }

        public override void SelectById(int id)
        {
            if (Context.Assets != null && id < Context.Assets.Files.Multis.Count)
            {
                _selected = id;
                _components = Context.Assets.Files.Multis.GetMultis((uint)id);
                _pan = Vector2.Zero;
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

            if (ImGui.BeginChild("list", new Vector2(200f, 0), ImGuiChildFlags.Borders))
            {
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("##filter", "multi id", ref _filter, 16);
                bool byId = Widgets.TryParseId(_filter, out int filterId);

                foreach (var (id, count) in _multis)
                {
                    if (byId && _filter.Length > 0 && id != filterId)
                    {
                        continue;
                    }

                    if (ImGui.Selectable($"0x{id:X4}  ({count})", id == _selected))
                    {
                        _selected = id;
                        _components = multis.GetMultis((uint)id);
                        _pan = Vector2.Zero;
                        _maxZ = 127;
                    }
                }
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

            if (ImGui.Button("Reset view"))
            {
                _pan = Vector2.Zero;
                _zoom = 1f;
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

                // (sx, sy) is the top corner of the component's 44x44 tile diamond; a static's bottom centre sits on its
                // bottom corner, 44 px lower
                float sx = (c.X - c.Y) * 22f;
                float sy = (c.X + c.Y) * 22f - c.Z * 4f;
                var pos = new Vector2(sx - sprite.UV.Width * 0.5f, sy + 44f - sprite.UV.Height);
                Widgets.DrawSpriteAt(draw, Context.Renderer, sprite, origin + pos * _zoom, _zoom);
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
