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
            _huePass = _hueTechnique.Passes[0];
            _geoPass = _geoTechnique.Passes[0];
            CurrentTechnique = _hueTechnique;
            Pass = _huePass;
        }

        private readonly EffectTechnique _hueTechnique, _geoTechnique;
        private readonly EffectPass _huePass, _geoPass;

        /// <summary>clip-4 (depth_model): HueGeoTechnique, which writes the geometric depth (LegacyGeo), or HueTechnique.</summary>
        public void SetGeo(bool on)
        {
            CurrentTechnique = on ? _geoTechnique : _hueTechnique;
            Pass = on ? _geoPass : _huePass;
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
