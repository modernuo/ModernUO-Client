// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using ClassicUO.Utility.Platforms;
using Microsoft.Xna.Framework;
using System;
using System.IO;
using System.Collections.Generic;
using System.Text;

namespace ClassicUO.Network
{
    sealed partial class PacketHandlers
    {
        /// <summary>event_loop_idle_wait: OPL requests wait to be sent (SendMegaClilocRequests sends a share per Update).</summary>
        internal static bool OplPending(World world)
            => world.ClientFeatures.TooltipsEnabled
                && (OplRequestQueue.Budget > 0 ? (Handler._oplQueue?.PendingCount ?? 0) != 0 : Handler._oplList.PendingCount != 0);

        public static void SendMegaClilocRequests(World world)
        {
            s_oplFramePackets = s_oplFrameSerials = 0;
            int budget = OplRequestQueue.Budget;

            if (world.ClientFeatures.TooltipsEnabled)
            {
                if (budget > 0)
                {
                    Handler._oplQueue?.Drain(Time.Ticks, budget, s_oplSink, out _);
                }
                else if (Handler._oplList.PendingCount != 0)
                {
                    Span<uint> batch = stackalloc uint[OplRequestQueue.MaxPerPacket];
                    int n;

                    // 15 serials per 0xD6 each frame; before 5.0.9 every pending serial, one 0xBF.10 each (SendOplBatch)
                    do
                    {
                        n = Handler._oplList.Take(batch, Time.Ticks);

                        if (n != 0)
                        {
                            SendOplBatch(batch.Slice(0, n));
                        }
                    }
                    while (n != 0 && Client.Game.UO.Version < Utility.ClientVersion.CV_5090);
                }
            }

#if CUO_DEVTOOLS
            if (OplSpreadCheck.On)
            {
                OplSpreadCheck.FrameEnd(Time.Ticks, s_oplFramePackets, s_oplFrameSerials, world.ClientFeatures.TooltipsEnabled, budget);
            }

            OplDebounceCheck.Tick(Time.Ticks, budget);
#endif

            if (Handler._customHouseRequests.Count > 0)
            {
                for (int i = 0; i < Handler._customHouseRequests.Count; ++i)
                {
                    NetClient.Socket.Send_CustomHouseDataRequest(Handler._customHouseRequests[i]);
                }

                Handler._customHouseRequests.Clear();
            }
        }

        // opl_request_budget: one 0xD6 per batch the queue hands out (a 0xBF.10 per serial before 5.0.9)
        private static void SendOplBatch(ReadOnlySpan<uint> serials)
        {
            if (Client.Game.UO.Version >= Utility.ClientVersion.CV_5090)
            {
                NetClient.Socket.Send_MegaClilocRequest(serials);
                s_oplFramePackets++;
            }
            else
            {
                foreach (uint serial in serials)
                {
                    NetClient.Socket.Send_MegaClilocRequest_Old(serial);
                }

                s_oplFramePackets += serials.Length;
            }

            s_oplFrameSerials += serials.Length;

#if CUO_DEVTOOLS
            if (OplSpreadCheck.On)
            {
                foreach (uint serial in serials)
                {
                    OplSpreadCheck.Sent(serial, Time.Ticks);
                }
            }

            if (OplDebounceCheck.On)
            {
                foreach (uint serial in serials)
                {
                    OplDebounceCheck.Sent(serial, Time.Ticks);
                }
            }
#endif
        }

        /// <summary>The queue behind <c>opl_request_budget</c> (created at the first request while the switch is on).</summary>
        internal static OplRequestQueue OplQueue => Handler._oplQueue;

        /// <summary>Tests: the old path's request list (switch off).</summary>
        internal static OplRequestList OldRequests => Handler._oplList;

        /// <summary>Tests: empties both request paths.</summary>
        internal static void ResetMegaClilocRequestsForTests()
        {
            Handler._oplList.Clear();
            Handler._oplQueue = null;
        }

        /// <summary>A hovered object without properties (OPL.Contains).</summary>
        public static void AddMegaClilocRequest(uint serial) => AddMegaClilocRequest(serial, OplRequestKind.Hover, 0);

        public static void AddMegaClilocRequest(uint serial, OplRequestKind kind, uint revision)
        {
#if CUO_DEVTOOLS
            if (OplSpreadCheck.On)
            {
                OplSpreadCheck.Demand(serial, kind, revision, Time.Ticks);
            }
#endif

            if (OplRequestQueue.Budget > 0)
            {
                Handler._oplQueue ??= new OplRequestQueue { DistanceOf = OplDistance };
                Handler._oplQueue.Request(serial, kind, revision, Time.Ticks);

                return;
            }

            Handler._oplList.Request(serial, kind, revision, Time.Ticks);
        }

        /// <summary>World removed the object (deleted, out of range): its queued request is dropped (opl_request_budget).</summary>
        public static void OnOplRemoved(uint serial)
        {
            Handler._oplQueue?.Remove(serial);
            Handler._oplList.Remove(serial);
#if CUO_DEVTOOLS
            if (OplSpreadCheck.On)
            {
                OplSpreadCheck.Removed(serial);
            }

            if (OplDebounceCheck.On)
            {
                OplDebounceCheck.Removed(serial);
            }
#endif
        }

