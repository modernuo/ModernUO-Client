// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using static ClassicUO.Network.NetClient;

namespace ClassicUO.Game.GameObjects
{
    enum HitsRequestStatus
    {
        None,
        Pending,
        Received
    }

    internal abstract class Entity : GameObject, IEquatable<Entity>
    {
        private static readonly RenderedText[] _hitsPercText = new RenderedText[101];
        private Direction _direction;


        protected Entity(World world, uint serial) : base(world)
        {
            Serial = serial;
        }

        private byte _animIndex;
        private bool _executeAnimation = true;
        private long _lastAnimationChangeTime;
        private Flags _flags;
        public byte AnimIndex { get => _animIndex; set { FunnelCheck.Write(this, FunnelField.AnimIndex); _animIndex = value; } }
        public bool ExecuteAnimation { get => _executeAnimation; set { FunnelCheck.Write(this, FunnelField.ExecuteAnimation); _executeAnimation = value; } }
        internal long LastAnimationChangeTime { get => _lastAnimationChangeTime; set { FunnelCheck.Write(this, FunnelField.LastAnimationChangeTime); _lastAnimationChangeTime = value; } }
        public Flags Flags { get => _flags; set { FunnelCheck.Write(this, FunnelField.Flags); _flags = value; } }
        private ushort _hits, _hitsMax;
        public byte HitsPercentage;

        // W5: the percentage (and its cached text) is recomputed when Hits or HitsMax change, not every frame
        public ushort Hits
        {
            get => _hits;
            set
            {
                FunnelCheck.Write(this, FunnelField.Hits);
                _hits = value;
                RefreshHitsPercentage();
            }
        }

        public ushort HitsMax
        {
            get => _hitsMax;
            set
            {
                FunnelCheck.Write(this, FunnelField.HitsMax);
                _hitsMax = value;
                RefreshHitsPercentage();
            }
        }
        public bool IsClicked;
        private uint _lastStepTime, _serial;
        private string _name;
        public uint LastStepTime { get => _lastStepTime; set { FunnelCheck.Write(this, FunnelField.LastStepTime); _lastStepTime = value; } }
        public string Name { get => _name; set { FunnelCheck.Write(this, FunnelField.Name); _name = value; } }
        public uint Serial { get => _serial; set { FunnelCheck.WriteSerial(this, value); _serial = value; } }
        public HitsRequestStatus HitsRequest;


        public bool IsHidden => (Flags & Flags.Hidden) != 0;

        public Direction Direction
        {
            get => _direction;
            set
            {
                FunnelCheck.Write(this, FunnelField.Direction);

                if (_direction != value)
                {
                    _direction = value;
                    OnDirectionChanged();
                }

                World.Ecs.Sync(this);
            }
        }

        public bool Exists => World.Contains(Serial);

        public RenderedText HitsTexture => _hitsPercText[HitsPercentage % _hitsPercText.Length];


        public bool Equals(Entity e)
        {
            return e != null && Serial == e.Serial;
        }

        public void FixHue(ushort hue)
        {
            Hue = FixedHue(hue);
        }

        /// <summary>T1: the hue <see cref="FixHue"/> stores for a packet hue (pure; the funnel writes the result itself).</summary>
        internal static ushort FixedHue(ushort hue)
        {
            ushort fixedColor = (ushort) (hue & 0x3FFF);

            if (fixedColor != 0)
            {
                if (fixedColor >= 0x0BB8)
                {
                    fixedColor = 1;
                }

                fixedColor |= (ushort) (hue & 0xC000);
            }
            else
            {
                fixedColor = (ushort) (hue & 0x8000);
            }

            return fixedColor;
        }

        public void UpdateHits(byte perc)
        {
            if (perc != HitsPercentage)
            {
                HitsPercentage = perc;

                ref var rtext = ref _hitsPercText[perc % _hitsPercText.Length];

                if (rtext == null || rtext.IsDestroyed)
                {
                    ushort color = 0x0044;

                    if (perc < 30)
                    {
                        color = 0x0021;
                    }
                    else if (perc < 50)
                    {
                        color = 0x0030;
                    }
                    else if (perc < 80)
                    {
                        color = 0x0058;
                    }

                    rtext = RenderedText.Create($"[{perc}%]", color, 3, false);
                }
            }
        }

        public virtual void CheckGraphicChange(byte animIndex = 0)
        {
        }

