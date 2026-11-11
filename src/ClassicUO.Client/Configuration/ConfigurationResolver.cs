// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Configuration
{
    internal static class ConfigurationResolver
    {
        public static T Load<T>(string file, JsonTypeInfo<T> ctx) where T : class
        {
            if (!File.Exists(file))
            {
                Log.Warn(file + " not found.");

                return null;
            }

            var text = AtomicFile.ReadAllText(file);

            text = Regex.Replace
            (
                text,
                @"(?<!\\)  # lookbehind: Check that previous character isn't a \
                                                \\         # match a \
                                                (?!\\)     # lookahead: Check that the following character isn't a \",
                @"\\",
                RegexOptions.IgnorePatternWhitespace
            );

            return JsonSerializer.Deserialize(text, ctx);
        }

        public static void Save<T>(T obj, string file, JsonTypeInfo<T> ctx) where T : class
        {
            // Written to a temp file and renamed over the target, so concurrent instances (and a reader) never see a partial file.
            // Two instances saving the same file is last-writer-wins. The catch covers a rename that still fails after retries.
            try
            {
                var fileInfo = new FileInfo(file);

                if (fileInfo.Directory != null && !fileInfo.Directory.Exists)
                {
                    fileInfo.Directory.Create();
                }

                var json = JsonSerializer.Serialize(obj, ctx);
                AtomicFile.WriteAllText(file, json);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.Error(e.ToString());
            }
        }
    }
}