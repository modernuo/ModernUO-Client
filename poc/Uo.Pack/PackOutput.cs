using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Uo.Pack;

/// <summary>
/// A pack output file that is never written in place. Test packs are built by hard-linking files from a published pack
/// and converting only some sections into the copy; opening an existing path with File.Create would truncate and rewrite
/// the shared file, i.e. the published pack. ReplacingFile writes to a temp file in the same folder and, on
/// <see cref="Commit"/>, moves it over the target, which replaces the directory entry (breaking any hard link) instead of
/// writing through it. Disposing without Commit (an exception while writing) deletes the temp file and leaves the
/// target untouched.
/// <para>The target folder is checked against the published packs roots (<see cref="PublishedPacks.EnsureWritable"/>),
/// after resolving links and path aliases, when the file is created and again just before the commit, so no writer can
/// reach a published pack through a junction inside an accepted output folder.</para>
/// </summary>
public sealed class ReplacingFile : FileStream
{
    private readonly string _target;
    private bool _committed, _done;

    private ReplacingFile(string target, string temp)
        : base(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16)
    {
        _target = target;
    }

    public static ReplacingFile Create(string path)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full)!;
        PublishedPacks.EnsureWritable(dir);
        Directory.CreateDirectory(dir);
        return new ReplacingFile(full, Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}{TempSuffix}"));
    }

    private const string TempSuffix = ".replacing.tmp";

    /// <summary>Search pattern for the temp files of crashed writers.</summary>
    public const string TempPattern = ".*" + TempSuffix;

    /// <summary>True for a temp file left behind by a crashed writer (the manifest skips them).</summary>
    public static bool IsTempFile(string path) => Path.GetFileName(path).StartsWith('.') && path.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Flushes, closes and moves the temp file over the target path. On failure (the target is held open by a
    /// running client, the folder became protected) the exception propagates, the target is untouched and the temp file
    /// is deleted; there is no fallback to writing in place.</summary>
    public void Commit()
    {
        Flush(flushToDisk: true);
        _committed = true;
        Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _done)
        {
            base.Dispose(disposing);
            return;
        }
        _done = true;
        string temp = Name;
        bool moved = false;
        try
        {
            base.Dispose(disposing); // may throw (a failed flush); the finally still removes the temp file
            if (_committed)
            {
                PublishedPacks.EnsureWritable(Path.GetDirectoryName(_target)!);
                File.Move(temp, _target, overwrite: true);
                moved = true;
            }
        }
        finally
        {
            // Never mask the original exception: cleanup failures are swallowed.
            if (!moved)
            {
                try { File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        using var fs = Create(path);
        fs.Write(bytes);
        fs.Commit();
    }

    public static void WriteAllText(string path, string text) => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Copies <paramref name="source"/> to <paramref name="dest"/> by replacing the destination entry.</summary>
    public static void Copy(string source, string dest)
    {
        using var fs = Create(dest);
        using (var src = File.OpenRead(source)) src.CopyTo(fs);
        fs.Commit();
    }
}

/// <summary>
/// The only ways the converter tools delete output. There is no recursive delete: they remove single files they write
/// (by name or pattern, never descending into subfolders) after checking the file's folder like every write
/// (<see cref="PublishedPacks.EnsureWritable"/>). Deleting a file removes its directory entry, so a hard link into a
/// published pack only loses this name.
/// </summary>
public static class PackOutputFiles
{
    public const string ManifestFile = "manifest.json";

    /// <summary>Deletes one output file (a no-op when absent) after checking its folder.</summary>
    public static void Delete(string path)
    {
        string full = Path.GetFullPath(path);
        PublishedPacks.EnsureWritable(Path.GetDirectoryName(full)!);
        FileAttributes a;
        try { a = File.GetAttributes(full); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((a & FileAttributes.Directory) != 0) throw new IOException($"{full} is a folder; only files are deleted");
        File.Delete(full);
    }

    /// <summary>Deletes the files directly in <paramref name="dir"/> matching <paramref name="pattern"/> (no subfolders).</summary>
    public static int DeleteFiles(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return 0;
        int n = 0;
        foreach (var f in Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly))
        {
            Delete(f);
            n++;
        }
        return n;
    }

    /// <summary>
    /// Prepares <paramref name="outDir"/> for a full conversion. A missing or empty folder is fine. A folder holding a
    /// previous converter output (a manifest.json) loses exactly the files its manifest lists, the manifest itself and
    /// any crashed-writer temp files beside them; nothing else is touched. Any other non-empty folder is refused: the
    /// converter does not delete what it did not write.
    /// </summary>
    public static int ClearPreviousOutput(string outDir)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir));
        PublishedPacks.EnsureOutputDir(root);
        if (!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any()) return 0;
        string manifestPath = Path.Combine(root, ManifestFile);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"refusing to clear {root}: it is not empty and has no {ManifestFile}, so it is not a converter output. " +
                                                "Choose a new or empty folder, or delete it yourself.");
        var manifest = ManifestFileIo.Read(manifestPath);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        int n = 0;
        foreach (var f in manifest.Files)
        {
            string p = Path.GetFullPath(Path.Combine(root, f.Path));
            if (Path.IsPathRooted(f.Path) || !PublishedPacks.IsSameOrUnder(p, root) || p.Length == root.Length)
                throw new InvalidOperationException($"refusing to clear {root}: its manifest names {f.Path}, outside the pack");
            if (!File.Exists(p)) continue;
            Delete(p);
            dirs.Add(Path.GetDirectoryName(p)!);
            n++;
        }
        foreach (var d in dirs) n += DeleteFiles(d, ReplacingFile.TempPattern);
        Delete(manifestPath);
        return n + 1;
    }
}

