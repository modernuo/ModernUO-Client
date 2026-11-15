// SPDX-License-Identifier: BSD-2-Clause
namespace Uo.Render;

public static class BuildInfo
{
    // static readonly, not const: the client reads the value of the Uo.Render.dll it loaded, not one copied in at its
    // own compile, so a client and a Uo.Render built with different CUO_DEVTOOLS settings are caught at startup
#if CUO_DEVTOOLS
    public static readonly bool DevTools = true;
#else
    public static readonly bool DevTools = false;
#endif
}
