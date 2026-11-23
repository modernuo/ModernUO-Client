// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ClassicUO.Configuration;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    // setting <json key> [value]: writes one settings.json key of Settings.GlobalSettings for this session (never saved), so
    // the switch matrix (cuo-poc-state measure/matrix) can run arms that differ only in live switches in one process.
    // Without a value it logs the current one. Bools take on|off|true|false. Only the property is written: a switch whose
    // value is latched at startup or login (switches.tsv lifetime) keeps its latched value, and a CUO_* override of the
    // switch still wins over the setting in the switch's Enabled getter. DevBench's set:<key>=<value> plan entry calls it too, so
    // a quiet timing plan can alternate live switch arms in one process.
    internal sealed partial class EcsSessionDriver
    {
        internal static bool ExecuteSetting(string command, string[] args)
        {
            if (command != "setting" || args.Length == 0)
            {
                return false;
            }

            Settings settings = Settings.GlobalSettings;
            JsonPropertyInfo prop = null;

            foreach (JsonPropertyInfo p in SettingsJsonContext.RealDefault.Settings.Properties)
            {
                if (p.Name == args[0])
                {
                    prop = p;

                    break;
                }
            }

            if (settings == null || prop == null || prop.Get == null || prop.Set == null)
            {
                Log.Warn($"[ECS] driver: setting {args[0]}: no such settings.json key");

                return true;
            }

            if (args.Length > 1)
            {
                string raw = string.Join(' ', args, 1, args.Length - 1);

                try
                {
                    object value;

                    if (prop.PropertyType == typeof(string))
                    {
                        value = raw;
                    }
                    else
                    {
                        string json = prop.PropertyType == typeof(bool) ? raw switch { "on" => "true", "off" => "false", _ => raw } : raw;
                        value = JsonSerializer.Deserialize(json, prop.PropertyType, SettingsJsonContext.RealDefault);
                    }

                    prop.Set(settings, value);
                }
                catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
                {
                    Log.Warn($"[ECS] driver: setting {args[0]}: cannot read '{raw}' as {prop.PropertyType.Name}: {e.Message}");

                    return true;
                }
            }

            Log.Info($"[ECS] driver: setting {args[0]} = {JsonSerializer.Serialize(prop.Get(settings), prop.PropertyType, SettingsJsonContext.RealDefault)}");

            return true;
        }
    }
}
#endif