/// <summary>
/// Published pack versions (C:\Repositories\uo-packs\&lt;version&gt;, named by uo-packs\CURRENT) are immutable. Every
/// converter write and delete checks its folder, and every output folder is checked before use. Roots: the default
/// <see cref="DefaultRoot"/>, the paths in <see cref="RootsEnvVar"/> (';'-separated), the <see cref="RootsOption"/>
/// arguments (<see cref="ConfigureFromArgs"/>, once per process), and any ancestor that holds a CURRENT file naming one
/// of its subfolders.
/// <para>Paths are compared in canonical form: on Windows the nearest existing ancestor is opened and its final path
/// taken from the file system (junctions, symlinks, subst drives, mount points, 8.3 names, \\?\ and \\.\ prefixes and
/// volume GUID paths all collapse to one drive path). Network paths (UNC shares) are also compared by file identity
/// (volume serial + file id) against the protected folders, so a share of a published folder is recognised. When the
/// relationship cannot be established (a link loop, a path that cannot be opened, missing file ids) the write is refused.</para>
/// </summary>
public static partial class PublishedPacks
{
    public const string DefaultRoot = @"C:\Repositories\uo-packs";
    public const string RootsEnvVar = "UO_PACKS_ROOTS";
    public const string RootsOption = "--packs-root";
    public const string CurrentFile = "CURRENT";

    private static readonly StringComparison PathCmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string[] _extraRoots = [];

    /// <summary>Adds <paramref name="extraRoots"/> to the process-wide configuration (replacing earlier extras).</summary>
    public static void Configure(IEnumerable<string> extraRoots) => _extraRoots = extraRoots.ToArray();

