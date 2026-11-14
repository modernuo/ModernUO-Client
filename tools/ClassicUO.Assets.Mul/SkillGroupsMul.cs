// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClassicUO.Assets
{
    /// <summary>
    /// Frozen copy of the file part of ClassicUO's SkillsGroupManager.LoadMULFile
    /// (src/ClassicUO.Client/Game/Managers/SkillsGroupManager.cs at 03b7df135, before skillgrp.mul moved into the pack):
    /// the same reads in the same order, stopping where the client starts building groups. The group names
    /// (groups 1..count−1) and the trailing group numbers are returned as read; the client applies its rules to them
    /// (group 0's name, numbers past the last group, the run-time skill count). The converter writes skillgrp.bin from it
    /// and verifies the pack against it.
    /// </summary>
    public static class SkillGroupsMul
    {
        /// <summary>
        /// Reads skillgrp.mul. Returns false (and logs nothing) where the client's LoadMULFile would have thrown before
        /// building any group or found no file, so it used its built-in groups: a missing file, a count it cannot
        /// allocate (0, or negative other than the unicode marker), a name or number that runs past the end of the file.
        /// </summary>
        public static bool TryRead(string path, out string[] names, out int[] assignments, out string error)
        {
            names = Array.Empty<string>();
            assignments = Array.Empty<int>();
            error = null;

            FileInfo info = new FileInfo(path);

            if (!info.Exists)
            {
                error = "missing";

                return false;
            }

            try
            {
                bool unicode = false;

                using (BinaryReader bin = new BinaryReader(File.OpenRead(info.FullName)))
                {
                    int start = 4;
                    int strlen = 17;
                    int count = bin.ReadInt32();

                    if (count == -1)
                    {
                        unicode = true;
                        count = bin.ReadInt32();
                        start *= 2;
                        strlen *= 2;
                    }

                    StringBuilder sb = new StringBuilder(17);

                    // new SkillsGroup[count]; groups[0] = g: throws for count <= 0
                    if (count <= 0)
                    {
                        error = $"group count {count}";

                        return false;
                    }

                    var n = new string[count - 1];

                    for (int i = 0; i < count - 1; ++i)
                    {
                        short strbuild;
                        bin.BaseStream.Seek(start + i * strlen, SeekOrigin.Begin);

                        if (unicode)
                        {
                            while ((strbuild = bin.ReadInt16()) != 0)
                            {
                                sb.Append((char) strbuild);
                            }
                        }
                        else
                        {
                            while ((strbuild = bin.ReadByte()) != 0)
                            {
                                sb.Append((char) strbuild);
                            }
                        }

                        n[i] = sb.ToString();

                        sb.Clear();
                    }

                    bin.BaseStream.Seek(start + (count - 1) * strlen, SeekOrigin.Begin);

                    var a = new List<int>();

                    while (bin.BaseStream.Length != bin.BaseStream.Position)
                    {
                        a.Add(bin.ReadInt32());
                    }

                    names = n;
                    assignments = a.ToArray();
                }
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is OverflowException || e is OutOfMemoryException)
            {
                error = e.Message;

                return false;
            }

            return true;
        }
    }
}
