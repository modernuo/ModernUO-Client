using System;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using Microsoft.Xna.Framework.Graphics;
using Uo.Pack;

namespace ClassicUO.Renderer.Animations
{
    public sealed class Animations
    {
        private readonly TextureAtlas _atlas;
        private readonly PixelPicker _picker = new PixelPicker();
        private readonly AnimationsLoader _animationLoader;

        private SpriteInfo[][][][] _cache;

        public Animations(AnimationsLoader animationLoader, GraphicsDevice device)
        {
            _animationLoader = animationLoader;
            _atlas = new TextureAtlas(device, 4096, 4096, SurfaceFormat.Color);
            _cache = new SpriteInfo[animationLoader.BodyCount][][][];
        }

        public int MaxAnimationCount => _animationLoader.BodyCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AnimationGroupsType GetAnimType(ushort graphic) =>
            graphic < _animationLoader.BodyCount ? (AnimationGroupsType)_animationLoader.GetBody(graphic).Type : 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AnimationFlags GetAnimFlags(ushort graphic) =>
            graphic < _animationLoader.BodyCount ? (AnimationFlags)_animationLoader.GetBody(graphic).Flags : 0;

        public bool PixelCheck(
            ushort animID,
            byte group,
            byte direction,
            bool uop,
            int frame,
            int x,
            int y
        )
        {
            if (_picker.Get(PickerKey(animID, group, direction, frame), x, y))
            {
                return true;
            }

            ushort resolved = _animationLoader.ResolveBody(animID, false, out _);

            return resolved != animID && _picker.Get(PickerKey(resolved, group, direction, frame), x, y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong PickerKey(ushort body, byte action, byte dir, int frame) =>
            body | ((ulong)(uint)frame << 16) | ((ulong)action << 32) | ((ulong)dir << 40);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetAnimDirection(ref byte dir, ref bool mirror)
        {
            switch (dir)
            {
                case 2:
                case 4:
                    mirror = dir == 2;
                    dir = 1;

                    break;

                case 1:
                case 5:
                    mirror = dir == 1;
                    dir = 2;

                    break;

                case 0:
                case 6:
                    mirror = dir == 0;
                    dir = 3;

                    break;

                case 3:
                    dir = 0;

                    break;

                case 7:
                    dir = 4;

                    break;
            }
        }

        public void GetAnimationDimensions(
            byte animIndex,
            ushort graphic,
            byte dir,
            byte animGroup,
            bool ismounted,
            byte frameIndex,
            out int centerX,
            out int centerY,
            out int width,
            out int height
        )
        {
            dir &= 0x7F;
            bool mirror = false;
            GetAnimDirection(ref dir, ref mirror);

            if (frameIndex == 0xFF)
            {
                frameIndex = (byte)animIndex;
            }

            var frames = GetAnimationFrames(graphic, animGroup, dir, out _, out _, false);

            if (!frames.IsEmpty && frameIndex < frames.Length && frames[frameIndex].Texture != null)
            {
                centerX = frames[frameIndex].Center.X;
                centerY = frames[frameIndex].Center.Y;
                width = frames[frameIndex].UV.Width;
                height = frames[frameIndex].UV.Height;
                return;
            }

            centerX = 0;
            centerY = 0;
            width = 0;
            height = ismounted ? 100 : 60;
        }

        /// <summary>
        /// Frames of (body, action, stored direction 0..4). The pack already applied Bodyconv.def and the UOP
        /// AnimationSequence group replacement; Body.def and Corpse.def aliases are followed here.
        /// </summary>
        public Span<SpriteInfo> GetAnimationFrames(
            ushort id,
            byte action,
            byte dir,
            out ushort hue,
            out bool useUOP,
            bool isEquip = false,
            bool isCorpse = false,
            bool forceUOP = false
        )
        {
            hue = 0;
            useUOP = false;

            if (action >= AnimationsLoader.MAX_ACTIONS || dir >= AnimationsLoader.MAX_DIRECTIONS || id >= _animationLoader.BodyCount)
            {
                return Span<SpriteInfo>.Empty;
            }

            id = _animationLoader.ResolveBody(id, isCorpse, out hue);
            ref readonly var body = ref _animationLoader.GetBody(id);
            useUOP = body.Source != AnimSource.None && (body.Source & AnimSource.Uop) != 0;

            // When we are searching for an equipment item we must ignore any other animation which is not equipment
            var currentAnimType = (AnimationGroupsType)body.Type;

            if (isEquip && currentAnimType != AnimationGroupsType.Equipment && currentAnimType != AnimationGroupsType.Human)
            {
                return Span<SpriteInfo>.Empty;
            }

            ref var actions = ref _cache[id];
            actions ??= new SpriteInfo[AnimationsLoader.MAX_ACTIONS][][];
            ref var dirs = ref actions[action];
            dirs ??= new SpriteInfo[AnimationsLoader.MAX_DIRECTIONS][];
            ref var sprites = ref dirs[dir];

            if (sprites == null)
            {
                sprites = LoadDirection(id, action, dir);
            }

            return sprites;
        }

        private SpriteInfo[] LoadDirection(ushort body, byte action, byte dir)
        {
            // stored client direction d is UO direction d + 3, the only records that are not mirrored copies
            if (!_animationLoader.TryGetDirection(body, action, dir + 3, out var entry))
            {
                return Array.Empty<SpriteInfo>();
            }

            var sprites = new SpriteInfo[entry.FrameCount];

            for (int i = 0; i < sprites.Length; i++)
            {
                uint index = entry.FirstFrame + (uint)i;
                ref readonly var frame = ref _animationLoader.GetFrame(index);

                if (frame.Sprite.OrigW == 0 || frame.Sprite.OrigH == 0)
                {
                    continue;
                }

                var pixels = _animationLoader.DecodeFrame(index);
                ref var spriteInfo = ref sprites[i];

                _picker.Set(PickerKey(body, action, dir, i), frame.Sprite.OrigW, frame.Sprite.OrigH, pixels);

                spriteInfo.Center.X = frame.CenterX;
                spriteInfo.Center.Y = frame.CenterY;
                spriteInfo.Texture = _atlas.AddSprite(pixels, frame.Sprite.OrigW, frame.Sprite.OrigH, out spriteInfo.UV);
                PackSpriteMap.Register(spriteInfo.Texture, spriteInfo.UV, PackSpriteMap.AnimFrame | index);
            }

            return sprites;
        }

        public void UpdateAnimationTable(BodyConvFlags flags)
        {
            // Bodyconv.def is resolved by the pack converter with every expansion enabled.
        }

        public void ConvertBodyIfNeeded(
            ref ushort graphic,
            bool isParent = false,
            bool forceUOP = false,
            bool isCorpse = false
        )
        {
            graphic = _animationLoader.ResolveBody(graphic, isCorpse, out _);
        }

        public bool AnimationExists(ushort graphic, byte group, bool isCorpse = false)
        {
            if (graphic < _animationLoader.BodyCount && group < AnimationsLoader.MAX_ACTIONS)
            {
                var frames = GetAnimationFrames(
                    graphic,
                    group,
                    0,
                    out var _,
                    out _,
                    false,
                    isCorpse
                );

                return !frames.IsEmpty && frames[0].Texture != null;
            }

            return false;
        }
    }
}
