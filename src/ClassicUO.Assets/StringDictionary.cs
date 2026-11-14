// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using ClassicUO.Utility.Logging;
using Uo.Pack;

namespace ClassicUO.Assets;

public sealed class StringDictionaryLoader : UOFileLoader
{
    private string[] _strings = Array.Empty<string>();

    public StringDictionaryLoader(UOFileManager fileManager) : base(fileManager)
    {
    }

    public bool TryGetString(int index, out string str)
    {
        if (index < 0 || index >= _strings.Length)
        {
            str = string.Empty;
            return false;
        }

        str = _strings[index];
        return true;
    }

    /// <summary>The strings from the pack's text/stringdict.bin (string_dictionary.uop as the converter decoded it); none when the pack has no such file.</summary>
    public override void Load()
    {
        var path = FileManager.Pack?.PathOf(MiscConst.StringDictFile);
        if (path == null || !File.Exists(path))
            return;

        try
        {
            var entries = StringListFile.Read(path, MiscConst.MagicStringDict);
            var strings = new string[entries.Length];
            for (var i = 0; i < entries.Length; ++i)
                strings[i] = entries[i].Text;
            _strings = strings;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Log.Error($"String dictionary: cannot read '{path}' ({e.Message})");
        }
    }
}
