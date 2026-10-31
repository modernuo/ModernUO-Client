// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.IO;
using ClassicUO.Assets;
using System;
using System.Collections.Generic;

namespace ClassicUO.Game.Managers
{
    sealed class AnimatedStaticsManager
    {
        // Built once during Initialize from the static-data table and never mutated again.
        // Array (not List<T>) keeps the iteration tight: indexer returns ref via Span and
        // there's no spare-capacity overhead.
        private StaticAnimationInfo[] _staticInfos = Array.Empty<StaticAnimationInfo>();
        private uint _processTime;


        public void Initialize()
        {
            Initialize(Client.Game.UO.FileManager.TileData.StaticData, Client.Game.UO.FileManager.AnimData);
        }

        /// <summary>The registration itself, on explicit tables (tests run it on a loaded pack without a game).</summary>
        internal void Initialize(StaticTiles[] statics, AnimDataLoader animData)
        {
            var infos = new List<StaticAnimationInfo>();

            for (int i = 0; i < statics.Length; i++)
            {
                if (statics[i].IsAnimated)
                {
                    if (animData.TryGetAnimData((ushort)i, out _))
                    {
                        infos.Add
                        (
                            new StaticAnimationInfo
                            {
                                Index = (ushort)i,
                                IsField = StaticFilters.IsField((ushort)i)
                            }
                        );
                    }
                }
            }

            _staticInfos = infos.ToArray();
        }

        /// <summary>The registered static ids, in table order (for tests).</summary>
        internal IEnumerable<ushort> RegisteredIds
        {
            get
            {
                foreach (var info in _staticInfos)
                {
                    yield return info.Index;
                }
            }
        }

        public unsafe void Process()
        {
            if (_staticInfos.Length == 0 || _processTime >= Time.Ticks)
            {
                return;
            }

            var animData = Client.Game.UO.FileManager.AnimData;


            // fix static animations time to reflect the standard client
            uint delay = Constants.ITEM_EFFECT_ANIMATION_DELAY * 2;
            uint next_time = Time.Ticks + 250;
            bool no_animated_field = ProfileManager.CurrentProfile != null && ProfileManager.CurrentProfile.FieldsType != 0;
            ArtEntry[] static_data = Client.Game.UO.FileManager.Arts.Entries;

            for (int i = 0; i < _staticInfos.Length; i++)
            {
                ref StaticAnimationInfo o = ref _staticInfos[i];

                if (no_animated_field && o.IsField)
                {
                    o.AnimIndex = 0;

                    continue;
                }

                if (o.Time < Time.Ticks)
                {
                    animData.TryGetAnimData(o.Index, out var info);

                    byte offset = o.AnimIndex;

                    if (info.FrameInterval > 0)
                    {
                        o.Time = Time.Ticks + info.FrameInterval * delay + 1;
                    }
                    else
                    {
                        o.Time = Time.Ticks + delay;
                    }

                    if (offset < info.FrameCount && o.Index + 0x4000 < static_data.Length)
                    {
                        static_data[o.Index + 0x4000].AnimOffset = info.FrameData[offset++];
                    }

                    if (offset >= info.FrameCount)
                    {
                        offset = 0;
                    }

                    o.AnimIndex = offset;
                }

                if (o.Time < next_time)
                {
                    next_time = o.Time;
                }
            }

            _processTime = next_time;
        }


        private struct StaticAnimationInfo
        {
            public uint Time;
            public ushort Index;
            public byte AnimIndex;
            public bool IsField;
        }
    }
}