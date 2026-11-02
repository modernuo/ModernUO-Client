// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Managers
{
    internal sealed class EffectManager : LinkedObject
    {
        private readonly World _world;

        public EffectManager(World world)
        {
            _world = world;
        }

        public void Update()
        {
            bool system = _world.EffectsSystemOn;

            for (GameEffect f = (GameEffect) Items; f != null;)
            {
                GameEffect next = (GameEffect) f.Next;

                f.Update();

                if (system && !f.IsDestroyed)
                {
                    EffectSystem.Publish(f);
                }

                if (!f.IsDestroyed && f.Distance > _world.ClientViewRange)
                {
                    f.Destroy();
                }

                f = next;
            }

            if (system && _world.EffectsCheckOn)
            {
                EffectSystem.Check(_world, this);
            }
        }

        /// <summary>Appends <paramref name="effect"/> to the list; with <c>ecs_effects</c> on it gets its entity first.</summary>
        public void Add(GameEffect effect)
        {
            if (_world.EffectsSystemOn)
            {
                EffectSystem.Register(effect);
            }

            PushToBack(effect);
        }


        public void CreateEffect
        (
            GraphicEffectType type,
            uint source,
            uint target,
            ushort graphic,
            ushort hue,
            ushort srcX,
            ushort srcY,
            sbyte srcZ,
            ushort targetX,
            ushort targetY,
            sbyte targetZ,
            byte speed,
            int duration,
            bool fixedDir,
            bool doesExplode,
            bool hasparticles,
            GraphicEffectBlendMode blendmode
        )
        {
            if (hasparticles)
            {
                Log.Warn("Unhandled particles in an effects packet.");
            }

            GameEffect effect;

            if (hue != 0)
            {
                hue++;
            }

            duration *= Constants.ITEM_EFFECT_ANIMATION_DELAY;

            switch (type)
            {
                case GraphicEffectType.Moving:
                    if (graphic <= 0)
                    {
                        return;
                    }

                    // TODO: speed == 0 means run at standard frameInterval got from anim.mul?
                    if (speed == 0)
                    {
                        speed++;
                    }

                    effect = new MovingEffect
                    (
                        _world,
                        this,
                        source,
                        target,
                        srcX,
                        srcY,
                        srcZ,
                        targetX,
                        targetY,
                        targetZ,
                        graphic,
                        hue,
                        fixedDir,
                        duration,
                        speed
                    )
                    {
                        Blend = blendmode,
                        CanCreateExplosionEffect = doesExplode
                    };

                    break;

                case GraphicEffectType.DragEffect:

                    if (graphic <= 0)
                    {
                        return;
                    }

                    if (speed == 0)
                    {
                        speed++;
                    }

                    effect = new DragEffect
                    (
                        _world,
                        this,
                        source,
                        target,
                        srcX,
                        srcY,
                        srcZ,
                        targetX,
                        targetY,
                        targetZ,
                        graphic,
                        hue,
                        duration,
                        speed
                    )
                    {
                        Blend = blendmode,
                        CanCreateExplosionEffect = doesExplode
                    };

                    break;

                case GraphicEffectType.Lightning:
                    effect = new LightningEffect
                    (
                        _world,
                        this,
                        source,
                        srcX,
                        srcY,
                        srcZ,
                        hue
                    );

                    break;

                case GraphicEffectType.FixedXYZ:

                    if (graphic <= 0)
                    {
                        return;
                    }

                    effect = new FixedEffect
                    (
                        _world,
                        this,
                        srcX,
                        srcY,
                        srcZ,
                        graphic,
                        hue,
                        duration,
                        0 //speed [use 50ms]
                    )
                    {
                        Blend = blendmode
                    };

                    break;

                case GraphicEffectType.FixedFrom:

                    if (graphic <= 0)
                    {
                        return;
                    }

                    effect = new FixedEffect
                    (
                        _world,
                        this,
                        source,
                        srcX,
                        srcY,
                        srcZ,
                        graphic,
                        hue,
                        duration,
                        0 //speed [use 50ms]
                    )
                    {
                        Blend = blendmode
                    };

                    break;

                case GraphicEffectType.ScreenFade:
                    Log.Warn("Unhandled 'Screen Fade' effect.");

                    return;

                default:
                    Log.Warn("Unhandled effect.");

                    return;
            }


            Add(effect);
        }

        public new void Clear()
        {
            GameEffect first = (GameEffect) Items;

            while (first != null)
            {
                LinkedObject n = first.Next;

                first.Destroy();

                first = (GameEffect) n;
            }

            Items = null;
        }
    }
}