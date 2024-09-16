using System;

namespace ClassicUO.Resources
{
    partial class Loader
    {
        [EmbedResourceCSharp.FileEmbed("cuologo.png")]
        public static partial ReadOnlySpan<byte> GetCuoLogo();

        [EmbedResourceCSharp.FileEmbed("game-background.png")]
        public static partial ReadOnlySpan<byte> GetBackgroundImage();
    }
}
