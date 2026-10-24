using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer
{
    // Native handles behind FNA3D's SDL_GPU driver. Needs the patched FNA3D (external/patches/fna3d-sdlgpu-interop.patch).
    public static unsafe partial class GpuInterop
    {
        private const int RENDERER_TYPE_SDL_GPU = 4;

        [StructLayout(LayoutKind.Explicit, Size = 72)]
        private struct SysRenderer
        {
            [FieldOffset(0)] public uint Version;
            [FieldOffset(4)] public int RendererType;
            [FieldOffset(8)] public IntPtr SdlGpuDevice;
        }

        [LibraryImport("FNA3D")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial void FNA3D_GetSysRendererEXT(IntPtr device, SysRenderer* sysrenderer);

        [LibraryImport("FNA3D")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial IntPtr FNA3D_SDLGPU_GetTextureEXT(IntPtr device, IntPtr texture);

        [LibraryImport("SDL3")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial byte* SDL_GetGPUDeviceDriver(IntPtr device);

        [LibraryImport("SDL3")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial uint SDL_GetGPUShaderFormats(IntPtr device);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "GLDevice")]
        private static extern ref IntPtr GLDevice(GraphicsDevice device);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "texture")]
        private static extern ref IntPtr NativeTexture(Texture texture);

        /// <summary>The SDL_GPUDevice* FNA3D renders with, or zero when FNA3D is not on the SDL_GPU driver.</summary>
        public static IntPtr GetDevice(GraphicsDevice device)
        {
            IntPtr fna3d = GLDevice(device);

            if (fna3d == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            SysRenderer sys = default;
            FNA3D_GetSysRendererEXT(fna3d, &sys);

            return sys.RendererType == RENDERER_TYPE_SDL_GPU ? sys.SdlGpuDevice : IntPtr.Zero;
        }

        /// <summary>The SDL_GPUTexture* behind a Texture2D or RenderTarget2D. Re-fetch it every frame: render targets are recreated on resize.</summary>
        public static IntPtr GetTexture(Texture texture)
        {
            if (texture == null || texture.IsDisposed)
            {
                return IntPtr.Zero;
            }

            return FNA3D_SDLGPU_GetTextureEXT(GLDevice(texture.GraphicsDevice), NativeTexture(texture));
        }

        public static string GetDriverName(IntPtr gpuDevice)
        {
            return gpuDevice == IntPtr.Zero ? null : Marshal.PtrToStringUTF8((IntPtr)SDL_GetGPUDeviceDriver(gpuDevice));
        }

        public static string GetShaderFormats(IntPtr gpuDevice)
        {
            if (gpuDevice == IntPtr.Zero)
            {
                return null;
            }

            uint formats = SDL_GetGPUShaderFormats(gpuDevice);
            string[] names = ["PRIVATE", "SPIRV", "DXBC", "DXIL", "MSL", "METALLIB"];
            string result = "";

            for (int i = 0; i < names.Length; i++)
            {
                if ((formats & (1u << i)) != 0)
                {
                    result += result.Length == 0 ? names[i] : "|" + names[i];
                }
            }

            return result;
        }
    }
}
