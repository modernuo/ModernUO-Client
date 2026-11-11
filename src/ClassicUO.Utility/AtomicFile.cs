// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Utility
{
    /// <summary>
    /// File access that is safe when several client instances share a file (profiles, settings, caches).
    /// Writers go to a temp file next to the target and replace the target in one rename, so a reader never sees a
    /// half-written file and a crash never leaves a truncated one. Readers open with FileShare.ReadWrite | Delete and
    /// the writer retries its rename for a short budget (200 ms by default) while a reader (Windows) still has the old file open. Two instances saving the same file is last-writer-wins: no locking.
    /// </summary>
    public static class AtomicFile
    {
        // most saves run on the game thread: a contested rename may stall it this long, then the save is dropped (logged by
        // the caller) and the next save retries. Background writers (map cache) pass a longer wait.
        public const int DefaultReplaceBudgetMs = 200;
        internal const int ReplaceDelayMs = 20;

        internal const int OpenReadBudgetMs = 70;

        /// <summary>Reads without blocking (or being blocked by) another instance replacing the file.</summary>
        public static FileStream OpenRead(string path, int budgetMs = OpenReadBudgetMs)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            while (true)
            {
                try
                {
                    return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (Exception e) when (IsTransient(e, path, clock.ElapsedMilliseconds, budgetMs, true) && clock.ElapsedMilliseconds < budgetMs)
                {
                    // another instance has the file open without sharing for a moment; a missing file, a denied one or
                    // any other error is permanent and goes to the caller at once
                    Thread.Sleep((int)Math.Min(10, budgetMs - clock.ElapsedMilliseconds));
                }
            }
        }

        /// <summary>
        /// Copies <paramref name="path"/> to <c>path.bak</c> (once: an existing backup is kept) before a save replaces a file
        /// the client failed to load, so the user's data survives a failed load followed by a save of defaults.
        /// Returns true when the data is preserved (copied now, already backed up, or no file); false when the copy
        /// failed: the caller must not replace the file and should retry on its next save.
        /// </summary>
        public static bool KeepBackup(string path)
        {
            try
            {
                string bak = path + ".bak";

                if (File.Exists(path) && !File.Exists(bak))
                {
                    File.Copy(path, bak);
                }

                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.Error($"could not back up {path}: {e.Message}; the file is left as it is");

                return false;
            }
        }

        public static string ReadAllText(string path)
        {
            using var reader = new StreamReader(OpenRead(path), Encoding.UTF8, true);

            return reader.ReadToEnd();
        }

        public static void LoadXml(XmlDocument doc, string path)
        {
            using var stream = OpenRead(path);
            doc.Load(stream);
        }

        public static void WriteAllText(string path, string text, int waitMs = DefaultReplaceBudgetMs)
        {
            using var stream = OpenWrite(path, waitMs);
            stream.ThrowOnFailure = true;
            var bytes = new UTF8Encoding(false).GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Commit();
        }

        /// <summary>
        /// Appends as read-modify-atomic-write (the file is replaced, never opened for write in place), so readers on
        /// <see cref="OpenRead"/> and other writers never see or fight over a half-written file. Two instances appending at the
        /// same moment is last-writer-wins: one line can be lost, the file is never torn. A missing file is created.
        /// </summary>
        public static void AppendAllText(string path, string text, int waitMs = DefaultReplaceBudgetMs)
        {
            string existing = File.Exists(path) ? ReadAllText(path) : string.Empty;

            if (existing.Length > 0 && existing[existing.Length - 1] != '\n' && existing[existing.Length - 1] != '\r')
            {
                existing += Environment.NewLine;
            }

            WriteAllText(path, existing + text, waitMs);
        }

        /// <summary>
        /// Read-modify-atomic-write: <paramref name="transform"/> gets the current text (empty when the file is missing) and
        /// returns the new text, or null to leave the file alone. The read happens here, so a change another instance
        /// published earlier is seen by the transform (a repair that adds missing entries converges). Two instances inside the
        /// same read-to-replace window are still last-writer-wins.
        /// </summary>
        public static void Update(string path, Func<string, string> transform, int waitMs = DefaultReplaceBudgetMs)
        {
            string existing = File.Exists(path) ? ReadAllText(path) : string.Empty;
            string updated = transform(existing);

            if (updated != null)
            {
                WriteAllText(path, updated, waitMs);
            }
        }

        public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes, int waitMs = DefaultReplaceBudgetMs)
        {
            using var stream = OpenWrite(path, waitMs);
            stream.ThrowOnFailure = true;
            stream.Write(bytes);
            stream.Commit();
        }

        /// <summary>
        /// A UTF-8 (no BOM) text writer onto a temp file. Disposing it replaces the target only if
        /// <paramref name="stream"/>.Commit() was called after the last write (see <see cref="OpenWrite"/>).
        /// </summary>
        public static StreamWriter CreateText(string path, out AtomicStream stream, int waitMs = DefaultReplaceBudgetMs)
        {
            stream = OpenWrite(path, waitMs);

            return new StreamWriter(stream, new UTF8Encoding(false));
        }

        /// <summary>
        /// A stream onto a temp file. The target is replaced when the stream is disposed AND Commit() was called; a stream
        /// disposed without Commit() (an exception unwound the writer) deletes the temp and leaves the target untouched.
        /// Creates the directory. Pass it to XmlTextWriter or StreamWriter, call Commit() after the last write, and let
        /// the writer dispose it.
        /// </summary>
        public static AtomicStream OpenWrite(string path, int waitMs = DefaultReplaceBudgetMs)
        {
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);

            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            return new AtomicStream(full, TempName(full), waitMs);
        }

        /// <summary>Instance-unique temp name beside the target (same volume, so the rename is atomic).</summary>
        internal static string TempName(string full)
            => full + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";

        /// <summary>
        /// Moves <paramref name="temp"/> over <paramref name="target"/>. Only a sharing violation is retried (a reader of
        /// the old file, Windows), for at most <paramref name="waitMs"/> in total, sleeps included; a read-only target, a missing directory
        /// or any error off Windows is permanent and fails at once.
        /// </summary>
        public static void Replace(string temp, string target, int waitMs = DefaultReplaceBudgetMs)
        {
            CopyUnixMode(temp, target);

            var clock = System.Diagnostics.Stopwatch.StartNew();

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, target, true);

                    return;
                }
                catch (Exception e) when (IsTransient(e, target, clock.ElapsedMilliseconds, waitMs, false) && clock.ElapsedMilliseconds < waitMs)
                {
                    // Windows refuses a rename over a file another instance has open, whatever its share flags; readers hold it
                    // only while they read a small file, so it lets go in a moment (ReplaceFile would get through, but it
                    // leaves ~RF*.TMP litter under contention)
                    // never sleeps past the budget: the last attempt happens at waitMs, so the total stays within it
                    Thread.Sleep((int)Math.Min(Math.Min(ReplaceDelayMs * attempt, 100), waitMs - clock.ElapsedMilliseconds));
                }
            }
        }

        // the temp is created with the default mode (umask), so a user who tightened the file (0600) would lose that on replace
        private static void CopyUnixMode(string temp, string target)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                if (File.Exists(target))
                {
                    File.SetUnixFileMode(temp, File.GetUnixFileMode(target));
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is PlatformNotSupportedException)
            {
                // best effort: the target is replaced with the default mode
            }
        }

        // Retried: ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33), another handle has the file open.
        // Access denied is permanent (read-only file, ACL) except for one case Windows cannot tell apart by code: a rename
        // over, or an open of, a file that another instance has open without FILE_SHARE_DELETE or is replacing right now
        // also reports ERROR_ACCESS_DENIED (the concurrent-writers test hits it). So access denied is retried only while
        // the file exists and is not read-only, and only for the first AccessDeniedWindowMs of the budget (the whole budget when the caller asked for a long wait, e.g. a background writer).
        internal const int AccessDeniedWindowMs = 100;

        private static bool IsTransient(Exception e, string file, long elapsedMs, int budgetMs, bool openingForRead)
        {
            if (!OperatingSystem.IsWindows() || e is DirectoryNotFoundException || e is FileNotFoundException)
            {
                return false;
            }

            if (e is IOException io)
            {
                return (io.HResult & 0xFFFF) is 32 or 33;
            }

            if (e is UnauthorizedAccessException && elapsedMs < (budgetMs > DefaultReplaceBudgetMs ? budgetMs : AccessDeniedWindowMs))
            {
                try
                {
                    return File.Exists(file) && (openingForRead || (File.GetAttributes(file) & FileAttributes.ReadOnly) == 0);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        public sealed class AtomicStream : FileStream
        {
            private readonly string _target;
            private readonly string _temp;
            private bool _done;
            private bool _commit;

            private readonly int _waitMs;

            internal AtomicStream(string target, string temp, int waitMs)
                : base(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None)
            {
                _target = target;
                _temp = temp;
                _waitMs = waitMs;
            }

            /// <summary>When the replace fails, Dispose throws instead of logging and giving up (set by WriteAllText/WriteAllBytes).</summary>
            internal bool ThrowOnFailure { get; set; }

            /// <summary>Marks the data complete: disposing now publishes it. Call after the last write (and after flushing any writer wrapped around it), never from a catch.</summary>
            public void Commit()
            {
                // surfaces a disk error here, while the write can still be abandoned
                Flush(true);
                _commit = true;
            }

            protected override void Dispose(bool disposing)
            {
                if (!disposing || _done)
                {
                    base.Dispose(disposing);

                    return;
                }

                _done = true;

                try
                {
                    base.Dispose(true);

                    if (_commit)
                    {
                        Replace(_temp, _target, _waitMs);

                        return;
                    }
                }
                catch (Exception e)
                {
                    DeleteTemp();

                    if (ThrowOnFailure)
                    {
                        throw;
                    }

                    // a save that cannot replace its target (reader holding it, read-only file) is dropped; the next save retries
                    Log.Error($"could not save {_target}: {e.Message}");

                    return;
                }

                DeleteTemp();
            }

            private void DeleteTemp()
            {
                try
                {
                    File.Delete(_temp);
                }
                catch
                {
                    // the temp is left behind; it is named per process, so it never clashes
                }
            }
        }
    }
}