        /// <summary>World cleared every property list (logout, reconnect).</summary>
        public static void OnOplCleared()
        {
            Handler._oplQueue?.Clear();
            Handler._oplList.Clear();
#if CUO_DEVTOOLS
            if (OplSpreadCheck.On)
            {
                OplSpreadCheck.Cleared();
            }

            if (OplDebounceCheck.On)
            {
                OplDebounceCheck.Cleared();
            }
#endif
        }

        // the queue's priority: the object's distance from the player, a contained item by its root's (-1: unknown)
        private static int OplDistance(uint serial)
        {
            World world = Client.Game?.UO?.World;

            if (world?.Player == null)
            {
                return -1;
            }

            Entity entity = world.Get(serial);

            if (entity is Item item && !item.OnGround)
            {
                uint root = item.RootContainer;
                entity = SerialHelper.IsValid(root) ? world.Get(root) : null;
            }

            return entity == null ? -1 : entity.Distance;
        }

        // Reused by MegaCliloc (main thread, not re-entrant): property lists for every new object in a crowded area
        // arrive at hundreds per second, and a new list per packet was one of the larger allocation sites there.
        private static readonly List<MegaClilocLine> _megaClilocLines = new List<MegaClilocLine>();

        // All line texts of the packet being parsed live in one reused buffer (a string per line was garbage as soon as
        // the joined property text existed); only the name and the joined text become strings. A few KB per client.
        private static char[] _megaClilocChars = new char[4096];

        private readonly struct MegaClilocLine
        {
            public readonly int Cliloc;
            public readonly int Start;
            public readonly int Length;
            public readonly int ArgCliloc;

            public MegaClilocLine(int cliloc, int start, int length, int argCliloc)
            {
                Cliloc = cliloc;
                Start = start;
                Length = length;
                ArgCliloc = argCliloc;
            }
        }

        private static void EnsureMegaChars(int needed)
        {
            if (needed > _megaClilocChars.Length)
            {
                Array.Resize(ref _megaClilocChars, Math.Max(needed, _megaClilocChars.Length * 2));
            }
        }

        private static void AppendMegaChars(ReadOnlySpan<char> text, ref int used)
        {
            EnsureMegaChars(used + text.Length);
            text.CopyTo(_megaClilocChars.AsSpan(used));
            used += text.Length;
        }

        // open + line + "</basefont>", in place
        private static void WrapMegaChars(int lineStart, string open, ref int used)
        {
            const string Close = "</basefont>";
            int length = used - lineStart;

            EnsureMegaChars(used + open.Length + Close.Length);
            _megaClilocChars.AsSpan(lineStart, length).CopyTo(_megaClilocChars.AsSpan(lineStart + open.Length));
            open.CopyTo(_megaClilocChars.AsSpan(lineStart));
            Close.CopyTo(_megaClilocChars.AsSpan(lineStart + open.Length + length));
            used = lineStart + open.Length + length + Close.Length;
        }

        // Same result as argument.Split('#', RemoveEmptyEntries) giving exactly two parts and parsing the second,
        // without the separator and result arrays.
        internal static int ParseArgCliloc(string argument) => ParseArgCliloc(argument.AsSpan());

        internal static int ParseArgCliloc(ReadOnlySpan<char> argument)
        {
            ReadOnlySpan<char> rest = argument;
            ReadOnlySpan<char> second = default;
            int parts = 0;

            while (true)
            {
                int i = rest.IndexOf('#');
                ReadOnlySpan<char> part = i < 0 ? rest : rest.Slice(0, i);

                if (!part.IsEmpty && ++parts == 2)
                {
                    second = part;
                }

                if (i < 0)
                {
                    break;
                }

                rest = rest.Slice(i + 1);
            }

            return parts == 2 && int.TryParse(second, out int value) ? value : 0;
        }

