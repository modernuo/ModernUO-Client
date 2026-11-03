using System.Numerics;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>Sound effects (audio/sounds.bin, 22,050 Hz mono PCM) and music (audio/music.bin, MP3): play and export.</summary>
    internal sealed class SoundBrowser : Panel
    {
        private readonly List<(int Id, long Bytes)> _sounds = new();
        private readonly List<(int Id, string Name, bool Loop)> _music = new();
        private readonly List<int> _rows = new();
        private readonly Dictionary<int, string> _names = new();
        private string _filter = "";
        private bool _dirty = true;
        private bool _filterDirty = true;
        private int _selected = -1;

        public SoundBrowser(StudioContext context) : base(context, false)
        {
        }

        public override string Title => "Sounds###sounds";
        public override string MenuName => "Sounds & Music";
        protected override Vector2 DefaultSize => new(620, 560);

        public override void OnPackChanged()
        {
            _dirty = true;
            _selected = -1;
        }

        protected override void DrawContent()
        {
            var loader = Context.Assets.Files.Sounds;

            if (_dirty)
            {
                Rebuild();
            }

            if (!ImGui.BeginTabBar("audio"))
            {
                return;
            }

            if (ImGui.BeginTabItem($"Sounds ({_sounds.Count})"))
            {
                if (!loader.HasPackSounds)
                {
                    ImGui.TextDisabled("This pack has no audio/sounds.bin.");
                }
                else
                {
                    DrawSounds(loader);
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem($"Music ({_music.Count})"))
            {
                if (!loader.HasPackMusic)
                {
                    ImGui.TextDisabled("This pack has no audio/music.bin.");
                }
                else
                {
                    DrawMusic(loader);
                }

                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        private void Rebuild()
        {
            _dirty = false;
            _filterDirty = true;
            _sounds.Clear();
            _music.Clear();
            _names.Clear();
            var loader = Context.Assets.Files.Sounds;

            if (loader.HasPackSounds)
            {
                for (int id = 0; id < ClassicUO.Assets.SoundsLoader.MAX_SOUND_DATA_INDEX_COUNT; id++)
                {
                    if (loader.TryGetSoundSource(id, out _, out long bytes) && bytes > 0)
                    {
                        _sounds.Add((id, bytes));
                    }
                }
            }

            if (loader.HasPackMusic)
            {
                for (int id = 0; id < 1024; id++)
                {
                    if (loader.TryGetMusic(id, out string name, out bool loop, out _))
                    {
                        _music.Add((id, name, loop));
                    }
                }
            }
        }

        private void DrawSounds(ClassicUO.Assets.SoundsLoader loader)
        {
            ImGui.SetNextItemWidth(200f);
            _filterDirty |= ImGui.InputTextWithHint("##filter", "id or name", ref _filter, 64);
            ImGui.SameLine();

            if (ImGui.Button("Stop"))
            {
                Context.StopSound();
            }

            ImGui.SameLine();
            ImGui.TextDisabled("Double-click plays.");

            if (_filterDirty)
            {
                FilterSounds(loader);
            }

            if (!ImGui.BeginTable("sounds", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV))
            {
                return;
            }

            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Length", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableHeadersRow();

            ImGuiListClipper clipper = new();
            clipper.Begin(_rows.Count);

            while (clipper.Step())
            {
                for (int r = clipper.DisplayStart; r < clipper.DisplayEnd; r++)
                {
                    DrawSoundRow(loader, _rows[r]);
                }
            }

            clipper.End();
            ImGui.EndTable();
        }

        // names live in the sound records, read with the PCM: fetched once per id, for the visible rows or a text filter
        private string NameOf(ClassicUO.Assets.SoundsLoader loader, int id)
        {
            if (!_names.TryGetValue(id, out string name))
            {
                loader.TryGetSound(id, out _, out name);
                _names[id] = name ?? "";
            }

            return _names[id];
        }

        private void FilterSounds(ClassicUO.Assets.SoundsLoader loader)
        {
            _filterDirty = false;
            _rows.Clear();
            string filter = _filter.Trim();
            bool byId = Widgets.TryParseId(filter, out int filterId);

            for (int i = 0; i < _sounds.Count; i++)
            {
                int id = _sounds[i].Id;

                if (filter.Length == 0 || (byId && id == filterId) || NameOf(loader, id).Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    _rows.Add(i);
                }
            }
        }

        private void DrawSoundRow(ClassicUO.Assets.SoundsLoader loader, int row)
        {
            var (id, bytes) = _sounds[row];
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                if (ImGui.Selectable($"0x{id:X4}##{id}", id == _selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                {
                    _selected = id;

                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        Play(loader, id);
                    }
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(NameOf(loader, id));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{bytes / 44100.0:0.00} s");
                ImGui.TableNextColumn();
                ImGui.PushID(id);

                if (ImGui.SmallButton("play"))
                {
                    Play(loader, id);
                }

                ImGui.SameLine();

                if (ImGui.SmallButton("wav"))
                {
                    Export(loader, id);
                }

                ImGui.PopID();
            }
        }

        private void Play(ClassicUO.Assets.SoundsLoader loader, int id)
        {
            if (loader.TryGetSound(id, out byte[] pcm, out _))
            {
                Context.PlaySound(pcm);
            }
        }

        private void Export(ClassicUO.Assets.SoundsLoader loader, int id)
        {
            if (!loader.TryGetSound(id, out byte[] pcm, out string name))
            {
                return;
            }

            string path = Path.Combine(Context.Settings.ResolveExportDirectory(), $"sound_0x{id:X4}{(string.IsNullOrEmpty(name) ? "" : "_" + Sanitize(name))}.wav");
            Images.SaveWav(path, pcm);
            StudioLog.Info($"Exported {path}");
        }

        private void DrawMusic(ClassicUO.Assets.SoundsLoader loader)
        {
            ImGui.TextDisabled("Music is stored as the original MP3 bytes; export writes them unchanged.");

            if (!ImGui.BeginTable("music", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV))
            {
                return;
            }

            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Loop", ImGuiTableColumnFlags.WidthFixed, 40f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();

            foreach (var (id, name, loop) in _music)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(id.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(name ?? "");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(loop ? "yes" : "");
                ImGui.TableNextColumn();
                ImGui.PushID(id);

                if (ImGui.SmallButton("mp3") && loader.TryGetMusic(id, out _, out _, out Func<Stream> open))
                {
                    string path = Path.Combine(Context.Settings.ResolveExportDirectory(), $"music_{id}_{Sanitize(name ?? "")}.mp3");
                    using (var src = open())
                    using (var dst = File.Create(path))
                    {
                        src.CopyTo(dst);
                    }

                    StudioLog.Info($"Exported {path}");
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name;
        }
    }
}
