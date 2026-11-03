using System.Numerics;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>
    /// A top-down overview of the map's facet from radarcol.bin, built on a worker the first time a facet is shown.
    /// The outline is the map's view; clicking or dragging moves the map there.
    /// </summary>
    internal sealed class RadarPanel : Panel
    {
        private sealed class Build
        {
            public int Facet;
            public int Scale;
            public Task<uint[]> Task;
            public int Width, Height;
            public CancellationTokenSource Cancel;
        }

        private readonly Dictionary<int, (CpuImage Image, int Scale)> _images = new();
        private Build _build;

        public RadarPanel(StudioContext context) : base(context, true)
        {
        }

        public override string Title => "Radar###radar";
        public override string MenuName => "Radar";
        public override string Icon => Icons.Radar;
        public override string MenuGroup => "Map";
        protected override Vector2 DefaultSize => new(360, 300);
        protected override ImGuiWindowFlags WindowFlags => ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        public override void OnPackChanged()
        {
            _build?.Cancel.Cancel();
            _build = null;

            foreach (var (image, _) in _images.Values)
            {
                image.Dispose();
            }

            _images.Clear();
        }

        protected override void DrawContent()
        {
            var map = Context.Get<MapPanel>();
            int facet = map?.Facet ?? -1;

            if (facet < 0)
            {
                ImGui.TextDisabled("Open the map.");

                return;
            }

            if (!_images.TryGetValue(facet, out var radar))
            {
                StartOrFinish(facet);
                ImGui.TextDisabled($"Building the radar of {MapData.FacetName(facet)}...");

                return;
            }

            var info = MapData.Info(Context.Assets, facet);
            Vector2 avail = Vector2.Max(ImGui.GetContentRegionAvail(), Vector2.One);
            float scale = MathF.Min(avail.X / info.Width, avail.Y / info.Height);
            var size = new Vector2(info.Width * scale, info.Height * scale);
            Vector2 min = ImGui.GetCursorScreenPos() + (avail - size) * 0.5f;
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton("radar", Vector2.Max(size, Vector2.One));

            var draw = ImGui.GetWindowDrawList();
            Widgets.DrawSpriteAt(draw, Context.Renderer, radar.Image.Sprite, min, scale * radar.Scale);

            // the map's view: the tiles under its four corners
            var corners = map.ViewCorners();
            var points = corners.Select(c => min + new Vector2(c.X, c.Y) * scale).ToArray();
            uint marker = Widgets.Col(1f, 0.85f, 0.2f);
            draw.AddPolyline(ref points[0], points.Length, marker, ImDrawFlags.Closed, 1.5f);

            // zoomed in, the view is a few radar pixels: a crosshair keeps it findable
            var (cx, cy) = map.CenterTile();
            Vector2 c = min + new Vector2(cx, cy) * scale;
            draw.AddLine(c - new Vector2(8, 0), c + new Vector2(8, 0), marker, 1.5f);
            draw.AddLine(c - new Vector2(0, 8), c + new Vector2(0, 8), marker, 1.5f);
            draw.AddCircle(c, 5f, marker, 16, 1.5f);

            if (ImGui.IsItemActive())
            {
                Vector2 t = (ImGui.GetIO().MousePos - min) / scale;
                map.CenterOn(facet, Math.Clamp((int)t.X, 0, info.Width - 1), Math.Clamp((int)t.Y, 0, info.Height - 1), select: false);
            }

            if (ImGui.IsItemHovered())
            {
                Vector2 t = (ImGui.GetIO().MousePos - min) / scale;
                ImGui.SetTooltip($"{(int)t.X}, {(int)t.Y}");
            }
        }

        private void StartOrFinish(int facet)
        {
            if (_build != null && _build.Facet != facet)
            {
                _build.Cancel.Cancel();
                _build = null;
            }

            if (_build == null)
            {
                var info = MapData.Info(Context.Assets, facet);
                // at most 4096 pixels on the long side: two tiles per pixel for Felucca and Trammel
                int scale = Math.Max(1, (Math.Max(info.Width, info.Height) + 4095) / 4096);
                var build = new Build { Facet = facet, Scale = scale, Cancel = new CancellationTokenSource() };
                var assets = Context.Assets;
                build.Task = Task.Run(() =>
                {
                    var pixels = MapData.BuildRadar(assets, facet, scale, out int w, out int h, build.Cancel.Token);
                    build.Width = w;
                    build.Height = h;

                    return pixels;
                });
                _build = build;

                return;
            }

            if (!_build.Task.IsCompleted)
            {
                return;
            }

            if (_build.Task.IsCompletedSuccessfully)
            {
                var image = new CpuImage(Context.Device, Context.Renderer);
                image.Set(_build.Task.Result, _build.Width, _build.Height);
                _images[facet] = (image, _build.Scale);
                StudioLog.Info($"Radar of {MapData.FacetName(facet)}: {_build.Width} x {_build.Height}");
            }
            else if (_build.Task.IsFaulted)
            {
                StudioLog.Error($"Radar of {MapData.FacetName(facet)} failed: {_build.Task.Exception?.GetBaseException().Message}");
                _images[facet] = (new CpuImage(Context.Device, Context.Renderer), 1);
            }

            _build = null;
        }
    }
}