        private static void MegaCliloc(World world, ref NetReader p)
        {
            if (!world.InGame)
            {
                return;
            }

            ushort unknown = p.ReadUInt16();

            if (unknown > 1)
            {
                return;
            }

            uint serial = p.ReadUInt32();

            p.Skip(2);

            uint revision = p.ReadUInt32();

            Handler._oplQueue?.Answered(serial, revision);
            Handler._oplList.Answered(serial, revision);
#if CUO_DEVTOOLS
            if (OplSpreadCheck.On)
            {
                OplSpreadCheck.Answered(serial, revision);
            }

            if (OplDebounceCheck.On)
            {
                OplDebounceCheck.Answered(serial);
            }
#endif

            Entity entity = world.FindMobile(serial);

            if (entity == null)
            {
                if (SerialHelper.IsMobile(serial))
                {
                    Log.Warn("Searching a mobile into World.Items from MegaCliloc packet");
                }

                entity = world.FindItem(serial);
            }

            List<MegaClilocLine> list = _megaClilocLines;
            list.Clear();
            int used = 0;

            while (p.Position < p.Length)
            {
                int cliloc = (int)p.ReadUInt32();

                if (cliloc == 0)
                {
                    break;
                }

                ushort length = p.ReadUInt16();

                // The argument is only parsed, never kept: decode it into a stack buffer instead of a string per line.
                Span<char> argBuffer = length <= 512 ? stackalloc char[(length / 2) + 1] : new char[(length / 2) + 1];
                ReadOnlySpan<char> argument = length != 0
                    ? argBuffer.Slice(0, p.ReadLittleUni(length / 2, argBuffer))
                    : ReadOnlySpan<char>.Empty;

                int lineStart = used;

                if (argument.IsEmpty)
                {
                    AppendMegaChars(Client.Game.UO.FileManager.Clilocs.Translate(cliloc, string.Empty, true), ref used);
                }
                else
                {
                    ValueStringBuilder translated = Client.Game.UO.FileManager.Clilocs.TranslateToBuilder(cliloc, argument, true);
                    AppendMegaChars(translated.AsSpan(), ref used);
                    translated.Dispose();
                }

                int argcliloc = ParseArgCliloc(argument);

                // hardcoded colors lol
                switch (cliloc)
                {
                    case 1080418:
                        if (Client.Game.UO.Version >= Utility.ClientVersion.CV_60143)
                            WrapMegaChars(lineStart, "<basefont color=#40a4fe>", ref used);
                        break;
                    case 1061170:
                        if (int.TryParse(argument, out var strength) && world.PlayerState.Strength < strength)
                            WrapMegaChars(lineStart, "<basefont color=#FF0000>", ref used);
                        break;
                    case 1062613:
                        WrapMegaChars(lineStart, "<basefont color=#FFCC33>", ref used);
                        break;
                    case 1159561:
                        WrapMegaChars(lineStart, "<basefont color=#b66dff>", ref used);
                        break;
                }

                int lineLength = used - lineStart;

                for (int i = 0; i < list.Count; i++)
                {
                    MegaClilocLine other = list[i];

                    if (
                        other.Cliloc == cliloc
                        && _megaClilocChars.AsSpan(other.Start, other.Length).SequenceEqual(_megaClilocChars.AsSpan(lineStart, lineLength))
                    )
                    {
                        list.RemoveAt(i);

                        break;
                    }
                }

                list.Add(new MegaClilocLine(cliloc, lineStart, lineLength, argcliloc));
            }

            Item container = null;

            if (entity is Item it && SerialHelper.IsValid(it.Container))
            {
                container = world.FindItem(it.Container);
            }

            bool inBuyList = false;

            if (container != null)
            {
                inBuyList =
                    container.Layer == Layer.ShopBuy
                    || container.Layer == Layer.ShopBuyRestock
                    || container.Layer == Layer.ShopSell;
            }

            string name = string.Empty;
            string data = string.Empty;
            int namecliloc = 0;

            if (list.Count != 0)
            {
                MegaClilocLine nameLine = list[0];
                name = new string(_megaClilocChars, nameLine.Start, nameLine.Length);

                if (entity != null && !SerialHelper.IsMobile(serial))
                {
                    world.Writer.SetName(entity, name);
                    namecliloc = nameLine.ArgCliloc > 0 ? nameLine.ArgCliloc : nameLine.Cliloc;
                }

                if (list.Count > 1)
                {
                    int dataStart = used;

                    for (int i = 1; i < list.Count; i++)
                    {
                        MegaClilocLine line = list[i];

                        if (used > dataStart)
                        {
                            AppendMegaChars("\n", ref used);
                        }

                        AppendMegaChars(_megaClilocChars.AsSpan(line.Start, line.Length), ref used);
                    }

                    data = new string(_megaClilocChars, dataStart, used - dataStart);
                }
            }

            int[] clilocs = null;

            if (list.Count != 0)
            {
                clilocs = new int[list.Count];

                for (int i = 0; i < list.Count; i++)
                {
                    clilocs[i] = list[i].Cliloc;
                }
            }

            list.Clear();

            world.OPL.Add(serial, revision, name, data, namecliloc, clilocs);

            UIManager.GetGump<SpellbookGump>(serial)?.RequestUpdateContents();

            if (inBuyList && container != null && SerialHelper.IsValid(container.Serial))
            {
                UIManager.GetGump<ShopGump>(container.RootContainer)?.SetNameTo((Item)entity, name);
            }
        }

        private static void OPLInfo(World world, ref NetReader p)
        {
            if (world.ClientFeatures.TooltipsEnabled)
            {
                uint serial = p.ReadUInt32();
                uint revision = p.ReadUInt32();
                bool held = world.OPL.IsRevisionEquals(serial, revision);
#if CUO_DEVTOOLS
                if (OplDebounceCheck.On)
                {
                    OplDebounceCheck.Notice(serial, revision, held);
                }
#endif

                if (!held)
                {
                    AddMegaClilocRequest(serial, OplRequestKind.Revision, revision);
                }
            }
        }
    }
}
