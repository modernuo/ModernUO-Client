namespace Uo.Pack;

/// <summary>
/// A pack output file that is never written in place. Test packs are built by hard-linking files from a published pack
/// and converting only some sections into the copy; opening an existing path with File.Create would truncate and rewrite
/// the shared file, i.e. the published pack. ReplacingFile writes to a temp file in the same folder and, on
/// <see cref="Commit"/>, moves it over the target, which replaces the directory entry (breaking any hard link) instead of
/// writing through it. Disposing without Commit (an exception while writing) deletes the temp file and leaves the
/// target untouched.
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
        Directory.CreateDirectory(dir);
        return new ReplacingFile(full, Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}{TempSuffix}"));
    }

    private const string TempSuffix = ".replacing.tmp";

    /// <summary>True for a temp file left behind by a crashed writer (the manifest skips them).</summary>
    public static bool IsTempFile(string path) => Path.GetFileName(path).StartsWith('.') && path.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Flushes, closes and moves the temp file over the target path.</summary>
    public void Commit()
    {
        Flush(flushToDisk: true);
        _committed = true;
        Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        string temp = Name;
        base.Dispose(disposing);
        if (!disposing || _done) return;
        _done = true;
        if (_committed)
        {
            File.Move(temp, _target, overwrite: true);
        }
        else
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
/// Published pack versions (C:\Repositories\uo-packs\&lt;version&gt;, named by uo-packs\CURRENT) are immutable. The converter
/// refuses an output directory that is, or resolves to (junctions and symlinks are followed), a folder under a published
/// packs root. Roots: the default <see cref="DefaultRoot"/>, the paths in <see cref="RootsEnvVar"/> (';'-separated), any
/// passed explicitly, and any ancestor of the output that holds a CURRENT file naming one of its subfolders.
/// </summary>
public static class PublishedPacks
{
    public const string DefaultRoot = @"C:\Repositories\uo-packs";
    public const string RootsEnvVar = "UO_PACKS_ROOTS";
    public const string CurrentFile = "CURRENT";

    private static readonly StringComparison PathCmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The configured roots: <see cref="DefaultRoot"/> plus <see cref="RootsEnvVar"/>.</summary>
    public static IEnumerable<string> ConfiguredRoots()
    {
        if (OperatingSystem.IsWindows()) yield return DefaultRoot;
        var env = Environment.GetEnvironmentVariable(RootsEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            foreach (var r in env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return r;
    }

    /// <summary>The published packs root that <paramref name="dir"/> is or lies under (after resolving links), else null.</summary>
    public static string? FindProtectedRoot(string dir, IEnumerable<string>? roots = null)
    {
        var resolved = Resolve(dir);
        foreach (var root in roots ?? ConfiguredRoots())
        {
            if (IsSameOrUnder(resolved, Resolve(root))) return root;
        }

        // Detected roots: an ancestor (of the path as given or as resolved) holding CURRENT that names one of its folders.
        foreach (var start in new[] { Path.GetFullPath(dir), resolved })
        {
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                if (IsPacksRoot(d.FullName)) return d.FullName;
            }
        }
        return null;
    }

    /// <summary>Throws when <paramref name="dir"/> is a published pack location.</summary>
    public static void EnsureWritable(string dir, IEnumerable<string>? roots = null)
    {
        var root = FindProtectedRoot(dir, roots);
        if (root != null)
            throw new InvalidOperationException($"refusing to write to {dir}: it is (or resolves to) a folder under the published packs root {root}. " +
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

    private static bool IsSameOrUnder(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        path = Path.TrimEndingDirectorySeparator(path);
        return path.Equals(root, PathCmp) || path.StartsWith(root + Path.DirectorySeparatorChar, PathCmp);
    }

    /// <summary>The full path with every existing junction or symlink component replaced by its final target.</summary>
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
                if (!info.Exists) break;
                FileSystemInfo? target = null;
                try { target = info.LinkTarget != null ? info.ResolveLinkTarget(returnFinalTarget: true) : null; }
                catch (IOException) { }
                if (target == null) continue;
                var rest = string.Join(Path.DirectorySeparatorChar, parts[(i + 1)..]);
                full = Path.GetFullPath(rest.Length > 0 ? Path.Combine(target.FullName, rest) : target.FullName);
                changed = true;
                break;
            }
            if (!changed) return full;
        }
        return full;
    }
}
