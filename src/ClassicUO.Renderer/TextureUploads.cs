// SPDX-License-Identifier: BSD-2-Clause

using System.Threading;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// Counts texture uploads (SetData/SetDataPointerEXT on a texture). The UI render cache (B8) compares recorded draws by
    /// texture reference, so a texture whose pixels change in place (the minimap writes into its atlas slot) must invalidate
    /// it: every texture upload in the client calls <see cref="Changed"/>. TextureUploadSitesTests checks the sources.
    /// </summary>
    public static class TextureUploads
    {
        private static int _epoch;

        public static int Epoch => Volatile.Read(ref _epoch);

        public static void Changed() => Interlocked.Increment(ref _epoch);
    }
}
