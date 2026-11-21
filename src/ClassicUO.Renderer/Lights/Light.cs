using ClassicUO.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer.Lights
{
    public sealed class Light
    {
        private readonly TextureAtlas _atlas;
        private readonly SpriteInfo[] _spriteInfos;
        private readonly LightsLoader _lightsLoader;

        public Light(LightsLoader lightsLoader, GraphicsDevice device)
        {
            _lightsLoader = lightsLoader;
            int page = TextureAtlas.PageSize(2048, 512);
            _atlas = new TextureAtlas(device, page, page, SurfaceFormat.Color);
            _spriteInfos = new SpriteInfo[lightsLoader.Count];
        }

        public ref readonly SpriteInfo GetLight(uint idx)
        {
            if (idx >= _spriteInfos.Length)
                return ref SpriteInfo.Empty;

            ref var spriteInfo = ref _spriteInfos[idx];

            if (spriteInfo.Texture == null)
            {
                var lightInfo = _lightsLoader.GetLight(idx);
                if (!lightInfo.Pixels.IsEmpty)
                {
                    spriteInfo.Texture = _atlas.AddSprite(
                        lightInfo.Pixels,
                        lightInfo.Width,
                        lightInfo.Height,
                        out spriteInfo.UV
                    );
                    _lru?.Uploaded(spriteInfo.Texture, idx, lightInfo.Width, lightInfo.Height);
                }
            }

            return ref spriteInfo;
        }

        // ---- atlas_lru: the light atlas's page LRU ----

        private AtlasPageLru _lru;

        public AtlasPageLru Lru => _lru;

        public long AtlasPageBytes => _atlas.PageBytes;

        /// <summary>atlas_lru on: call before the first sprite loads.</summary>
        public void EnableLru(Animations.AtlasLruPolicy policy)
        {
            _atlas.TrackPages = true;
            _lru = new AtlasPageLru("light", _atlas, _atlas.PageBytes, policy, DropPage);
        }

        /// <summary>Once per frame before anything is drawn (atlas_lru on): frees pages above the budget.</summary>
        public void TickLru(long nowMs, bool unfocused) => _lru?.Tick(nowMs, unfocused);

        // the entries on a page about to be freed load again on their next lookup
        private void DropPage(Texture2D page, System.Collections.Generic.List<uint> keys)
        {
            if (keys == null)
            {
                return;
            }

            foreach (uint idx in keys)
            {
                ref var spriteInfo = ref _spriteInfos[idx];

                if (ReferenceEquals(spriteInfo.Texture, page))
                {
                    _lru.Forgot(idx, spriteInfo.UV.Width, spriteInfo.UV.Height);
                    spriteInfo = default;
                }
            }
        }
    }
}
