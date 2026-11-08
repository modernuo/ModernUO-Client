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
            AnimIndex = 0;


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

        public bool IsMoving => (UsesEndpoints && !EcsEntity.IsNull ? EffectSystem.HasTarget(this) : Target != null) || TargetX != 0 && TargetY != 0;

        public bool CanCreateExplosionEffect;
        public ushort AnimationGraphic = 0xFFFF;
        public AnimDataFrame AnimDataFrame;
        public byte AnimIndex;
        public float AngleToTarget;
        public GraphicEffectBlendMode Blend;
        public long Duration = -1;
        public uint IntervalInMs;
        public bool IsEnabled;
        public long NextChangeFrameTime;

        /// <summary>T8: this effect's entity, created when it joins the manager's list (default before that and once the effect
        /// is destroyed).</summary>
        internal Friflo.Engine.ECS.Entity EcsEntity;

        /// <summary>T8 ownership (ecs_effect_endpoints): source and target are the state's references, read from the ECS; the
        /// object references below are kept only for the off path and the checker (CUO_EFFECT_ENDPOINT_CHECK).</summary>
        internal bool UsesEndpoints;

        /// <summary>CUO_EFFECT_ENDPOINT_CHECK when the effect registered: the objects are kept and every read is compared.</summary>
        internal bool EndpointCheck;

        /// <summary>T8 ownership phase 2 (ecs_effect_motion, decided when the effect registers): the kind's motion is
        /// <see cref="EffectSystem"/>'s motion step over the state; position, offset, angle and target here are written from
        /// it for the views.</summary>
        internal bool UsesMotion;

        /// <summary>CUO_EFFECT_MOTION_CHECK when the effect registered: the legacy motion is replayed beside every step.</summary>
        internal bool MotionCheck;

        internal EffectManager Manager => _manager;

        /// <summary>The motion step's <c>IsPositionChanged = true</c> (MovingEffect.UpdateOffset sets it every update).</summary>
        internal void MarkPositionChanged() => IsPositionChanged = true;

        public GameObject Source;
        internal GameObject Target;
        internal ushort TargetX;
        internal ushort TargetY;
        internal sbyte TargetZ;

        /// <summary>The source object for the views (depth, light) and the client-side target cursor.</summary>
        internal GameObject SourceObject => UsesEndpoints && !EcsEntity.IsNull ? EffectSystem.SourceObjectOf(this) : Source;


        public override void Update()
        {
            base.Update();


            if (UsesEndpoints && !EcsEntity.IsNull ? EffectSystem.SourceGone(this) : Source != null && Source.IsDestroyed)
            {
                Destroy();

                return;
            }

            if (IsDestroyed)
            {
                return;
            }

            EffectSystem.Animate(this);
        }

        protected (ushort x, ushort y, sbyte z) GetSource()
        {
            return UsesEndpoints && !EcsEntity.IsNull ? EffectSystem.SourceOf(this) : LegacySource();
        }

        internal (ushort x, ushort y, sbyte z) LegacySource()
        {
            return Source == null ? (X, Y, Z) : (Source.X, Source.Y, Source.Z);
        }

        /// <summary>FixedEffect: the source's offset, when the source is an object (else the effect keeps its own).</summary>
        protected bool TryGetSourceOffset(out Microsoft.Xna.Framework.Vector3 offset)
        {
            if (UsesEndpoints && !EcsEntity.IsNull)
            {
                return EffectSystem.SourceOffsetOf(this, out offset);
            }

            offset = Source?.Offset ?? default;

            return Source != null;
        }

        /// <summary>MovingEffect: the target object was destroyed (its last coordinates stay the target).</summary>
        protected bool TargetGone()
        {
            return UsesEndpoints && !EcsEntity.IsNull ? EffectSystem.TargetGone(this) : Target != null && Target.IsDestroyed;
        }

        protected void CreateExplosionEffect()
        {
            if (CanCreateExplosionEffect)
            {
                (var targetX, var targetY, var targetZ) = GetTarget();
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

        /// <summary>T8: copies position, offset, angle, target and source/target serials to <paramref name="s"/>.</summary>
        internal void Publish(ref EffectState s)
        {
            (ushort tx, ushort ty, sbyte tz) = GetTarget();
            s.X = X;
            s.Y = Y;
            s.Z = Z;
            s.TargetX = tx;
            s.TargetY = ty;
            s.TargetZ = tz;
            s.OffsetX = OffsetX;
            s.OffsetY = OffsetY;
            s.OffsetZ = Offset.Z;
            s.Angle = AngleToTarget;

            // with endpoints the serials are the references themselves (the re-key moves them)
            if (!UsesEndpoints)
            {
                s.SourceSerial = (Source as Entity)?.Serial ?? 0;
                s.TargetSerial = (Target as Entity)?.Serial ?? 0;
            }

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

            if (UsesEndpoints && !EcsEntity.IsNull)
            {
                EffectSystem.DropSource(this);
            }

            SetInWorldTile(x, y,z);
        }

        protected (ushort x, ushort y, sbyte z) GetTarget()
        {
            return UsesEndpoints && !EcsEntity.IsNull ? EffectSystem.TargetOf(this) : LegacyTarget();
        }

        internal (ushort x, ushort y, sbyte z) LegacyTarget()
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

            if (UsesEndpoints && !EcsEntity.IsNull)
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

            AnimIndex = 0;
            Source = null;
            Target = null;
            base.Destroy();
        }
    }
}
