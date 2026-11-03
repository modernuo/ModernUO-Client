using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;
using ModernUO.Studio.Panels;

namespace ModernUO.Studio
{
    /// <summary>What every panel shares: the device, the ImGui renderer, the settings, the open pack and cross-panel navigation.</summary>
    internal sealed class StudioContext
    {
        private readonly List<Panel> _panels = new();
        private SoundEffectInstance _playing;
        private SoundEffect _playingEffect;

        public StudioContext(GraphicsDevice device, ImGuiRenderer renderer, StudioSettings settings)
        {
            Device = device;
            Renderer = renderer;
            Settings = settings;
        }

        public GraphicsDevice Device { get; }
        public ImGuiRenderer Renderer { get; }
        public StudioSettings Settings { get; }
        public StudioAssets Assets { get; private set; }
        public IReadOnlyList<Panel> Panels => _panels;

        public void Add(Panel panel) => _panels.Add(panel);

        public T Get<T>() where T : Panel
        {
            foreach (var panel in _panels)
            {
                if (panel is T t)
                {
                    return t;
                }
            }

            return null;
        }

        public bool OpenPack(string root)
        {
            StudioAssets assets;

            try
            {
                assets = StudioAssets.Open(root, Device);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                StudioLog.Error($"Could not open pack '{root}': {ex.Message}");

                return false;
            }

            ClosePack();
            Assets = assets;
            Settings.AddRecent(root);
            Settings.Save();

            int failed = assets.Results.Count(r => r.Error != null);
            StudioLog.Info($"Opened pack {root} (from client {assets.Manifest.SourceClientVersion}, {assets.Results.Count - failed} sections loaded, {failed} failed)");

            foreach (var panel in _panels)
            {
                panel.OnPackChanged();
            }

            return true;
        }

        public void ClosePack()
        {
            if (Assets == null)
            {
                return;
            }

            StopSound();

            foreach (var panel in _panels)
            {
                panel.OnPackChanged();
            }

            try
            {
                Assets.Dispose();
            }
            catch (Exception ex)
            {
                // a loader that never loaded can fail its own teardown; the pack is closed either way
                StudioLog.Warn($"Closing the pack: {ex.Message}");
            }

            Assets = null;
        }

        public void PlaySound(byte[] pcm)
        {
            StopSound();

            if (pcm == null || pcm.Length < 2)
            {
                return;
            }

            try
            {
                _playingEffect = new SoundEffect(pcm, 22050, AudioChannels.Mono);
                _playing = _playingEffect.CreateInstance();
                _playing.Play();
            }
            catch (Exception ex) when (ex is NoAudioHardwareException or InvalidOperationException or ArgumentException)
            {
                StudioLog.Warn($"Sound playback failed: {ex.Message}");
            }
        }

        public bool IsSoundPlaying => _playing is { State: SoundState.Playing };

        public void StopSound()
        {
            _playing?.Stop();
            _playing?.Dispose();
            _playing = null;
            _playingEffect?.Dispose();
            _playingEffect = null;
        }

        public void ShowArt(int id, bool land) => Get<ArtBrowser>()?.Select(id, land);

        public void ShowTileData(int id, bool land) => Get<TileDataBrowser>()?.Select(id, land);

        public void ShowHue(int hue) => Get<HueBrowser>()?.Select(hue);

        public void ShowGump(int id) => Get<GumpBrowser>()?.Select(id);

        public void ShowTexmap(int id) => Get<TexmapBrowser>()?.SelectItem(id);

        public void ShowBody(int id) => Get<AnimationBrowser>()?.SelectItem(id);

        public void ShowMulti(int id) => Get<MultiBrowser>()?.SelectItem(id);
    }
}
