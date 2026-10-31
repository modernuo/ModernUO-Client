// SPDX-License-Identifier: BSD-2-Clause

using System.Runtime.InteropServices;

namespace ClassicUO.IO.Audio
{
    /// <summary>
    ///     P/Invoke surface of the native cuo_mp3 library (src/native/cuo_mp3, dr_mp3 v0.7.4). JIT builds load
    ///     cuo_mp3.dll / libcuo_mp3.so / libcuo_mp3.dylib from the app folder; NativeAOT publishes link the static
    ///     library into the executable (DirectPInvoke in ClassicUO.Client.csproj), so no separate file ships.
    /// </summary>
    internal static unsafe partial class Mp3Native
    {
        public const string Library = "cuo_mp3";

        /// <summary>Mirrors cuo_mp3_info in cuo_mp3.h.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Info
        {
            public uint SampleRate;
            public uint Channels;
            public uint DelayFrames;
            public uint PaddingFrames;
            public ulong RawFrames;
        }

        [LibraryImport(Library, EntryPoint = "cuo_mp3_version")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        public static partial uint Version();

        [LibraryImport(Library, EntryPoint = "cuo_mp3_open_memory")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        public static partial nint OpenMemory(void* data, nuint size);

        [LibraryImport(Library, EntryPoint = "cuo_mp3_close")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        public static partial void Close(nint mp3);

        [LibraryImport(Library, EntryPoint = "cuo_mp3_get_info")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        public static partial int GetInfo(nint mp3, Info* info);

        [LibraryImport(Library, EntryPoint = "cuo_mp3_read_s16")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        public static partial ulong ReadS16(nint mp3, ulong frames, short* output);

        [LibraryImport(Library, EntryPoint = "cuo_mp3_seek")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        public static partial int Seek(nint mp3, ulong frame);

        [LibraryImport(Library, EntryPoint = "cuo_mp3_get_frame_count")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        public static partial ulong GetFrameCount(nint mp3);
    }
}
