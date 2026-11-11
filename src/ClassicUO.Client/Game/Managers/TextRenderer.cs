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

        // text_tail_cache: the last node reached from FirstNode through DRight, valid while no link changed since it was
        // taken (TextObject.LinkVersion)
        private TextObject _tail;
        private uint _tailVersion;
        private bool _tailValid;

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_TEXT_TAIL=1|0) and the checker
        private static readonly bool? s_env = Environment.GetEnvironmentVariable("CUO_TEXT_TAIL") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        private static readonly bool s_check = Environment.GetEnvironmentVariable("CUO_TEXT_ORDER_CHECK") == "1";

        /// <summary>Tests: the checker for every renderer (null: <c>CUO_TEXT_ORDER_CHECK=1</c>).</summary>
        internal static bool? CheckOverride;

        /// <summary>Checker counts over the session: uses compared with the full walk, differences found.</summary>
        public static long Checks, Mismatches;

        /// <summary>Tests: sees each mismatch line.</summary>
        internal static Action<string> MismatchObserver;

        private uint _nextLog;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

        public static bool TailCacheOn
        {
            get
            {
                if (OverrideForTests.HasValue)
                {
                    return OverrideForTests.Value;
                }

#if CUO_DEVTOOLS
                if (s_env.HasValue)
                {
                    return s_env.Value;
                }
#endif
                return Settings.GlobalSettings != null && Settings.GlobalSettings.TextTailCache;
            }
        }

        public override void Destroy()
        {
            //Clear();
        }

        public virtual void Update()
        {
            ProcessWorldText(false);
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

            for (TextObject o = DrawPointer; o != null; o = o.DLeft)
            {
                if (o.IsDestroyed || o.RenderedText == null || o.RenderedText.IsDestroyed || !o.RenderedText.HasContent || o.Time < ClassicUO.Time.Ticks)
                {
                    continue;
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

        public void MoveToTop(TextObject obj)
        {
            if (obj == null)
            {
                return;
            }

            obj.UnlinkD();
            LinkVersion++;

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
                bool cache = TailCacheOn;
#if CUO_DEVTOOLS
                bool check = CheckOverride ?? s_check;

                if (check)
                {
                    TextObject kept = _tailValid && _tailVersion == LinkVersion ? _tail : null;
                    TextObject walked = WalkToTail();
                    CheckOrder(kept, walked, cache);
                    _tail = walked;
                    _tailVersion = LinkVersion;
                    _tailValid = true;
                    DrawPointer = cache ? Tail : walked;
                    return;
                }
#endif
                if (cache)
                {
                    DrawPointer = Tail;
                    return;
                }
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

                if (DrawPointer.DRight == null)
                {
                    break;
                }
            }

            if (doit)
            {
                // the full walk just reached the real tail: refresh the kept one from it
#if CUO_DEVTOOLS
                if (CheckOverride ?? s_check)
                {
                    CheckOrder(_tailValid && _tailVersion == LinkVersion ? _tail : null, DrawPointer, TailCacheOn);
                }
#endif
                _tail = DrawPointer;
                _tailVersion = LinkVersion;
                _tailValid = true;
            }
        }

#if CUO_DEVTOOLS
        // CLEANUP(release): CUO_TEXT_ORDER_CHECK. kept is the tail kept from earlier uses (null: none valid, nothing to
        // compare); walked is the full walk's. The link order is checked in both directions.
        private void CheckOrder(TextObject kept, TextObject walked, bool cache)
        {
            Checks++;

            if (kept != null && !ReferenceEquals(kept, walked))
            {
                Mismatch($"kept tail differs from the walked tail (kept {Describe(kept)}, walked {Describe(walked)})");
            }

            int forward = 0;
            TextObject last = FirstNode;

            for (TextObject t = FirstNode; t != null; t = t.DRight)
            {
                forward++;
                last = t;

                if (t.DRight != null && !ReferenceEquals(t.DRight.DLeft, t))
                {
                    Mismatch($"link order: {Describe(t.DRight)}.DLeft is not its DRight owner");
                }

                if (forward > 1_000_000)
                {
                    Mismatch("link order: forward walk does not end (cycle)");

                    return;
                }
            }

            if (!ReferenceEquals(last, walked))
            {
                Mismatch("walked tail is not the forward walk's last node");
            }

            int backward = 0;

            for (TextObject t = walked; t != null && backward <= forward; t = t.DLeft)
            {
                backward++;
            }

            if (backward != forward)
            {
                Mismatch($"link order: {forward} nodes forward from the head, {backward} backward from the tail");
            }

            uint now = (uint)ClassicUO.Time.Ticks;

            if (now >= _nextLog)
            {
                _nextLog = now + 5000;
                Log.Info($"[text-order-check] text_tail_cache={(cache ? "on" : "off")}, checks {Checks}, mismatches {Mismatches}");
            }
        }

        private static string Describe(TextObject o)
        {
            return o == null ? "null" : $"#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o)}";
        }

        private static void Mismatch(string what)
        {
            Mismatches++;
            MismatchObserver?.Invoke(what);

            if (Mismatches <= 20)
            {
                Log.Warn($"[text-order-check] mismatch: {what}");
            }
        }
#endif

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
        }
    }
}
