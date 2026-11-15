// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.GameObjects
{
    internal class TextObject : BaseGameObject
    {
        //private static readonly QueuedPool<TextObject> _queue = new QueuedPool<TextObject>
        //(
        //    1000,
        //    o =>
        //    {
        //        o.IsDestroyed = false;
        //        o.Alpha = 0xFF;
        //        o.Hue = 0;
        //        o.Time = 0;
        //        o.IsTransparent = false;
        //        o.SecondTime = 0;
        //        o.Type = 0;
        //        o.X = 0;
        //        o.Y = 0;
        //        o.RealScreenPosition = Point.Zero;
        //        o.OffsetY = 0;
        //        o.Owner = null;
        //        o.UnlinkD();
        //        o.IsTextGump = false;
        //        o.RenderedText?.Destroy();
        //        o.RenderedText = null;
        //        o.Clear();
        //    }
        //);

        public TextObject(World world) : base(world) { }

        public byte Alpha;
        public TextObject DLeft, DRight;

        /// <summary>Counts changes to any DLeft/DRight link (text_tail_cache: a renderer's kept tail is valid while it is
        /// unchanged). Every writer of those links bumps it; all of them are on the main thread.</summary>
        internal static uint LinkVersion;

        /// <summary>Links in the owner's <see cref="TextContainer"/>.</summary>
        public TextObject Next, Previous;
        public ushort Hue;
        public bool IsDestroyed;
        public bool IsTextGump;
        public bool IsTransparent;
        public GameObject Owner;

        /// <summary>item_pool: the owner's <see cref="GameObject.Incarnation"/> when it got this text. A container item's text
        /// lives in its gump until it expires, past the item's Destroy; a click on it must not reach the item reused since.</summary>
        public uint OwnerIncarnation;

        /// <summary>The owner, unless it was reused for another object since (item_pool; with the switch off an entity's
        /// Incarnation never changes, so this is the owner as before).</summary>
        public Entity LiveOwnerEntity => Owner is Entity e && e.Incarnation == OwnerIncarnation ? e : null;

        public RenderedText RenderedText;
        public long Time, SecondTime;
        public MessageType Type;
        public int X, Y, OffsetY;


        public static TextObject Create(World world)
        {
            return new TextObject(world) { Alpha = 0xFF }; // _queue.GetOne();
        }


        public virtual void Destroy()
        {
            if (IsDestroyed)
            {
                return;
            }

            UnlinkD();

            RealScreenPosition = Point.Zero;
            IsDestroyed = true;
            RenderedText?.Destroy();
            RenderedText = null;
            Owner = null;

            //_queue.ReturnOne(this);
        }

        public void UnlinkD()
        {
            if (DRight != null || DLeft != null)
            {
                LinkVersion++;
            }

            if (DRight != null)
            {
                DRight.DLeft = DLeft;
            }

            if (DLeft != null)
            {
                DLeft.DRight = DRight;
            }

            DRight = null;
            DLeft = null;
        }

        public void ToTopD()
        {
            TextObject obj = this;

            while (obj != null)
            {
                if (obj.DLeft == null)
                {
                    break;
                }

                obj = obj.DLeft;
            }

            TextRenderer next = (TextRenderer) obj;
            next.MoveToTop(this);
        }
    }
}