// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Assets;
using ClassicUO.Utility;

namespace ClassicUO.Game.GameObjects
{
    internal abstract partial class GameEffect : GameObject
    {
        private readonly EffectManager _manager;

        /// <summary>Links in <see cref="EffectManager"/>'s list.</summary>
        public GameEffect Next, Previous;

        protected GameEffect(World world, EffectManager manager, ushort graphic, ushort hue, int duration, byte speed) : base(world)
        {
            _manager = manager;

            Graphic = graphic;
            Hue = hue;
            AllowedToDraw = CanBeDrawn(world, graphic);
            AlphaHue = 0xFF;
            AnimDataFrame = Client.Game?.UO?.FileManager?.AnimData?.CalculateCurrentGraphic(graphic) ?? default;
            IsEnabled = true;


            speed *= 10;

            if (speed == 0)
            {
                speed = Constants.ITEM_EFFECT_ANIMATION_DELAY;
            }

            if (AnimDataFrame.FrameInterval == 0)
            {
                IntervalInMs = speed;

                // NOTE:
                // tested on outlands with arrows & bolts
                // server sends duration = 50 , a very small amount of time so the arrow will be destroyed suddenly
                // im not sure if this is the right fix, but keep it atm

                // NOTE 2:
                // this fix causes issue with other effects. It makes perma effects. So bad
                //duration = -1;
            }
            else
            {
                IntervalInMs = (uint)(AnimDataFrame.FrameInterval * speed);
            }

            Duration = duration > 0 ? Time.Ticks + duration : -1;
        }

        public bool IsMoving => (!EcsEntity.IsNull ? EffectSystem.HasTarget(this) : Target != null) || TargetX != 0 && TargetY != 0;

        public bool CanCreateExplosionEffect;
        public AnimDataFrame AnimDataFrame;
        public GraphicEffectBlendMode Blend;
        public long Duration = -1;
        public uint IntervalInMs;
        public bool IsEnabled;

        /// <summary>T8: this effect's entity, created when it joins the manager's list (default before that and once the effect
        /// is destroyed). From then on source and target are the state's references, read from the ECS, and the kind's motion
        /// is <see cref="EffectSystem"/>'s motion step over the state. The frame, offset and angle live only in the state (T9
        /// S7); the position here is written from it for the tile placement, the target coordinates for a gone target.</summary>
        internal Friflo.Engine.ECS.Entity EcsEntity;

        internal EffectManager Manager => _manager;

        /// <summary>The motion step's <c>IsPositionChanged = true</c> (MovingEffect.UpdateOffset sets it every update).</summary>
        internal void MarkPositionChanged() => IsPositionChanged = true;

        /// <summary>The constructor's source and target objects, until <see cref="EffectSystem.Register"/> turns them into the
        /// state's references and drops them.</summary>
        public GameObject Source;
        internal GameObject Target;
        internal ushort TargetX;
        internal ushort TargetY;
        internal sbyte TargetZ;

        /// <summary>The source object for the views (depth, light) and the client-side target cursor.</summary>
        internal GameObject SourceObject => !EcsEntity.IsNull ? EffectSystem.SourceObjectOf(this) : Source;

        /// <summary>The source object's position, the effect's own for a ground point (before the effect registers, and a
        /// moving effect's update after its source ended it).</summary>
        internal (ushort x, ushort y, sbyte z) ObjectSource()
        {
            return Source == null ? (X, Y, Z) : (Source.X, Source.Y, Source.Z);
        }

        protected void CreateExplosionEffect()
        {
            if (CanCreateExplosionEffect)
            {
                (var targetX, var targetY, var targetZ) = ObjectTarget();
                CreateExplosionAt(targetX, targetY, targetZ);
            }
        }

        /// <summary>The explosion at the target (the motion step passes the target it resolved).</summary>
        internal void CreateExplosionAt(ushort targetX, ushort targetY, sbyte targetZ)
        {
            FixedEffect effect = new FixedEffect(World, _manager, 0x36CB, Hue, 400, 0);
            effect.Blend = Blend;
            effect.SetSource(targetX, targetY, targetZ);

            _manager.Add(effect);
        }

        /// <summary>T8: copies position and target to <paramref name="s"/>, with the starting offset (a moving effect's
        /// <see cref="MovingEffect.START_OFFSET_X"/>) and no angle (Register; the source and target references and serials are
        /// bound before).</summary>
        internal void Publish(ref EffectState s)
        {
            (ushort tx, ushort ty, sbyte tz) = ObjectTarget();
            s.X = X;
            s.Y = Y;
            s.Z = Z;
            s.TargetX = tx;
            s.TargetY = ty;
            s.TargetZ = tz;
            s.OffsetX = this is MovingEffect ? MovingEffect.START_OFFSET_X : 0;
            s.OffsetY = 0;
            s.OffsetZ = 0;
            s.Angle = 0;
            s.Flags = (byte)(IsMoving ? s.Flags | EffectState.FLAG_MOVING : s.Flags & ~EffectState.FLAG_MOVING);
        }

        public void SetSource(GameObject source)
        {
            Source = source;
            SetInWorldTile(source.X, source.Y, source.Z);
        }

        public void SetSource(ushort x, ushort y, sbyte z)
        {
            Source = null;

            if (!EcsEntity.IsNull)
            {
                EffectSystem.DropSource(this);
            }

            SetInWorldTile(x, y,z);
        }

        /// <summary>The target object's position, the effect's target coordinates for a ground point (see
        /// <see cref="ObjectSource"/>).</summary>
        internal (ushort x, ushort y, sbyte z) ObjectTarget()
        {
            return Target == null ? (TargetX, TargetY, TargetZ) : (Target.X, Target.Y, Target.Z);
        }

        public void SetTarget(GameObject target)
        {
            Target = target;
        }

        public void SetTarget(ushort x, ushort y, sbyte z)
        {
            Target = null;

            if (!EcsEntity.IsNull)
            {
                EffectSystem.DropTarget(this);
            }
            TargetX = x;
            TargetY = y;
            TargetZ = z;
        }

        public override void Destroy()
        {
            _manager?.Remove(this);
            EffectSystem.Unregister(this);

            Source = null;
            Target = null;
            base.Destroy();
        }
    }
}
