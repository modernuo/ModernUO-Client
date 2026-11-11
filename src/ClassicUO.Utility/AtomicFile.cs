// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;

namespace ClassicUO.Utility
{
    /// <summary>
    /// File access that is safe when several client instances share a file (profiles, settings, caches).
    /// Writers go to a temp file next to the target and replace the target in one rename, so a reader never sees a
    /// half-written file and a crash never leaves a truncated one. Readers open with FileShare.ReadWrite | Delete and
    /// the writer retries its rename for about a second while a reader (Windows) still has the old file open. Two instances saving the same file is last-writer-wins: no locking.
    /// </summary>
    public static class AtomicFile
    {
        internal const int ReplaceAttempts = 12;
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

        public static void WriteAllText(string path, string text)
        {
            using var stream = OpenWrite(path);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
        {
            using var stream = OpenWrite(path);
            stream.Write(bytes);
        }

        /// <summary>A UTF-8 (no BOM) text writer; disposing it replaces the target.</summary>
        public static StreamWriter CreateText(string path)
            => new StreamWriter(OpenWrite(path), new UTF8Encoding(false));

        /// <summary>
        /// A stream onto a temp file; disposing it (after the data is written) replaces <paramref name="path"/>.
        /// Creates the directory. Use with `using`; pass it to XmlTextWriter or StreamWriter, which close it.
        /// </summary>
        public static FileStream OpenWrite(string path)
        {
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);

            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            return new AtomicStream(full, TempName(full));
        }

        /// <summary>Instance-unique temp name beside the target (same volume, so the rename is atomic).</summary>
        internal static string TempName(string full)
            => full + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";

        /// <summary>Moves <paramref name="temp"/> over <paramref name="target"/>, retrying while a reader holds the target.</summary>
        internal static void Replace(string temp, string target)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, target, true);

                    return;
                }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < ReplaceAttempts)
                {
                    // Windows refuses a rename over a file another instance has open, whatever its share flags; readers hold it
                    // only while they read a small file, so it lets go in a moment (ReplaceFile would get through, but it
                    // leaves ~RF*.TMP litter under contention)
                    Thread.Sleep(ReplaceDelayMs * attempt);
                }
            }
        }

        private sealed class AtomicStream : FileStream
        {
            private readonly string _target;
            private readonly string _temp;
            private bool _done;

            public AtomicStream(string target, string temp)
                : base(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None)
            {
                _target = target;
                _temp = temp;
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);

                if (!disposing || _done)
                {
                    return;
                }

                _done = true;

                try
                {
                    Replace(_temp, _target);
                }
                catch
                {
                    try
                    {
                        File.Delete(_temp);
                    }
                    catch
                    {
                        // the temp is left behind; it is named per process, so it never clashes
                    }

                    throw;
                }
            }
        }
    }
}