        public override void Update()
        {
            base.Update();

            if (ObjectHandlesStatus == ObjectHandlesStatus.OPEN)
            {
                ObjectHandlesStatus = ObjectHandlesStatus.DISPLAYING;

                // TODO: Some servers may not want to receive this (causing original client to not send it),
                //but all servers tested (latest POL, old POL, ServUO, Outlands) do.
                if ( /*Client.Game.UO.Version > ClientVersion.CV_200 &&*/ SerialHelper.IsMobile(Serial))
                {
                    Socket.Send_NameRequest(Serial);
                }

                UIManager.Add(new NameOverheadGump(World, this));
            }


            // kept for world_update_active_items off (the old per-frame loop); idempotent with the setters
            if (_hitsMax > 0 && !Configuration.Settings.GlobalSettings.WorldUpdateActiveItems)
            {
                RefreshHitsPercentage();
            }
        }

        /// <summary>The per-frame rule Entity.Update applied: for HitsMax &gt; 0, the clamped percentage of Hits.</summary>
        public static byte HitsPercent(ushort hits, ushort hitsMax)
        {
            int perc = MathHelper.PercetangeOf(hits, hitsMax);

            return (byte)(perc > 100 ? 100 : perc < 0 ? 0 : perc);
        }

        private void RefreshHitsPercentage()
        {
            if (_hitsMax > 0)
            {
                UpdateHits(HitsPercent(_hits, _hitsMax));
            }
        }

        public override void Destroy()
        {
            base.Destroy();

            GameActions.SendCloseStatus(World, Serial, HitsRequest >= HitsRequestStatus.Pending);

            AnimIndex = 0;
            LastAnimationChangeTime = 0;
            World.Ecs.OnDestroyed(this);

            if (this is Item item)
            {
                World.OnItemDestroyed(item);
            }
            else if (this is Mobile mobile)
            {
                World.OnMobileDestroyed(mobile);
            }
        }

        public Item FindItem(ushort graphic, ushort hue = 0xFFFF)
        {
            Item item = null;

            if (hue == 0xFFFF)
            {
                int minColor = 0xFFFF;

                // T2: the children from the ECS with ecs_read_contents on (WorldState.Children)
                foreach (Item it in World.State.Children(this))
                {

                    if (it.Graphic == graphic)
                    {
                        if (it.Hue < minColor)
                        {
                            item = it;
                            minColor = it.Hue;
                        }
                    }

                    if (SerialHelper.IsValid(it.Container))
                    {
                        Item found = it.FindItem(graphic, hue);

                        if (found != null && found.Hue < minColor)
                        {
                            item = found;
                            minColor = found.Hue;
                        }
                    }
                }
            }
            else
            {
                // T2: the children from the ECS with ecs_read_contents on (WorldState.Children)
                foreach (Item it in World.State.Children(this))
                {

                    if (it.Graphic == graphic && it.Hue == hue)
                    {
                        item = it;
                    }

                    if (SerialHelper.IsValid(it.Container))
                    {
                        Item found = it.FindItem(graphic, hue);

                        if (found != null)
                        {
                            item = found;
                        }
                    }
                }
            }

            return item;
        }

        public Item GetItemByGraphic(ushort graphic, bool deepsearch = false)
        {
            // T2: the children from the ECS with ecs_read_contents on (WorldState.Children)
            foreach (Item item in World.State.Children(this))
            {
                if (item.Graphic == graphic)
                {
                    return item;
                }

                // (legacy: the deep search walks this entity's children again, not the item's)
                if (deepsearch && World.State.HasChildren(item))
                {
                    foreach (Item childItem in World.State.Children(this))
                    {

                        Item res = childItem.GetItemByGraphic(graphic, deepsearch);

                        if (res != null)
                        {
                            return res;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// The first item on <paramref name="layer"/> among the entity's children. T3a: for a mobile, under
        /// <c>CUO_EQUIP_READ_CHECK=1</c>, compared with the ECS's worn item (<see cref="WorldState.FindWorn"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Item FindItemByLayer(Layer layer)
        {
            if (this is Mobile mobile && World?.State is WorldState state && state.EquipCheckEnabled)
            {
                return state.FindWorn(mobile, layer);
            }

            return FindItemByLayerInList(layer);
        }

        /// <summary>The legacy read: the first non-destroyed child on <paramref name="layer"/> in the child list.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Item FindItemByLayerInList(Layer layer)
        {
            for (LinkedObject i = Items; i != null; i = i.Next)
            {
                Item it = (Item) i;

                if (!it.IsDestroyed && it.Layer == layer)
                {
                    return it;
                }
            }

            return null;
        }

        public static implicit operator uint(Entity entity)
        {
            return entity.Serial;
        }

        public static bool operator ==(Entity e, Entity s)
        {
            return Equals(e, s);
        }

        public static bool operator !=(Entity e, Entity s)
        {
            return !Equals(e, s);
        }

        public override bool Equals(object obj)
        {
            return obj is Entity ent && Equals(ent);
        }

        public override int GetHashCode()
        {
            return (int) Serial;
        }

        public abstract void ProcessAnimation(bool evalutate = false);

        public abstract ushort GetGraphicForAnimation();
    }
}