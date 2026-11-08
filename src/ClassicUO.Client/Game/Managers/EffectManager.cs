// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Managers
{
    // Not a LinkedObject: the list links live on GameEffect, so world objects carry no list fields (owner item 5, 2026-11-07).
    internal sealed class EffectManager
    {
        private readonly World _world;

        /// <summary>The oldest effect; newer ones follow through <see cref="GameEffect.Next"/>.</summary>
        public GameEffect Items;

        // the newest effect, so an add is O(1) instead of a walk of the whole list (bursts were O(n²))
        private GameEffect _last;

        /// <summary>The newest effect; older ones precede it through <see cref="GameEffect.Previous"/>.</summary>
        public GameEffect Last => _last;

        public bool IsEmpty => Items == null;

        public EffectManager(World world)
        {
            _world = world;
        }

        // ecs_effect_motion: the player's position for this pass (MovingEffect's path is relative to it), resolved at the
        // first moving step of the pass
        private bool _playerResolved;
        private int _playerX, _playerY, _playerZ;

        /// <summary>The player's position from its mirror record (the object's without one), once per pass.</summary>
        internal (int x, int y, int z) MotionPlayer()
        {
            if (!_playerResolved)
            {
                (_playerX, _playerY, _playerZ) = EffectSystem.PlayerPosition(_world);
                _playerResolved = true;
            }

            return (_playerX, _playerY, _playerZ);
        }

        public void Update()
        {
            bool system = _world.EffectsSystemOn;
            _playerResolved = false;

            for (GameEffect f = Items; f != null;)
            {
                GameEffect next = f.Next;

                f.Update();

                // with ecs_effect_motion the step wrote the state itself (and resolved the endpoints once)
                if (system && !f.IsDestroyed && !f.UsesMotion)
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

            if (system && _world.EffectEndpointsCheckOn)
            {
                EffectSystem.CheckEndpoints(this);
            }

            if (system && _world.EffectMotionCheckOn)
            {
                EffectSystem.LogMotionCheck();
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

        public void Clear()
        {
            GameEffect first = Items;

            while (first != null)
            {
                GameEffect n = first.Next;

                first.Destroy();

                first = n;
            }

            Items = null;
            _last = null;
        }

        private void PushToBack(GameEffect item)
        {
            if (item == null)
            {
                return;
            }

            Remove(item);

            if (Items == null)
            {
                Items = item;
            }
            else
            {
                _last.Next = item;
                item.Previous = _last;
            }

            _last = item;
        }

        /// <summary>Unlinks <paramref name="item"/> (GameEffect.Destroy); an effect that is not linked is left as it is.</summary>
        public void Remove(GameEffect item)
        {
            if (item == null)
            {
                return;
            }

            if (item == _last)
            {
                _last = item.Previous;
            }

            if (item == Items)
            {
                Items = Items.Next;

                if (Items != null)
                {
                    Items.Previous = null;
                }
            }
            else
            {
                if (item.Previous != null)
                {
                    item.Previous.Next = item.Next;
                }

                if (item.Next != null)
                {
                    item.Next.Previous = item.Previous;
                }
            }

            item.Next = null;
            item.Previous = null;
        }
    }
}