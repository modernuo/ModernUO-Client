// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Input;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;

namespace ClassicUO.Game.Managers
{
    internal class TextRenderer : TextObject
    {
        private readonly List<Rectangle> _bounds = new List<Rectangle>();

        public TextRenderer(World world) : base (world)
        {
            FirstNode = this;
        }

        protected TextObject FirstNode;
        protected TextObject DrawPointer;

        // the last node reached from FirstNode through DRight, valid while no link changed since it was
        // taken (TextObject.LinkVersion)
        private TextObject _tail;
        private uint _tailVersion;
        private bool _tailValid;

        // the earliest Time of a linked text after the last prune (nothing is expired while Time.Ticks <=
        // it); 0 = walk at the next Update (a text was added)
        private long _pruneAt;

        public override void Destroy()
        {
            //Clear();
        }

        public virtual void Update()
        {
            PruneExpired();

            ProcessWorldText(false);
        }

        // unlink the texts the draw pass would skip for being expired. Time never moves after a text is added
        // (the Time.Ticks it expires at is set before AddMessage), so an expired one stays expired; the live ones keep their order.
        private void PruneExpired()
        {
            long now = ClassicUO.Time.Ticks;

            if (now <= _pruneAt)
            {
                return;
            }

            long earliest = long.MaxValue;
            TextObject t = FirstNode.DRight;

            while (t != null)
            {
                TextObject next = t.DRight;

                if (t.Time < now)
                {
                    t.UnlinkD();
                }
                else if (t.Time < earliest)
                {
                    earliest = t.Time;
                }

                t = next;
            }

            _pruneAt = earliest;
        }

        /// <summary>The last node of the list; the cached one while no link changed since it was taken.</summary>
        internal TextObject Tail
        {
            get
            {
                if (!_tailValid || _tailVersion != LinkVersion)
                {
                    _tail = WalkToTail();
                    _tailVersion = LinkVersion;
                    _tailValid = true;
                }

                return _tail;
            }
        }

        private TextObject WalkToTail()
        {
            TextObject t = FirstNode;

            while (t.DRight != null)
            {
                t = t.DRight;
            }

            return t;
        }

        public virtual void Draw(UltimaBatcher2D batcher, int startX, int startY, float layerDepth, bool isGump = false)
        {
            ProcessWorldText(false);

            int mouseX = Mouse.Position.X;
            int mouseY = Mouse.Position.Y;
            bool trackVisual = Loop.VisualDue.Tracking;

            for (TextObject o = DrawPointer; o != null; o = o.DLeft)
            {
#if CUO_DEVTOOLS
                if (FrameStats.On)
                {
                    CountNode(o);
                }
#endif
                if (o.IsDestroyed || o.RenderedText == null || o.RenderedText.IsDestroyed || !o.RenderedText.HasContent || o.Time < ClassicUO.Time.Ticks)
                {
                    continue;
                }

                if (trackVisual)
                {
                    NoteVisualDue(o);
                }

                ushort hue = 0;

                float alpha = o.Alpha / 255f;

                if (o.IsTransparent)
                {
                    if (o.Alpha == 0xFF)
                    {
                        alpha = 0x7F / 255f;
                    }
                }

                int x = o.RealScreenPosition.X;
                int y = o.RealScreenPosition.Y;

                if (o.RenderedText.PixelCheck(mouseX - x, mouseY - y))
                {
                    SelectedObject.Object = o;
                }

                if (!isGump)
                {
                    if (o.Owner is Entity && SelectedObject.Object == o)
                    {
                        hue = 0x0035;
                    }
                }
                else
                {
                    x += startX;
                    y += startY;
                }

#if CUO_DEVTOOLS
                FrameStats.Inc(FrameCounter.TextDrawn);
#endif
                o.RenderedText.Draw
                (
                    batcher,
                    x,
                    y,
                    layerDepth,
                    alpha,
                    hue
                );
            }
        }

#if CUO_DEVTOOLS
        // text-walk: classifies a node of the list as Draw reaches it (same test as the skip above)
        private static void CountNode(TextObject o)
        {
            FrameStats.Inc(FrameCounter.TextNodes);

            if (o.IsDestroyed || o.RenderedText == null || o.RenderedText.IsDestroyed || !o.RenderedText.HasContent)
            {
                FrameStats.Inc(FrameCounter.TextNoContent);
            }
            else if (o.Time < ClassicUO.Time.Ticks)
            {
                FrameStats.Inc(FrameCounter.TextExpired);
                FrameStats.Inc(o.Owner is Mobile ? FrameCounter.TextExpMobile : o.Owner is Item ? FrameCounter.TextExpItem : FrameCounter.TextExpOther);
            }
            else
            {
                FrameStats.Inc(FrameCounter.TextLive);
            }
        }
#endif

