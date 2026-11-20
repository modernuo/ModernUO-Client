// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Utility.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClassicUO.Assets
{
    /// <summary>
    /// Read-only memory shared between client instances (shared_cliloc). A pack table that is stored raw is mapped
    /// straight from the pack file; one that is stored compressed is decompressed once into a per-user cache file keyed
    /// by the pack's content hash (manifest.json's sha256) and mapped from there, so later launches and other instances
    /// skip the decompression and the OS shares the pages.
    ///
    /// Layout: &lt;root&gt;/&lt;pack key&gt;/&lt;name&gt;.bin, each with a <see cref="CacheHeader"/> (the source record it was
    /// made from and a hash of the payload): a file that is short, damaged or made from other data is rebuilt, never used.
    /// Files are built in a temp file beside the target and moved in without replacing (the loser of two builders maps
    /// the winner's file). Every instance holds a shared lock on &lt;pack key&gt;/.inuse (a FileShare.ReadWrite handle:
    /// flock LOCK_SH on Linux and macOS), and the startup cleanup deletes only folders it can lock exclusively, so a
    /// folder in use is never deleted. Any failure falls back to the caller's heap path.
    ///
    /// Mappings live until <see cref="Dispose"/> (the process's end): tables built over them may be read by any thread at
    /// any time, so a mapping is never released while the process runs. Readers use <see cref="Mapping.Pointer"/>
    /// directly (a span over the base pointer acquired once), never a view accessor on a lookup path.
    ///
    /// Protected packs (later): <see cref="AllowDiskCache"/> false keeps plaintext off the disk; compressed tables then
    /// stay on the heap path (a memory-only shared section is the place to add instead).
    /// </summary>
    public sealed class SharedTableCache : IDisposable
    {
        public const string LockFileName = ".inuse";
        /// <summary>Safety net on the cache root's total size: a file that would push it past this is not written.</summary>
        public const long DefaultCapBytes = 512L << 20;
        // a temp file older than this is a crashed builder's (a build takes well under a second)
        internal static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(10);

        private readonly object _gate = new object();
        private readonly List<Mapping> _mappings = new List<Mapping>();
        private readonly Dictionary<string, Mapping> _byKey = new Dictionary<string, Mapping>(StringComparer.Ordinal);
        private FileStream _lock;
        private bool _disposed;

        public string Root { get; }
        public string PackKey { get; }
        public string Folder { get; }
        public long CapBytes { get; }
        public bool AllowDiskCache { get; }

        /// <summary>Counters for logs, tests and measurements.</summary>
        public int Built, Reused, Rebuilt, DirectMaps, Fallbacks, StaleFoldersDeleted, StaleFoldersInUse;

        public SharedTableCache(string root, string packKey, long capBytes = DefaultCapBytes, bool allowDiskCache = true)
        {
            Root = root;
            PackKey = packKey;
            Folder = Path.Combine(root, packKey);
            CapBytes = capBytes;
            AllowDiskCache = allowDiskCache;
        }

        /// <summary>
        /// The per-user cache root: CUO_SHARED_CACHE_DIR when set, else %LOCALAPPDATA% (Windows), $XDG_CACHE_HOME or
        /// ~/.cache (Linux), ~/Library/Caches (macOS), with ModernUO/cache under it.
        /// </summary>
        public static string DefaultRoot()
        {
            string env = Environment.GetEnvironmentVariable("CUO_SHARED_CACHE_DIR");

            if (!string.IsNullOrEmpty(env))
            {
                return env;
            }

            string baseDir;

            if (OperatingSystem.IsWindows())
            {
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            else if (OperatingSystem.IsMacOS())
            {
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches");
            }
            else
            {
                string xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
                baseDir = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)
                    ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            }

            return Path.Combine(baseDir, "ModernUO", "cache");
        }

        /// <summary>The pack's content key: the first 128 bits of manifest.json's sha256, lower-case hex.</summary>
        public static string PackKeyOf(string manifestPath)
        {
            Span<byte> hash = stackalloc byte[32];
            using (var fs = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                SHA256.HashData(fs, hash);
            }

            return Convert.ToHexStringLower(hash.Slice(0, 16));
        }

        /// <summary>
        /// Takes the shared lock on this pack's folder (creating it) and deletes the other packs' folders that no instance
        /// uses, plus crashed builders' temp files. False when the folder can't be used (the caller falls back).
        /// </summary>
        public bool Open()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                _lock = OpenLock(Folder, exclusive: false);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.Warn($"Shared tables: cannot use '{Folder}' ({e.Message}); tables stay on the heap");
                _lock = null;

                return false;
            }

            DeleteStaleFolders();
            DeleteStaleTemps(Folder);

            return true;
        }

        public bool IsOpen => _lock != null;

        // FileShare.ReadWrite: other instances may hold it too (Unix: flock LOCK_SH). FileShare.None: nobody else may
        // (Unix: LOCK_EX); fails while any instance holds it.
        internal static FileStream OpenLock(string folder, bool exclusive) =>
            new FileStream(Path.Combine(folder, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                exclusive ? FileShare.None : FileShare.ReadWrite | FileShare.Delete);

        private void DeleteStaleFolders()
        {
            string[] dirs;

            try
            {
                dirs = Directory.GetDirectories(Root);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return;
            }

            foreach (string dir in dirs)
            {
                if (string.Equals(Path.GetFileName(dir), PackKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryDeleteFolder(dir))
                {
                    StaleFoldersDeleted++;
                }
                else
                {
                    StaleFoldersInUse++;
                }
            }
        }

        /// <summary>Deletes a cache folder unless an instance holds its lock (or a file in it is mapped). Tests use it.</summary>
        internal static bool TryDeleteFolder(string dir)
        {
            FileStream exclusive;

            try
            {
                exclusive = OpenLock(dir, exclusive: true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false; // in use (or not ours to delete)
            }

            bool allGone = true;

            using (exclusive)
            {
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    if (string.Equals(Path.GetFileName(f), LockFileName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(f);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        allGone = false; // Windows: still mapped by a process that dropped its lock; next launch retries
                    }
                }
            }

            if (!allGone)
            {
                return false;
            }

            try
            {
                File.Delete(Path.Combine(dir, LockFileName));
                Directory.Delete(dir, false);

                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false; // an instance took the lock between the two steps: it keeps the folder
            }
        }

        private static void DeleteStaleTemps(string folder)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(folder, "*.tmp"))
                {
                    try
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > StaleTempAge)
                        {
                            File.Delete(f);
                        }
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Maps <paramref name="length"/> bytes at <paramref name="offset"/> of a pack file read-only (a raw record), or
        /// null when it can't be mapped. The same range asked again returns the same mapping.
        /// </summary>
        public Mapping MapFileRange(string path, long offset, long length)
        {
            string key = "file|" + Path.GetFullPath(path) + "|" + offset + "|" + length;

            lock (_gate)
            {
                if (_byKey.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                try
                {
                    var m = Mapping.Create(path, offset, length);
                    Add(key, m);
                    DirectMaps++;

                    return m;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
                {
                    Log.Warn($"Shared tables: cannot map '{path}' ({e.Message}); the table stays on the heap");
                    Fallbacks++;

                    return null;
                }
            }
        }

        /// <summary>
        /// The cache file <paramref name="name"/> of this pack, built with <paramref name="fill"/> (which writes exactly
        /// <paramref name="rawBytes"/> bytes into the span it gets) when it is missing or does not match
        /// <paramref name="source"/>, then mapped. Null when the disk cache is off, the folder is unusable, the cap
        /// would be exceeded, or anything fails: the caller then uses its heap path.
        /// </summary>
        public Mapping GetOrBuild(string name, in CacheSource source, int rawBytes, FillDelegate fill)
        {
            if (!AllowDiskCache || _lock == null || rawBytes < 0)
            {
                return null;
            }

            string target = Path.Combine(Folder, name);
            string key = "cache|" + target;

            lock (_gate)
            {
                if (_byKey.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                try
                {
                    var m = OpenValid(target, source, rawBytes);
                    bool rebuilt = false;

                    if (m == null && File.Exists(target))
                    {
                        // short, damaged or made from other data: replace it (fails on Windows while another process maps it)
                        try
                        {
                            File.Delete(target);
                            rebuilt = true;
                        }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                        {
                            Log.Warn($"Shared tables: '{target}' does not match and can't be replaced ({e.Message}); the table stays on the heap");
                            Fallbacks++;

                            return null;
                        }
                    }

                    if (m != null)
                    {
                        Reused++;
                    }
                    else
                    {
                        if (TotalBytes(Root) + CacheHeader.Size + rawBytes > CapBytes)
                        {
                            Log.Warn($"Shared tables: the cache at '{Root}' would pass its {CapBytes >> 20} MiB cap; '{name}' stays on the heap");
                            Fallbacks++;

                            return null;
                        }

                        m = Build(target, source, rawBytes, fill);

                        if (m == null)
                        {
                            Fallbacks++;

                            return null;
                        }

                        if (rebuilt)
                        {
                            Rebuilt++;
                        }
                        else
                        {
                            Built++;
                        }
                    }

                    Add(key, m);

                    return m;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException || e is ArgumentException)
                {
                    Log.Warn($"Shared tables: '{target}' failed ({e.Message}); the table stays on the heap");
                    Fallbacks++;

                    return null;
                }
            }
        }

        public delegate void FillDelegate(Span<byte> destination);

        private void Add(string key, Mapping m)
        {
            _byKey[key] = m;
            _mappings.Add(m);
        }

        // The mapping of a cache file whose header and hash match, else null (missing, short, damaged, other source).
        private static Mapping OpenValid(string path, in CacheSource source, int rawBytes)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            Mapping m;

            try
            {
                m = Mapping.Create(path, 0, -1);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                return null; // e.g. an empty file
            }

            if (Validate(m, source, rawBytes))
            {
                return m;
            }

            m.Dispose();

            return null;
        }

        internal static unsafe bool Validate(Mapping m, in CacheSource source, int rawBytes)
        {
            if (m.Length != CacheHeader.Size + (long)rawBytes)
            {
                return false;
            }

            var header = *(CacheHeader*)m.Pointer;

            if (header.Magic != CacheHeader.MagicValue || header.Version != CacheHeader.CurrentVersion ||
                header.HeaderBytes != CacheHeader.Size || header.PayloadBytes != (ulong)rawBytes || !header.Source.Equals(source))
            {
                return false;
            }

            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(new ReadOnlySpan<byte>(m.Pointer + CacheHeader.Size, rawBytes), hash);

            return hash.Slice(0, 16).SequenceEqual(new ReadOnlySpan<byte>(header.Hash, 16));
        }

        private unsafe Mapping Build(string target, in CacheSource source, int rawBytes, FillDelegate fill)
        {
            string temp = target + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";

            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    fs.SetLength(CacheHeader.Size + (long)rawBytes);

                    using var mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
                    using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
                    byte* p = null;
                    view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);

                    try
                    {
                        p += view.PointerOffset;
                        var payload = new Span<byte>(p + CacheHeader.Size, rawBytes);
                        fill(payload);

                        var header = new CacheHeader
                        {
                            Magic = CacheHeader.MagicValue,
                            Version = CacheHeader.CurrentVersion,
                            HeaderBytes = CacheHeader.Size,
                            PayloadBytes = (ulong)rawBytes,
                            Source = source,
                        };
                        Span<byte> hash = stackalloc byte[32];
                        SHA256.HashData(payload, hash);
                        hash.Slice(0, 16).CopyTo(new Span<byte>(header.Hash, 16));
                        *(CacheHeader*)p = header;
                    }
                    finally
                    {
                        view.SafeMemoryMappedViewHandle.ReleasePointer();
                    }

                    view.Flush();
                }

                try
                {
                    // never over an existing file: another instance may have built and mapped it meanwhile
                    File.Move(temp, target, overwrite: false);
                }
                catch (IOException) when (File.Exists(target))
                {
                    TryDelete(temp);
                    var winner = OpenValid(target, source, rawBytes);

                    if (winner == null)
                    {
                        Log.Warn($"Shared tables: '{target}' was written by another instance and does not match; the table stays on the heap");
                    }

                    return winner;
                }

                return OpenValid(target, source, rawBytes);
            }
            catch
            {
                TryDelete(temp);

                throw;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        internal static long TotalBytes(string root)
        {
            long total = 0;

            try
            {
                foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    total += f.Length;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }

            return total;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                foreach (var m in _mappings)
                {
                    m.Dispose();
                }

                _mappings.Clear();
                _byKey.Clear();
                _lock?.Dispose();
                _lock = null;
            }
        }

        /// <summary>The source a cache file was made from: a record of a pack file (its size, the record's place, sizes and codec).</summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
        public struct CacheSource : IEquatable<CacheSource>
        {
            public ulong FileBytes;
            public ulong Offset;
            public uint StoredBytes;
            public uint RawBytes;
            public ushort Codec;
            public ushort Reserved;
            public uint Reserved2;

            public readonly bool Equals(CacheSource o) =>
                FileBytes == o.FileBytes && Offset == o.Offset && StoredBytes == o.StoredBytes && RawBytes == o.RawBytes &&
                Codec == o.Codec;

            public override readonly bool Equals(object obj) => obj is CacheSource o && Equals(o);
            public override readonly int GetHashCode() => HashCode.Combine(FileBytes, Offset, StoredBytes, RawBytes, Codec);
        }

        /// <summary>64 bytes at the start of a cache file; the payload follows.</summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
        public unsafe struct CacheHeader
        {
            public const int Size = 64;
            public const uint MagicValue = 0x43534F55; // "UOSC"
            public const ushort CurrentVersion = 1;

            public uint Magic;
            public ushort Version;
            public ushort HeaderBytes;
            public ulong PayloadBytes;
            public CacheSource Source; // 32 bytes
            public fixed byte Hash[16]; // the payload's sha256, first 16 bytes
        }

        /// <summary>
        /// A read-only mapping of a file range. The base pointer is acquired once and released at <see cref="Dispose"/>;
        /// <see cref="Pointer"/> points at the range's first byte.
        /// </summary>
        public sealed unsafe class Mapping : IDisposable
        {
            private MemoryMappedFile _file;
            private MemoryMappedViewAccessor _view;
            private SafeMemoryMappedViewHandle _handle;
            private byte* _base;

            public byte* Pointer { get; private set; }
            public long Length { get; }
            public string Path { get; }

            private Mapping(string path, long length)
            {
                Path = path;
                Length = length;
            }

            /// <summary>Maps <paramref name="length"/> bytes at <paramref name="offset"/> (length −1: to the file's end).</summary>
            internal static Mapping Create(string path, long offset, long length)
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                long fileBytes = fs.Length;

                if (length < 0)
                {
                    length = fileBytes - offset;
                }

                if (offset < 0 || length <= 0 || offset > fileBytes || length > fileBytes - offset)
                {
                    throw new ArgumentException($"range {offset}+{length} is outside the file ({fileBytes} bytes)");
                }

                var m = new Mapping(path, length);

                try
                {
                    m._file = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
                    m._view = m._file.CreateViewAccessor(offset, length, MemoryMappedFileAccess.Read);
                    m._handle = m._view.SafeMemoryMappedViewHandle;
                    byte* p = null;
                    m._handle.AcquirePointer(ref p);
                    m._base = p;
                    m.Pointer = p + m._view.PointerOffset;
                }
                catch
                {
                    m.Dispose();

                    throw;
                }

                return m;
            }

            public ReadOnlySpan<byte> Span => new ReadOnlySpan<byte>(Pointer, checked((int)Length));

            /// <summary>
            /// Touches every page once (one byte per 4 KiB), so later reads find them resident: a page another instance
            /// already has costs a soft fault here instead of on a lookup. Returns a value so the reads aren't removed.
            /// </summary>
            public int Prefault()
            {
                // Runs on a worker and can race Dispose (a test or a reload disposing the table first): a reference on
                // the view handle keeps the view mapped until the walk ends, and a disposed mapping is skipped.
                SafeMemoryMappedViewHandle handle = _handle;
                byte* p = Pointer;

                if (handle == null || p == null)
                {
                    return 0;
                }

                bool added = false;

                try
                {
                    handle.DangerousAddRef(ref added);
                }
                catch (ObjectDisposedException)
                {
                    return 0;
                }

                try
                {
                    int sum = 0;

                    for (long i = 0; i < Length; i += 4096)
                    {
                        sum += Volatile.Read(ref p[i]);
                    }

                    return sum;
                }
                finally
                {
                    if (added)
                    {
                        handle.DangerousRelease();
                    }
                }
            }

            public void Dispose()
            {
                if (_base != null)
                {
                    _handle.ReleasePointer();
                    _base = null;
                    Pointer = null;
                }

                _view?.Dispose();
                _view = null;
                _file?.Dispose();
                _file = null;
            }
        }
    }
}
