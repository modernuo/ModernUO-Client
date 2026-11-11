// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;
using ClassicUO.Utility.Logging;
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

        /// <summary>Reads without blocking (or being blocked by) another instance replacing the file.</summary>
        public static FileStream OpenRead(string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && !(e is DirectoryNotFoundException) && attempt < 8)
                {
                    // another instance is mid-replace (a delete-pending target refuses opens); a file that is really
                    // missing or locked costs ~70 ms before the exception goes to the caller
                    Thread.Sleep(10);
                }
            }
        }

        /// <summary>
        /// Copies <paramref name="path"/> to <c>path.bak</c> (once: an existing backup is kept) before a save replaces a file
        /// the client failed to load, so the user's data survives a failed load followed by a save of defaults. Best effort.
        /// </summary>
        public static void KeepBackup(string path)
        {
            try
            {
                string bak = path + ".bak";

                if (File.Exists(path) && !File.Exists(bak))
                {
                    File.Copy(path, bak);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // nothing more to do; the save goes ahead
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
        /// the old file, Windows), for at most <paramref name="waitMs"/> in total; a read-only target, a missing directory
        /// or any error off Windows is permanent and fails at once.
        /// </summary>
        public static void Replace(string temp, string target, int waitMs = DefaultReplaceBudgetMs)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, target, true);

                    return;
                }
                catch (Exception e) when (IsTransient(e, target) && clock.ElapsedMilliseconds + ReplaceDelayMs <= waitMs)
                {
                    // Windows refuses a rename over a file another instance has open, whatever its share flags; readers hold it
                    // only while they read a small file, so it lets go in a moment (ReplaceFile would get through, but it
                    // leaves ~RF*.TMP litter under contention)
                    Thread.Sleep(Math.Min(ReplaceDelayMs * attempt, 100));
                }
            }
        }

        private static bool IsTransient(Exception e, string target)
        {
            if (!OperatingSystem.IsWindows() || e is DirectoryNotFoundException || e is FileNotFoundException)
            {
                return false;
            }

            if (e is UnauthorizedAccessException)
            {
                // a delete-pending target also reports access denied, but a read-only one never opens up
                try
                {
                    return File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReadOnly) == 0;
                }
                catch
                {
                    return false;
                }
            }

            return e is IOException;
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