        public void MoveToTop(TextObject obj)
        {
            if (obj == null)
            {
                return;
            }

            obj.UnlinkD();
            LinkVersion++;
            _pruneAt = 0;

            TextObject next = FirstNode.DRight;
            FirstNode.DRight = obj;
            obj.DLeft = FirstNode;
            obj.DRight = next;

            if (next != null)
            {
                next.DLeft = obj;
            }
        }

        public void ProcessWorldText(bool doit)
        {
            if (doit)
            {
                if (_bounds.Count != 0)
                {
                    _bounds.Clear();
                }
            }
            else
            {
                DrawPointer = Tail;
                return;
            }

            for (DrawPointer = FirstNode; DrawPointer != null; DrawPointer = DrawPointer.DRight)
            {
                if (doit)
                {
                    TextObject t = DrawPointer;

                    if (t.Time >= ClassicUO.Time.Ticks && t.RenderedText != null && !t.RenderedText.IsDestroyed)
                    {
                        if (t.Owner != null)
                        {
                            t.IsTransparent = Collides(t);
                            CalculateAlpha(t);
                        }
                    }
                }

#if CUO_DEVTOOLS
                if (FrameStats.On)
                {
                    FrameStats.Inc(FrameCounter.TextFwdNodes);
                }
#endif
                if (DrawPointer.DRight == null)
                {
                    break;
                }
            }

            if (doit)
            {
                // the full walk just reached the real tail: refresh the kept one from it
                _tail = DrawPointer;
                _tailVersion = LinkVersion;
                _tailValid = true;
            }
        }

        // visual-due tracking (evloop-4): a drawn text disappears once Time.Ticks passes its Time and, with text_fading,
        // fades every frame over its last second (CalculateAlpha)
        private static void NoteVisualDue(TextObject o)
        {
            if (ProfileManager.CurrentProfile is { TextFading: true })
            {
                long fadeFrom = (long)o.Time - 1001;

                if (fadeFrom < ClassicUO.Time.Ticks)
                {
                    Loop.VisualDue.Now(Loop.VisualSource.WorldText);

                    return;
                }

                Loop.VisualDue.At(fadeFrom, Loop.VisualSource.WorldText);

                return;
            }

            Loop.VisualDue.At(o.Time, Loop.VisualSource.WorldText);
        }

        private void CalculateAlpha(TextObject msg)
        {
            if (ProfileManager.CurrentProfile != null && ProfileManager.CurrentProfile.TextFading)
            {
                int delta = (int) (msg.Time - ClassicUO.Time.Ticks);

                if (delta >= 0 && delta <= 1000)
                {
                    delta /= 10;

                    if (delta > 100)
                    {
                        delta = 100;
                    }
                    else if (delta < 1)
                    {
                        delta = 0;
                    }

                    delta = 255 * delta / 100;

                    if (!msg.IsTransparent || delta <= 0x7F)
                    {
                        msg.Alpha = (byte) delta;
                    }

                    msg.IsTransparent = true;
                }
            }
        }

        private bool Collides(TextObject msg)
        {
            bool result = false;

            Rectangle rect = new Rectangle
            {
                X = msg.RealScreenPosition.X,
                Y = msg.RealScreenPosition.Y,
                Width = msg.RenderedText.Width,
                Height = msg.RenderedText.Height
            };

            for (int i = 0; i < _bounds.Count; i++)
            {
                if (_bounds[i].Intersects(rect))
                {
                    result = true;

                    break;
                }
            }

            _bounds.Add(rect);

            return result;
        }

        public void AddMessage(TextObject obj)
        {
            if (obj == null)
            {
                return;
            }

            obj.UnlinkD();
            LinkVersion++;
            _pruneAt = 0;

            TextObject item = FirstNode;

            if (item != null)
            {
                if (item.DRight != null)
                {
                    TextObject next = item.DRight;

                    item.DRight = obj;
                    obj.DLeft = item;
                    obj.DRight = next;
                    next.DLeft = obj;
                }
                else
                {
                    item.DRight = obj;
                    obj.DLeft = item;
                    obj.DRight = null;
                }
            }
        }


        public new virtual void Clear()
        {
            if (FirstNode != null)
            {
                TextObject first = FirstNode;

                while (first?.DLeft != null)
                {
                    first = first.DLeft;
                }

                while (first != null)
                {
                    TextObject next = first.DRight;

                    first.Destroy();

                    first = next;
                }
            }

            if (DrawPointer != null)
            {
                TextObject first = DrawPointer;

                while (first?.DLeft != null)
                {
                    first = first.DLeft;
                }

                while (first != null)
                {
                    TextObject next = first.DRight;

                    first.Destroy();

                    first = next;
                }
            }

            FirstNode = this;
            FirstNode.DLeft = null;
            FirstNode.DRight = null;
            DrawPointer = null;
            LinkVersion++;
            _pruneAt = 0;
        }
    }
}
