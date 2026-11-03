using System.Numerics;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Hexa.NET.ImGui;
using ModernUO.Studio.Gui;

namespace ModernUO.Studio.Panels
{
    /// <summary>Mobile and equipment animations (anim/): every body in the pack's resolved body set, per action and direction.</summary>
    internal sealed class AnimationBrowser : Panel
    {
        private static readonly string[] _directions = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        private readonly List<int> _bodies = new();
        private string _filter = "";
        private bool _dirty = true;
        private int _body = -1;
        private int _action;
        private int _direction = 3; // SE: the classic paperdoll-ish facing
        private bool _playing = true;
        private float _fps = 10f;
        private double _frameTime;
        private int _frame;
        private float _zoom = 2f;
        private bool _showCenter = true;
        private float _thumb = 64f;
        private short[] _thumbActions; // per body: the action its thumbnail shows (-1 none, -2 not looked up yet)

        public AnimationBrowser(StudioContext context) : base(context, false)
        {
        }

        public override string Title => "Animations###anims";
        public override string MenuName => "Animations";
        protected override Vector2 DefaultSize => new(900, 640);

        public override void OnPackChanged()
        {
            _dirty = true;
            _body = -1;
            _thumbActions = null;
        }

        protected override void DrawContent()
        {
            var loader = Context.Assets.Files.Animations;

            if (Context.Assets.Animations == null || loader.PackBodies == null)
            {
                ImGui.TextDisabled("anim/ did not load (see Pack Info).");

                return;
            }

            if (ImGui.BeginChild("bodies", new Vector2(_thumb + 200f, 0), ImGuiChildFlags.Borders))
            {
                ImGui.SetNextItemWidth(-1f);
                _dirty |= ImGui.InputTextWithHint("##filter", "body id or type", ref _filter, 32);

                if (_dirty)
                {
                    Rebuild(loader);
                }

                ImGui.TextDisabled($"{_bodies.Count} bodies");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1f);
                ImGui.SliderFloat("##thumb", ref _thumb, 32f, 160f, "thumb %.0f px");

                if (ImGui.BeginChild("list"))
                {
                    // only the visible rows ask for frames: a body never scrolled to is never decoded
                    var draw = ImGui.GetWindowDrawList();
                    ImGuiListClipper clipper = new();
                    clipper.Begin(_bodies.Count, Widgets.ThumbRowHeight(_thumb));

                    while (clipper.Step())
                    {
                        for (int r = clipper.DisplayStart; r < clipper.DisplayEnd; r++)
                        {
                            int id = _bodies[r];
                            ref readonly var body = ref loader.GetBody((ushort)id);
                            string alias = body.ActionCount == 0 && body.AliasBody != 0xFFFF ? $"drawn as 0x{body.AliasBody:X}" : $"{body.ActionCount} actions";

                            if (Widgets.ThumbRow(id, id == _body, _thumb, $"0x{id:X4}  {body.Type}", alias, out Vector2 thumbMin))
                            {
                                SelectBody(loader, id);
                            }

                            DrawThumbnail(draw, loader, id, thumbMin, thumbMin + new Vector2(_thumb, _thumb));
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
                if (_body >= 0)
                {
                    DrawView(loader);
                }
                else
                {
                    ImGui.TextDisabled("Select a body.");
                }
            }

            ImGui.EndChild();
        }

        private void Rebuild(AnimationsLoader loader)
        {
            _dirty = false;
            _bodies.Clear();
            string filter = _filter.Trim();
            bool byId = Widgets.TryParseId(filter, out int filterId);

            for (int id = 0; id < loader.BodyCount; id++)
            {
                ref readonly var body = ref loader.GetBody((ushort)id);

                if (body.ActionCount == 0 && body.AliasBody == 0xFFFF)
                {
                    continue;
                }

                if (filter.Length > 0 && !(byId && id == filterId) && !body.Type.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _bodies.Add(id);
            }
        }

        /// <summary>Frame 0 of the body's first action, facing the view's direction.</summary>
        private void DrawThumbnail(ImDrawListPtr draw, AnimationsLoader loader, int id, Vector2 min, Vector2 max)
        {
            int action = ThumbAction(loader, id);

            if (action < 0)
            {
                return;
            }

            byte dir = (byte)_direction;
            bool mirror = false;
            Context.Assets.Animations.GetAnimDirection(ref dir, ref mirror);
            Span<SpriteInfo> frames = Context.Assets.Animations.GetAnimationFrames((ushort)id, (byte)action, dir, out _, out _);

            if (frames.IsEmpty)
            {
                return;
            }

            if (frames[0].Texture == null)
            {
                Widgets.Pending(draw, min, max);

                return;
            }

            Widgets.DrawSprite(draw, Context.Renderer, frames[0], min + new Vector2(2, 2), max - new Vector2(2, 2), 2f, mirror);
        }

        private int ThumbAction(AnimationsLoader loader, int id)
        {
            if (_thumbActions == null || _thumbActions.Length != loader.BodyCount)
            {
                _thumbActions = new short[loader.BodyCount];
                Array.Fill(_thumbActions, (short)-2);
            }

            if (_thumbActions[id] == -2)
            {
                // an alias body (Body.def) has no actions of its own: GetAnimationFrames draws the alias, so look there
                ref readonly var body = ref loader.GetBody((ushort)id);
                ushort source = body.ActionCount == 0 && body.AliasBody != 0xFFFF ? body.AliasBody : (ushort)id;
                _thumbActions[id] = -1;

                for (int a = 0; a < AnimationsLoader.MAX_ACTIONS; a++)
                {
                    if (loader.HasAction(source, a))
                    {
                        _thumbActions[id] = (short)a;

                        break;
                    }
                }
            }

            return _thumbActions[id];
        }

        public override void SelectById(int id)
        {
            if (Context.Assets?.Animations != null && id < Context.Assets.Files.Animations.BodyCount)
            {
                SelectBody(Context.Assets.Files.Animations, id);
            }
        }

        private void SelectBody(AnimationsLoader loader, int id)
        {
            _body = id;
            _frame = 0;

            if (!loader.HasAction((ushort)id, _action))
            {
                _action = 0;

                for (int a = 0; a < AnimationsLoader.MAX_ACTIONS; a++)
                {
                    if (loader.HasAction((ushort)id, a))
                    {
                        _action = a;

                        break;
                    }
                }
            }
        }

        private void DrawView(AnimationsLoader loader)
        {
            ushort body = (ushort)_body;
            ref readonly var entry = ref loader.GetBody(body);
            Widgets.CopyableId("Body", _body);
            ImGui.SameLine();
            ImGui.TextColored(Widgets.Dim, $"{entry.Type}, {entry.ActionCount} actions, flags 0x{entry.Flags:X}");

            if (entry.AliasBody != 0xFFFF)
            {
                ImGui.TextColored(Widgets.Dim, $"Drawn as body 0x{entry.AliasBody:X4} with hue {entry.AliasHue} (Body.def)");
            }

            ImGui.SetNextItemWidth(200f);

            if (ImGui.BeginCombo("action", $"{_action}"))
            {
                for (int a = 0; a < AnimationsLoader.MAX_ACTIONS; a++)
                {
                    if (loader.HasAction(body, a) && ImGui.Selectable($"{a}", a == _action))
                    {
                        _action = a;
                        _frame = 0;
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();

            for (int d = 0; d < 8; d++)
            {
                if (d > 0)
                {
                    ImGui.SameLine();
                }

                if (ImGui.RadioButton(_directions[d], _direction == d))
                {
                    _direction = d;
                    _frame = 0;
                }
            }

            // stored directions are 0..4; 5..7 draw mirrored (the client's GetAnimDirection)
            byte dir = (byte)_direction;
            bool mirror = false;
            Context.Assets.Animations.GetAnimDirection(ref dir, ref mirror);
            Span<SpriteInfo> frames = Context.Assets.Animations.GetAnimationFrames(body, (byte)_action, dir, out _, out _);

            if (ImGui.Button(_playing ? "Pause" : "Play"))
            {
                _playing = !_playing;
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f);
            ImGui.SliderFloat("fps", ref _fps, 1f, 30f, "%.0f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f);
            ImGui.SliderFloat("zoom", ref _zoom, 1f, 6f, "%.0fx");
            ImGui.SameLine();
            ImGui.Checkbox("centre", ref _showCenter);

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
            var size = new Vector2(ImGui.GetContentRegionAvail().X, MathF.Max(200f, ImGui.GetContentRegionAvail().Y - 90f));
            ImGui.Dummy(size);
            Widgets.Checker(draw, min, min + size);

            ref SpriteInfo frame = ref frames[_frame];
            // the frame's anchor is the mobile's feet: a point a little below the middle of the view
            var anchor = new Vector2(MathF.Floor(min.X + size.X * 0.5f), MathF.Floor(min.Y + size.Y * 0.75f));

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

            // a strip of every frame of this direction
            if (ImGui.BeginChild("strip", new Vector2(0, 80f), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
            {
                var stripDraw = ImGui.GetWindowDrawList();

                for (int i = 0; i < frames.Length; i++)
                {
                    if (i > 0)
                    {
                        ImGui.SameLine();
                    }

                    Vector2 p = ImGui.GetCursorScreenPos();

                    if (ImGui.InvisibleButton($"f{i}", new Vector2(64f, 64f)))
                    {
                        _frame = i;
                        _playing = false;
                    }

                    stripDraw.AddRectFilled(p, p + new Vector2(64f, 64f), i == _frame ? Widgets.Col(0.26f, 0.42f, 0.66f, 0.55f) : Widgets.Col(0.12f, 0.12f, 0.13f));
                    Widgets.DrawSprite(stripDraw, Context.Renderer, frames[i], p, p + new Vector2(64f, 64f), 1f, mirror);
                }
            }

            ImGui.EndChild();
        }
    }
}
