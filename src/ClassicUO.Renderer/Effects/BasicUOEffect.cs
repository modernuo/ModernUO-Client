using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer.Effects
{
    internal class BasicUOEffect : Effect
    {
        public BasicUOEffect(GraphicsDevice graphicsDevice) : base(graphicsDevice, Resources.GetUOShader().ToArray())
        {
            MatrixTransform = Parameters["MatrixTransform"];
            WorldMatrix = Parameters["WorldMatrix"];
            Viewport = Parameters["Viewport"];
            Brighlight = Parameters["Brightlight"];
            CircleOfTransparencyRadius = Parameters["CircleOfTransparencyRadius"];

            GeoCam = Parameters["GeoCam"];

            _hueTechnique = Techniques["HueTechnique"];
            _geoTechnique = Techniques["HueGeoTechnique"];
            _instancedTechnique = Techniques["HueInstancedTechnique"];
            _huePass = _hueTechnique.Passes[0];
            _geoPass = _geoTechnique.Passes[0];
            _instancedPass = _instancedTechnique.Passes[0];
            _technique = _hueTechnique;
            CurrentTechnique = _hueTechnique;
            Pass = _huePass;
        }

        private readonly EffectTechnique _hueTechnique, _geoTechnique, _instancedTechnique;
        private readonly EffectPass _huePass, _geoPass, _instancedPass;
        private EffectTechnique _technique;

        /// <summary>clip-4 (depth_model): HueGeoTechnique, which writes the geometric depth (LegacyGeo), or HueTechnique.</summary>
        public void SetGeo(bool on)
        {
            _technique = on ? _geoTechnique : _hueTechnique;
            CurrentTechnique = _technique;
            Pass = on ? _geoPass : _huePass;
        }

        /// <summary>
        /// legacy_instanced_sprites: binds HueInstancedTechnique with the current parameter values. The caller draws its
        /// instances, then calls <see cref="RestoreTechnique"/> and applies <see cref="Pass"/> again.
        /// </summary>
        public void ApplyInstanced()
        {
            CurrentTechnique = _instancedTechnique;
            _instancedPass.Apply();
        }

        /// <summary>Makes the technique <see cref="Pass"/> belongs to current again after <see cref="ApplyInstanced"/>.</summary>
        public void RestoreTechnique()
        {
            CurrentTechnique = _technique;
        }

        public EffectParameter MatrixTransform { get; }
        public EffectParameter WorldMatrix { get; }
        public EffectParameter Viewport { get; }
        public EffectParameter Brighlight { get; }
        public EffectParameter CircleOfTransparencyRadius { get; }
        public EffectParameter GeoCam { get; }
        public EffectPass Pass { get; private set; }
    }
}