    /// <summary>
    /// Collects every <c>--packs-root &lt;dir&gt;</c> from <paramref name="args"/>, wherever it appears, configures them
    /// process-wide and returns the remaining arguments. Tools call this once, before handling any other argument, so
    /// every command they run sees the same roots.
    /// </summary>
    public static string[] ConfigureFromArgs(string[] args)
    {
        var rest = new List<string>(args.Length);
        var roots = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != RootsOption)
            {
                rest.Add(args[i]);
                continue;
            }
            if (i + 1 >= args.Length) throw new ArgumentException($"{RootsOption} needs a folder");
            roots.Add(args[++i]);
        }
        Configure(roots);
        return rest.ToArray();
    }

    /// <summary>The configured roots: <see cref="DefaultRoot"/>, <see cref="RootsEnvVar"/> and <see cref="Configure"/>d ones.</summary>
    public static IEnumerable<string> ConfiguredRoots()
    {
        if (OperatingSystem.IsWindows()) yield return DefaultRoot;
        var env = Environment.GetEnvironmentVariable(RootsEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            foreach (var r in env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return r;
        foreach (var r in _extraRoots) yield return r;
    }

    /// <summary>
    /// The published packs root that <paramref name="dir"/> is or lies under, else null. With
    /// <paramref name="orContains"/>, also a configured root that lies under <paramref name="dir"/> (an output folder
    /// above a protected root). Throws <see cref="IOException"/> when the relationship cannot be established.
    /// </summary>
    public static string? FindProtectedRoot(string dir, IEnumerable<string>? roots = null, bool orContains = false)
    {
        var target = Canonical(dir);
        var rootList = (roots ?? ConfiguredRoots()).ToList();
        var canonicalRoots = new List<(string Given, string Canonical)>();
        foreach (var root in rootList)
        {
            var r = Canonical(root);
            canonicalRoots.Add((root, r));
            if (IsSameOrUnder(target, r)) return root;
            if (orContains && IsSameOrUnder(r, target)) return root;
        }

        // Detected roots: an ancestor (of the path as given or as canonicalised) holding CURRENT that names one of its folders.
        foreach (var start in new[] { Path.GetFullPath(dir), target })
        {
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                if (IsPacksRoot(d.FullName)) return d.FullName;
            }
        }

        // A network path (or a root given as one) can name a protected folder under another spelling that only the file
        // system can match: compare file identities.
        if (OperatingSystem.IsWindows() && (IsNetwork(target) || canonicalRoots.Any(r => IsNetwork(r.Canonical))))
        {
            var hit = IdentityMatch(target, canonicalRoots, orContains);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>Throws when <paramref name="dir"/> is (or resolves to) a folder under a published packs root, or when
    /// that cannot be established. Every write and delete calls this for its folder.</summary>
    public static void EnsureWritable(string dir, IEnumerable<string>? roots = null) => Ensure(dir, roots, orContains: false);

    /// <summary><see cref="EnsureWritable"/> for an output folder a tool will fill: also refuses a folder that contains a
    /// configured packs root.</summary>
    public static void EnsureOutputDir(string dir, IEnumerable<string>? roots = null) => Ensure(dir, roots, orContains: true);

    private static void Ensure(string dir, IEnumerable<string>? roots, bool orContains)
    {
        string? root;
        try
        {
            root = FindProtectedRoot(dir, roots, orContains);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"refusing to write to {dir}: cannot establish whether it is a published pack location ({e.Message}).", e);
        }
        if (root != null)
            throw new InvalidOperationException($"refusing to write to {dir}: it is (or resolves to) a folder under{(orContains ? " or above" : "")} the published packs root {root}. " +
                                                "Published packs are immutable; build a test pack in your own scratch folder (hard-link the unchanged files, then --only <sections>).");
    }

    private static bool IsPacksRoot(string dir)
    {
        try
        {
            var current = Path.Combine(dir, CurrentFile);
            if (!File.Exists(current) || new FileInfo(current).Length > 4096) return false;
            var name = File.ReadAllText(current).Trim();
            return name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && Directory.Exists(Path.Combine(dir, name));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies under it (both full paths). A
    /// separator is appended to the root only when it lacks one, so a volume root (C:\ or /) works.</summary>
    public static bool IsSameOrUnder(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        path = Path.TrimEndingDirectorySeparator(path);
        if (path.Equals(root, PathCmp)) return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathCmp);
    }

    private static bool IsNetwork(string canonical) => canonical.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// The canonical full path: on Windows, the file system's final path of the nearest existing ancestor (links, subst,
    /// mount points and prefixes resolved) plus the not-yet-existing rest; elsewhere <see cref="Resolve"/>. Throws
    /// <see cref="IOException"/> when an existing component cannot be opened (e.g. a link loop) or the result is ambiguous.
    /// </summary>
    public static string Canonical(string path)
    {
        if (!OperatingSystem.IsWindows()) return Resolve(path);
        string full = Path.GetFullPath(path);
        var tail = new List<string>();
        string cur = full;
        string final;
        while (true)
        {
            int err = Native.FinalPath(cur, out final);
            if (err == 0) break;
            bool notFound = err is Native.ErrorFileNotFound or Native.ErrorPathNotFound or Native.ErrorInvalidDrive;
            if (!notFound || EntryExists(cur))
                throw new IOException($"cannot resolve {cur} (Win32 error {err})");
            string? parent = Path.GetDirectoryName(cur);
            if (parent == null)
            {
                // Nothing on the path exists, not even the volume (an unmapped drive): nothing to alias.
                if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException($"cannot resolve {full} (Win32 error {err})");
                final = cur;
                break;
            }
            tail.Add(Path.GetFileName(cur));
            cur = parent;
        }
        if (tail.Any(t => t is "." or ".." || t.Length == 0)) throw new IOException($"cannot resolve {full}: relative components after a missing folder");
        var result = StripPrefix(final);
        for (int i = tail.Count - 1; i >= 0; i--) result = Path.Combine(result, tail[i]);
        return Path.TrimEndingDirectorySeparator(result);
    }

    private static string StripPrefix(string p)
    {
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + p[8..];
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            var rest = p[4..];
            // Only a drive path is canonical; anything else (a volume GUID or device path) has no drive spelling to compare.
            if (rest.Length >= 2 && rest[1] == ':' && char.IsAsciiLetter(rest[0])) return rest;
            throw new IOException($"cannot resolve {p} to a drive or network path");
        }
        return p;
    }

    private static bool EntryExists(string p)
    {
        try
        {
            File.GetAttributes(p); // does not follow links: a dangling or looping link exists
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    /// <summary>Identity comparison for network paths: the target's existing ancestors against every folder of each
    /// existing protected root (and, with <paramref name="orContains"/>, the target against each root's ancestors).</summary>
    private static string? IdentityMatch(string target, List<(string Given, string Canonical)> roots, bool orContains)
    {
        var targetIds = new List<FileId>();
        for (string? d = NearestExisting(target); d != null; d = Path.GetDirectoryName(d))
            targetIds.Add(Native.Identity(d));
        foreach (var (given, canonical) in roots)
        {
            if (!Directory.Exists(canonical)) continue;
            var ids = new HashSet<FileId> { Native.Identity(canonical) };
            foreach (var sub in Directory.EnumerateDirectories(canonical, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false,
                     }))
                ids.Add(Native.Identity(sub));
            if (targetIds.Any(ids.Contains)) return given;
            if (orContains && targetIds.Count > 0 && Directory.Exists(target))
            {
                for (string? a = Path.GetDirectoryName(canonical); a != null; a = Path.GetDirectoryName(a))
                    if (Native.Identity(a).Equals(targetIds[0])) return given;
            }
        }
        return null;
    }

    private static string? NearestExisting(string p)
    {
        for (string? d = p; d != null; d = Path.GetDirectoryName(d))
            if (Directory.Exists(d)) return d;
        return null;
    }

    /// <summary>The full path with every existing junction or symlink component replaced by its final target (the
    /// non-Windows canonical form). Throws <see cref="IOException"/> on a link that cannot be resolved or a loop.</summary>
    public static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        for (int guard = 0; guard < 64; guard++)
        {
            var root = Path.GetPathRoot(full)!;
            var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string cur = root;
            bool changed = false;
            for (int i = 0; i < parts.Length; i++)
            {
                cur = Path.Combine(cur, parts[i]);
                FileSystemInfo info = Directory.Exists(cur) ? new DirectoryInfo(cur) : new FileInfo(cur);
                if (!info.Exists && info.LinkTarget == null) break;
                if (info.LinkTarget == null) continue;
                var target = info.ResolveLinkTarget(returnFinalTarget: true) // throws on a loop
                             ?? throw new IOException($"cannot resolve link {cur}");
                var rest = string.Join(Path.DirectorySeparatorChar, parts[(i + 1)..]);
                full = Path.GetFullPath(rest.Length > 0 ? Path.Combine(target.FullName, rest) : target.FullName);
                changed = true;
                break;
            }
            if (!changed) return full;
        }
        throw new IOException($"cannot resolve {path}: too many links");
    }

    private readonly record struct FileId(uint Volume, ulong Index);

    private static partial class Native
    {
        public const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorInvalidDrive = 15;

        /// <summary>0 and the final path of <paramref name="path"/>, else the Win32 error.</summary>
        public static unsafe int FinalPath(string path, out string final)
        {
            final = "";
            using var h = Open(path);
            if (h.IsInvalid) return Marshal.GetLastPInvokeError();
            var buf = new char[1024];
            while (true)
            {
                uint n;
                fixed (char* b = buf) n = GetFinalPathNameByHandleW(h, b, (uint)buf.Length, 0 /* FILE_NAME_NORMALIZED | VOLUME_NAME_DOS */);
                if (n == 0) return Marshal.GetLastPInvokeError() is var e and not 0 ? e : -1;
                if (n < buf.Length)
                {
                    final = new string(buf, 0, (int)n);
                    return 0;
                }
                buf = new char[n + 1];
            }
        }

        public static FileId Identity(string path)
        {
            using var h = Open(path);
            if (h.IsInvalid) throw new IOException($"cannot open {path} (Win32 error {Marshal.GetLastPInvokeError()})");
            if (!GetFileInformationByHandle(h, out var info)) throw new IOException($"cannot identify {path} (Win32 error {Marshal.GetLastPInvokeError()})");
            ulong index = (ulong)info.FileIndexHigh << 32 | info.FileIndexLow;
            if (index == 0) throw new IOException($"{path}: the file system reports no file id");
            return new FileId(info.VolumeSerialNumber, index);
        }

        private static SafeFileHandle Open(string path) =>
            CreateFileW(path, 0 /* query only */, 7 /* share read, write, delete */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                        0x02000000 /* BACKUP_SEMANTICS: folders too */, IntPtr.Zero);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle h, char* buf, uint len, uint flags);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);
    }
}
