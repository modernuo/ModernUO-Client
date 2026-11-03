using ClassicUO.Assets;
using ClassicUO.Renderer;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>Gump images (gumps/gumps.bin), with hue preview and PNG export.</summary>
    internal sealed class GumpBrowser : SpriteGridBrowser
    {
        private float _zoom = 1f;
        private int _hue;
        private bool _partialHue;
        private CpuImage _hued;
        private int _huedFor = -1;

        public GumpBrowser(StudioContext context) : base(context, true)
        {
        }

        public override string Title => "Gumps###gumps";
        public override string MenuName => "Gumps";

        private GumpsLoader Loader => Context.Assets.Files.Gumps;

        protected override int SlotCount => Context.Assets?.Gumps == null ? 0 : Loader.Count;

        protected override bool Exists(int id) => Loader.PixelCount((uint)id) > 0;

        protected override bool Request(int id) => Context.Assets.Gumps.Request((uint)id);

        protected override SpriteInfo Get(int id) => Context.Assets.Gumps.GetGump((uint)id);

        public void Select(int id) => SelectId(id);

        protected override void OnSelected(int id) => _huedFor = -1;

        public override void OnPackChanged()
        {
            base.OnPackChanged();
            _hued?.Clear();
            _huedFor = -1;
        }

        protected override void DrawContextMenu(int id)
        {
            if (ImGui.MenuItem("Export PNG"))
            {
                Export(id);
            }
        }

        protected override void DrawDetails(int id)
        {
            Widgets.CopyableId("Gump", id);

            if (Loader.TryGetSize((uint)id, out int w, out int h))
            {
                ImGui.TextUnformatted($"Size: {w} x {h}");
            }

            var sprite = Request(id) ? Get(id) : SpriteInfo.Empty;

            if (_hue != 0)
            {
                UpdateHued(id);
                sprite = _hued.Sprite;
            }

            Preview(Context, sprite, ref _zoom, 320f);

            ImGui.SetNextItemWidth(120f);

            if (ImGui.InputInt("hue", ref _hue))
            {
                _hue = Math.Clamp(_hue, 0, 0xFFFF);
                _huedFor = -1;
            }

            ImGui.SameLine();

            if (ImGui.Checkbox("partial", ref _partialHue))
            {
                _huedFor = -1;
            }

            if (ImGui.Button("Export PNG"))
            {
                Export(id);
            }
        }

        private void UpdateHued(int id)
        {
            _hued ??= new CpuImage(Context.Device, Context.Renderer);

            if (_huedFor == id)
            {
                return;
            }

            _huedFor = id;
            var info = Loader.GetGump((uint)id);

            if (info.Pixels.IsEmpty)
            {
                _hued.Clear();

                return;
            }

            Images.ApplyHue(info.Pixels, Context.Assets.Files.Hues.PackHues, _hue, _partialHue);
            _hued.Set(info.Pixels, info.Width, info.Height);
        }

        private void Export(int id)
        {
            var info = Loader.GetGump((uint)id);

            if (info.Pixels.IsEmpty)
            {
                return;
            }

            if (_hue != 0)
            {
                Images.ApplyHue(info.Pixels, Context.Assets.Files.Hues.PackHues, _hue, _partialHue);
            }

            string path = Path.Combine(Context.Settings.ResolveExportDirectory(), _hue != 0 ? $"gump_0x{id:X4}_hue{_hue}.png" : $"gump_0x{id:X4}.png");
            Images.SavePng(Context.Device, path, info.Pixels, info.Width, info.Height);
            StudioLog.Info($"Exported {path}");
        }
    }
}
