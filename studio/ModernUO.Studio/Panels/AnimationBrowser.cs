using System.Numerics;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Hexa.NET.ImGui;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>Mobile and equipment animations (anim/): every body in the pack's resolved body set, per action and direction.</summary>
    internal sealed class AnimationBrowser : AssetBrowser
    {
        private static readonly string[] _directions = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        private int _action;
        private int _direction = 3; // SE: the facing UO shows mobiles in most often
        private bool _playing = true;
        private float _fps = 10f;
        private double _frameTime;
        private int _frame;
        private float _zoom = 2f;
        private bool _showCenter = true;
        private short[] _thumbActions; // per body: the action its thumbnail shows (-1 none, -2 not looked up yet)

        public AnimationBrowser(StudioContext context) : base(context, false)
        {
        }

        public override string Title => "Animations###anims";
        public override string MenuName => "Animations";
        public override string Icon => Icons.Person;

        private AnimationsLoader Loader => Context.Assets.Files.Animations;
        private ClassicUO.Renderer.Animations.Animations Atlas => Context.Assets.Animations;

        protected override string Kind => "Body";
        protected override string SearchHint => "body id (400, 0x190) or type";
        protected override float DefaultCell => 88f;
        protected override float DefaultInspectorWidth => 480f;
        protected override Parts InspectorParts => Parts.All;

        protected override int SlotCount => Context.Assets?.Animations == null || Loader.PackBodies == null ? 0 : Loader.BodyCount;

        protected override bool Exists(int id)
        {
            ref readonly var body = ref Loader.GetBody((ushort)id);

            return body.ActionCount != 0 || body.AliasBody != 0xFFFF;
        }

        protected override string NameOf(int id) => Loader.GetBody((ushort)id).Type.ToString();

        protected override string DetailOf(int id)
        {
            ref readonly var body = ref Loader.GetBody((ushort)id);

            return body.ActionCount == 0 && body.AliasBody != 0xFFFF ? $"drawn as 0x{body.AliasBody:X}" : $"{body.ActionCount} actions";
        }

        public override void OnPackChanged()
        {
            base.OnPackChanged();
            _thumbActions = null;
        }

        protected override void OnSelected(int id)
        {
            _frame = 0;

            if (!Loader.HasAction((ushort)SourceBody(id), _action))
            {
                _action = Math.Max(0, (int)ThumbAction(id));
            }
        }

        /// <summary>An alias body (Body.def) has no actions of its own: GetAnimationFrames draws the alias.</summary>
        private int SourceBody(int id)
        {
            ref readonly var body = ref Loader.GetBody((ushort)id);

            return body.ActionCount == 0 && body.AliasBody != 0xFFFF ? body.AliasBody : id;
        }

        private short ThumbAction(int id)
        {
            if (_thumbActions == null || _thumbActions.Length != Loader.BodyCount)
            {
                _thumbActions = new short[Loader.BodyCount];
                Array.Fill(_thumbActions, (short)-2);
            }

            if (_thumbActions[id] == -2)
            {
                int source = SourceBody(id);
                _thumbActions[id] = -1;

                for (int a = 0; a < AnimationsLoader.MAX_ACTIONS; a++)
                {
                    if (Loader.HasAction((ushort)source, a))
                    {
                        _thumbActions[id] = (short)a;

                        break;
                    }
                }
            }

            return _thumbActions[id];
        }

        private Span<SpriteInfo> Frames(int id, int action, out bool mirror)
        {
            byte dir = (byte)_direction;
            mirror = false;
            // stored directions are 0..4; 5..7 draw mirrored (the client's GetAnimDirection)
            Atlas.GetAnimDirection(ref dir, ref mirror);

            return Atlas.GetAnimationFrames((ushort)id, (byte)action, dir, out _, out _);
        }

        /// <summary>Frame 0 of the body's first action, facing the preview's direction.</summary>
        protected override void DrawThumbnail(ImDrawListPtr draw, int id, Vector2 min, Vector2 max)
        {
            int action = ThumbAction(id);

            if (action < 0)
            {
                return;
            }

            Span<SpriteInfo> frames = Frames(id, action, out bool mirror);

            if (frames.IsEmpty)
            {
                return;
            }

            if (frames[0].Texture == null)
            {
                Widgets.Pending(draw, min, max);

                return;
            }

            Widgets.DrawSprite(draw, Context.Renderer, frames[0], min, max, 2f, mirror);
        }

        protected override void DrawPreview(int id)
        {
            ushort source = (ushort)SourceBody(id);
            ImGui.SetNextItemWidth(90f);

            if (ImGui.BeginCombo("action", $"{_action}"))
            {
                for (int a = 0; a < AnimationsLoader.MAX_ACTIONS; a++)
                {
                    if (Loader.HasAction(source, a) && ImGui.Selectable($"{a}", a == _action))
                    {
                        _action = a;
                        _frame = 0;
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(70f);

            if (ImGui.BeginCombo("facing", _directions[_direction]))
            {
                for (int d = 0; d < 8; d++)
                {
                    if (ImGui.Selectable(_directions[d], d == _direction))
                    {
                        _direction = d;
                        _frame = 0;
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();

            if (Ui.IconButton("play", _playing ? Icons.Pause : Icons.Play, _playing ? "Pause" : "Play", _playing))
            {
                _playing = !_playing;
            }

            ImGui.SetNextItemWidth(110f);
            ImGui.SliderFloat("fps", ref _fps, 1f, 30f, "%.0f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110f);
            ImGui.SliderFloat("zoom", ref _zoom, 1f, 6f, "%.0fx");
            ImGui.SameLine();
            Ui.Toggle("feet", ref _showCenter);

            Span<SpriteInfo> frames = Frames(id, _action, out bool mirror);

            if (frames.IsEmpty)
            {
                ImGui.TextDisabled("No frames for this action and direction.");

                return;
            }

            if (_playing)
            {
                _frameTime += ImGui.GetIO().DeltaTime;

                if (_frameTime >= 1.0 / _fps)
                {
                    _frameTime = 0;
                    _frame = (_frame + 1) % frames.Length;
                }
            }

            _frame = Math.Clamp(_frame, 0, frames.Length - 1);
            ImGui.SetNextItemWidth(-1f);
            ImGui.SliderInt("##frame", ref _frame, 0, frames.Length - 1, $"frame %d / {frames.Length - 1}");

            var draw = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            var size = new Vector2(MathF.Max(1f, ImGui.GetContentRegionAvail().X), 300f);
            ImGui.Dummy(size);
            Widgets.Checker(draw, min, min + size);

            ref SpriteInfo frame = ref frames[_frame];
            // the frame's anchor is the mobile's feet: a point a little below the middle of the view
            var anchor = new Vector2(MathF.Floor(min.X + size.X * 0.5f), MathF.Floor(min.Y + size.Y * 0.8f));

            if (frame.Texture != null)
            {
                float x = mirror ? anchor.X - (frame.UV.Width - frame.Center.X) * _zoom : anchor.X - frame.Center.X * _zoom;
                float y = anchor.Y - (frame.UV.Height + frame.Center.Y) * _zoom;
                draw.PushClipRect(min, min + size, true);
                Widgets.DrawSpriteAt(draw, Context.Renderer, frame, new Vector2(x, y), _zoom, mirror);
                draw.PopClipRect();
            }
            else
            {
                Widgets.Pending(draw, min, min + size);
            }

            if (_showCenter)
            {
                draw.AddLine(anchor - new Vector2(6, 0), anchor + new Vector2(6, 0), Widgets.Col(1f, 0.3f, 0.3f));
                draw.AddLine(anchor - new Vector2(0, 6), anchor + new Vector2(0, 6), Widgets.Col(1f, 0.3f, 0.3f));
            }

            // every frame of this direction
            if (ImGui.BeginChild("strip", new Vector2(0, 76f), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
            {
                var stripDraw = ImGui.GetWindowDrawList();

                for (int i = 0; i < frames.Length; i++)
                {
                    if (i > 0)
                    {
                        ImGui.SameLine();
                    }

                    Vector2 p = ImGui.GetCursorScreenPos();

                    if (ImGui.InvisibleButton($"f{i}", new Vector2(56f, 56f)))
                    {
                        _frame = i;
                        _playing = false;
                    }

                    stripDraw.AddRectFilled(p, p + new Vector2(56f, 56f), i == _frame ? Widgets.Col(0.26f, 0.42f, 0.66f, 0.55f) : Widgets.Col(0.12f, 0.12f, 0.13f));
                    Widgets.DrawSprite(stripDraw, Context.Renderer, frames[i], p, p + new Vector2(56f, 56f), 1f, mirror);
                }
            }

            ImGui.EndChild();
        }

        protected override void DrawProperties(int id)
        {
            ref readonly var body = ref Loader.GetBody((ushort)id);

            if (Inspector.BeginProperties())
            {
                Inspector.Property("Type", body.Type.ToString());
                Inspector.Property("Actions", body.ActionCount.ToString());
                Inspector.Property("Flags", $"0x{body.Flags:X}");

                if (body.SourceBody != id && body.ActionCount != 0)
                {
                    Inspector.Property("Imported from", Inspector.Id(body.SourceBody));
                }

                Inspector.EndProperties();
            }
        }

        protected override void DrawRelations(int id)
        {
            ref readonly var body = ref Loader.GetBody((ushort)id);

            if (body.AliasBody == 0xFFFF && body.CorpseBody == 0xFFFF)
            {
                ImGui.TextColored(Widgets.Dim, "Drawn as itself.");

                return;
            }

            if (!Inspector.BeginProperties("rel"))
            {
                return;
            }

            if (body.AliasBody != 0xFFFF && Inspector.PropertyLink("Drawn as", $"body {Inspector.Id(body.AliasBody)}, hue {body.AliasHue}"))
            {
                SelectItem(body.AliasBody);
            }

            if (body.CorpseBody != 0xFFFF && Inspector.PropertyLink("Corpse", $"body {Inspector.Id(body.CorpseBody)}"))
            {
                SelectItem(body.CorpseBody);
            }

            Inspector.EndProperties();
        }

        protected override void DrawActions(int id)
        {
            if (Ui.Button($"{Icons.FileImage}  Export frame"))
            {
                Export(id, _frame, _frame);
            }

            ImGui.SameLine();

            if (Ui.Button($"{Icons.Images}  Export direction"))
            {
                Export(id, 0, int.MaxValue);
            }
        }

        /// <summary>Frames [first, last] of the current action and direction as PNGs (mirrored directions are exported unmirrored).</summary>
        private void Export(int id, int first, int last)
        {
            byte dir = (byte)_direction;
            bool mirror = false;
            Atlas.GetAnimDirection(ref dir, ref mirror);
            ushort body = Loader.ResolveBody((ushort)id, false, out _);

            // stored client direction d is UO direction d + 3 (Animations.LoadDirection)
            if (!Loader.TryGetDirection(body, _action, dir + 3, out var entry))
            {
                return;
            }

            string dirName = Path.Combine(Context.Settings.ResolveExportDirectory(), $"body_0x{id:X4}");
            int count = 0;

            for (int i = first; i < entry.FrameCount && i <= last; i++)
            {
                uint index = entry.FirstFrame + (uint)i;
                ref readonly var frame = ref Loader.GetFrame(index);

                if (frame.Sprite.OrigW == 0 || frame.Sprite.OrigH == 0)
                {
                    continue;
                }

                uint[] pixels = Loader.DecodeFrame(index);
                Images.SavePng(Context.Device, Path.Combine(dirName, $"action{_action}_dir{dir}_frame{i}.png"), pixels, frame.Sprite.OrigW, frame.Sprite.OrigH);
                count++;
            }

            StudioLog.Info($"Exported {count} frame(s) to {dirName}");
        }
    }
}
