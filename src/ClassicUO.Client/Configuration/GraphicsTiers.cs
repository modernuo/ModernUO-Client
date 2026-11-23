// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.Configuration
{
    internal enum QualityTier
    {
        Low = 0,
        Medium = 1,
        High = 2
    }

    /// <summary>
    /// The one source mapping a feature's settings.json key to its quality tier (perf-budget-tiers plan, owner 2026-11-23). The
    /// Options "Graphics quality" preset and DevBench's set:quality=low|medium|high both apply it, and the quiet pass measures the
    /// presets as arms. Tier features are permanent player settings, not temporary ECS switches: when a row's key is baked in
    /// and removed from <see cref="Settings"/>, delete its row (GraphicsTiersTests fails until then).
    /// Rows are the ledger's decided tiers only; features whose tier is TBD (motion_above_display_rate, compat mipmaps,
    /// light_alpha_fade) are left out and so are not touched by a preset.
    /// </summary>
    internal static class GraphicsTiers
    {
        internal enum RowKind
        {
            /// <summary>A feature that costs frame time: on from its tier up (Low subset of Medium subset of High).</summary>
            Feature,

            /// <summary>A cost reducer: on at Low only.</summary>
            Reducer,

            /// <summary>A baked-in prerequisite: on at every tier.</summary>
            Always
        }

        internal sealed class Row
        {
            public string Key { get; }
            public RowKind Kind { get; }
            /// <summary>For a Feature: the lowest tier that turns it on.</summary>
            public QualityTier From { get; }
            public Func<Settings, bool> Get { get; }
            public Action<Settings, bool> Set { get; }
            public string Note { get; }

            public Row(string key, RowKind kind, QualityTier from, Func<Settings, bool> get, Action<Settings, bool> set, string note)
            {
                Key = key;
                Kind = kind;
                From = from;
                Get = get;
                Set = set;
                Note = note;
            }

            public bool ValueAt(QualityTier tier)
            {
                switch (Kind)
                {
                    case RowKind.Always: return true;
                    case RowKind.Reducer: return tier == QualityTier.Low;
                    default: return tier >= From;
                }
            }
        }

        public static readonly Row[] Rows =
        {
            new Row("light_range", RowKind.Reducer, QualityTier.Low, s => s.LightRange, (s, v) => s.LightRange = v, "Low lowers the light range"),
            new Row("view_margin", RowKind.Feature, QualityTier.High, s => s.ViewMargin, (s, v) => s.ViewMargin = v, "High (proposed Medium after bg/vm-legacy)"),
            new Row("light_occlusion", RowKind.Feature, QualityTier.High, s => s.LightOcclusion, (s, v) => s.LightOcclusion = v, "High"),
            new Row("light_batch", RowKind.Feature, QualityTier.High, s => s.LightBatch, (s, v) => s.LightBatch = v, "follows light_occlusion")
        };

        public static string Name(QualityTier tier) => tier switch
        {
            QualityTier.Low => "Low",
            QualityTier.Medium => "Medium",
            _ => "High"
        };

        /// <summary>low|medium|high (any case) to a tier.</summary>
        public static bool TryParse(string text, out QualityTier tier)
        {
            return Enum.TryParse(text?.Trim(), true, out tier) && Enum.IsDefined(tier);
        }

        /// <summary>Writes every tier row for the tier. The caller saves.</summary>
        public static void Apply(Settings settings, QualityTier tier)
        {
            foreach (Row row in Rows)
            {
                row.Set(settings, row.ValueAt(tier));
            }
        }

        public static bool Matches(Settings settings, QualityTier tier)
        {
            foreach (Row row in Rows)
            {
                if (row.Get(settings) != row.ValueAt(tier))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The preset the settings equal, or null for Custom. Tiers are told apart by their rows, so with every row
        /// equal at two tiers the lowest wins.</summary>
        public static QualityTier? Detect(Settings settings)
        {
            for (QualityTier t = QualityTier.Low; t <= QualityTier.High; t++)
            {
                if (Matches(settings, t))
                {
                    return t;
                }
            }

            return null;
        }
    }
}
