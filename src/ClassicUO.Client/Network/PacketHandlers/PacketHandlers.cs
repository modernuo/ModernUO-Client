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
        public delegate void OnPacketBufferReader(World world, ref NetReader p);

        private static uint _requestedGridLoot;

        private readonly OplRequestList _oplList = new OplRequestList();
        private OplRequestQueue _oplQueue;
        private static readonly OplBatchSink s_oplSink = SendOplBatch;
        private static int s_oplFramePackets, s_oplFrameSerials;
        private List<uint> _customHouseRequests = new List<uint>();
        private readonly OnPacketBufferReader[] _handlers = new OnPacketBufferReader[0x100];

        public static PacketHandlers Handler { get; } = new PacketHandlers();

        public void Add(byte id, OnPacketBufferReader handler) => _handlers[id] = handler;

        private byte[] _readingBuffer = new byte[4096];
        private readonly PacketLogger _packetLogger = new PacketLogger();
        private readonly CircularBuffer _buffer = new CircularBuffer();

        // The queue, its generation and every change to them are guarded by _queueLock, which is never held while a packet
        // handler runs (a handler can send): packets are dequeued into a local buffer under the lock and dispatched after
        // it is released.
        private readonly object _queueLock = new object();

        // The connection generation the received-bytes queue belongs to.
#if CUO_DEVTOOLS
        private readonly DiagReceiveTimeline _diagReads = Diagnostics.On ? new() : null;
#endif
        private int _generation;
#if CUO_DEVTOOLS

        /// <summary>net-rework harness: the trace every dispatched packet is written to (null when no trace runs).</summary>
        internal Harness.NetTrace Trace;
#endif

        /// <summary>Times parsing stopped at a packet whose bytes had not all arrived yet (a header or body cut by a read).</summary>
        public int PartialPacketWaits { get; private set; }

        /// <summary>Bytes received but not yet dispatched (a packet split across reads). Main thread.</summary>
        internal int PendingBytes => _lastRing?.ReadableBytes ?? _buffer.Length;

        public int ParsePackets(NetClient socket, World world, Span<byte> data)
        {
            NetRing ring = socket.ReceiveRing;

            if (ring != null)
            {
                return ParseRing(socket, world, ring);
            }

            lock (_queueLock)
            {
                SyncGenerations(socket.ConnectionGeneration);

                if (!data.IsEmpty)
                {
                    _buffer.Enqueue(data);
#if CUO_DEVTOOLS
                    if (Diagnostics.On) _diagReads.Enqueue(data.Length, socket.DiagReadTimestamp);
#endif
                }
            }

            return ParseQueue(socket, world);
        }

        // net_ring: the ring the last parse read (PendingBytes)
        private NetRing _lastRing;

        // net_ring (NetRings): packets are framed in the receive ring, read in place by the handler and released after it
        // returns (only Drain writes the ring, and no handler drains). A connection change while a handler runs leaves the
        // packet unreleased: SyncRing drops it with the rest of the old stream.
        private int ParseRing(NetClient socket, World world, NetRing ring)
        {
            _lastRing = ring;
            socket.SyncRing();
            Span<byte> slice = socket.TakeRingSlice();
#if CUO_DEVTOOLS
            bool check = NetRings.CheckOn;

            if (check)
            {
                lock (_queueLock)
                {
                    SyncGenerations(socket.ConnectionGeneration);

                    if (!slice.IsEmpty)
                    {
                        _buffer.Enqueue(slice);
                        NetRings.CheckedSlices++;
                    }
                }
            }

            if (Diagnostics.On && !slice.IsEmpty) _diagReads.Enqueue(slice.Length, socket.DiagReadTimestamp);
#endif
            int packetsCount = 0;

            while (true)
            {
                socket.SyncRing();
                Span<byte> span = ring.GetReadSpan();

                if (span.IsEmpty)
                {
                    break;
                }

                byte packetID = span[0];
                int packetlength = socket.PacketsTable.GetPacketLength(packetID);
                int offset = 1;

                if (packetlength == -1)
                {
                    if (span.Length < 3)
                    {
                        PartialPacketWaits++;

                        break;
                    }

                    packetlength = (span[1] << 8) | span[2];
                    offset = 3;
                }

                if (packetlength < offset)
                {
                    Log.Error($"Malformed packet ID: {packetID:X2} | off: {offset} | len: {packetlength}; dropping the connection");
                    ring.DiscardReadable();
                    socket.ProtocolError();

                    break;
                }

                if (span.Length < packetlength)
                {
                    PartialPacketWaits++;

                    break;
                }

                Span<byte> packet = span.Slice(0, packetlength);
#if CUO_DEVTOOLS
                long diagRead = 0, diagDequeued = 0;
                int diagPingIndex = -1;

                if (check)
                {
                    CheckRingPacket(socket, packet);
                }

                if (Diagnostics.On)
                {
                    diagRead = _diagReads.Dequeue(packetlength);

                    if (packetlength == 2 && packet[0] == 0x73)
                    {
                        diagPingIndex = packet[1];
                        diagDequeued = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                }
#endif
                PacketLogger.Default?.Log(packet, false);
                int generation = socket.ConnectionGeneration;
                bool dispatched;
#if CUO_DEVTOOLS
                var previousDispatch = Diagnostics.On ? Diagnostics.EnterDispatch(socket.Statistics,
                    packetlength == 2 && packet[0] == 0x73 && packet[1] == diagPingIndex ? diagPingIndex : -1,
                    diagRead, diagDequeued) : default;
                var trace = Trace;
                trace?.BeginPacket(packet);
                try
                {
#endif
                    dispatched = AnalyzePacket(socket, world, packet, offset);
#if CUO_DEVTOOLS
                }
                finally
                {
                    trace?.EndPacket(world);
                    if (Diagnostics.On) Diagnostics.LeaveDispatch(previousDispatch);
                }
#endif
                if (socket.ConnectionGeneration == generation)
                {
                    ring.CommitRead(packetlength);
                }

                ++packetsCount;

                if (!dispatched)
                {
                    break;
                }
            }
#if CUO_DEVTOOLS

            if (check)
            {
                CheckRingLeftover(socket, ring);
            }

            NetRings.LogSummary();
            SpanBuffers.LogSummary();
#endif

            return packetsCount;
        }
#if CUO_DEVTOOLS

        // CUO_NET_RING_CHECK: the CircularBuffer path, fed the same slices, must frame the same packet next
        private void CheckRingPacket(NetClient socket, ReadOnlySpan<byte> packet)
        {
            lock (_queueLock)
            {
                SyncGenerations(socket.ConnectionGeneration);
                NetRings.CheckedPackets++;

                if (!GetPacketInfo(socket, _buffer, _buffer.Length, out byte id, out int offset, out int length) || length < offset || _buffer.Length < length)
                {
                    NetRings.Mismatch($"ring framed 0x{packet[0]:X2} ({packet.Length} bytes); the CircularBuffer path had no whole packet ({_buffer.Length} bytes queued)");
                    _buffer.Clear();

                    return;
                }

                while (length > _readingBuffer.Length)
                {
                    Array.Resize(ref _readingBuffer, _readingBuffer.Length * 2);
                }

                _buffer.Dequeue(_readingBuffer, 0, length);

                if (length != packet.Length || !packet.SequenceEqual(_readingBuffer.AsSpan(0, length)))
                {
                    NetRings.Mismatch($"ring framed 0x{packet[0]:X2} ({packet.Length} bytes); the CircularBuffer path framed 0x{id:X2} ({length} bytes) with {(length == packet.Length ? "different bytes" : "another length")}");
                }
            }
        }

        // after a parse both paths hold the same unframed tail, so a packet cannot land in a different frame
        private void CheckRingLeftover(NetClient socket, NetRing ring)
        {
            lock (_queueLock)
            {
                SyncGenerations(socket.ConnectionGeneration);
                socket.SyncRing();

                if (_buffer.Length != ring.ReadableBytes)
                {
                    NetRings.Mismatch($"after the parse the ring holds {ring.ReadableBytes} bytes, the CircularBuffer path {_buffer.Length}");
                    _buffer.Clear();
                }
            }
        }
#endif

        // A new connection (or the end of one) starts with empty queues: half a packet from the old stream must not be
        // glued to the first bytes of the next one. Must be called with _queueLock held.
        private void SyncGenerations(int generation)
        {
            if (_generation != generation)
            {
                _generation = generation;
                _buffer.Clear();
#if CUO_DEVTOOLS
                if (Diagnostics.On) _diagReads.Clear();
#endif
            }
        }

        private int ParseQueue(NetClient socket, World world)
        {
            var packetsCount = 0;
            var stream = _buffer;

            while (true)
            {
                byte[] packetBuffer;
                int packetlength;
                int offset;
                var malformed = false;
#if CUO_DEVTOOLS
                long diagRead = 0, diagDequeued = 0;
                int diagPingIndex = -1;
#endif

                lock (_queueLock)
                {
                    // the connection may have changed while a packet was handled (the 0x8C relay): what is left is old stream
                    SyncGenerations(socket.ConnectionGeneration);

                    if (stream.Length == 0)
                    {
                        break;
                    }

                    if (
                        !GetPacketInfo(
                            socket,
                            stream,
                            stream.Length,
                            out var packetID,
                            out offset,
                            out packetlength
                        )
                    )
                    {
                        // a variable-length header cut after its first or second byte: wait for the rest, like a cut body
                        PartialPacketWaits++;

                        break;
                    }

                    if (packetlength < offset)
                    {
                        // a length that can't even hold its own header (03 00 00): the stream is lost, and looping on it
                        // would never end
                        Log.Error($"Malformed packet ID: {packetID:X2} | off: {offset} | len: {packetlength}; dropping the connection");
                        stream.Clear();
                        malformed = true;
                    }
                    else if (stream.Length < packetlength)
                    {
                        // The packet continues in bytes not received yet (a TCP read, a 4 KB drain slice or the frame's
                        // drain cap ended inside it). It stays queued and is completed by the next ParsePackets; this is
                        // normal under load, so it is counted, not logged.
                        PartialPacketWaits++;

                        break;
                    }
                    else
                    {
                        while (packetlength > _readingBuffer.Length)
                        {
                            Array.Resize(ref _readingBuffer, _readingBuffer.Length * 2);
                        }

                        _ = stream.Dequeue(_readingBuffer, 0, packetlength);
#if CUO_DEVTOOLS
                        if (Diagnostics.On)
                        {
                            diagRead = _diagReads.Dequeue(packetlength);
                            if (packetlength == 2 && _readingBuffer[0] == 0x73)
                            {
                                diagPingIndex = _readingBuffer[1];
                                diagDequeued = System.Diagnostics.Stopwatch.GetTimestamp();
                            }
                        }
#endif
                    }

                    packetBuffer = _readingBuffer;
                }

                if (malformed)
                {
                    socket.ProtocolError();

                    break;
                }

                PacketLogger.Default?.Log(packetBuffer.AsSpan(0, packetlength), false);

                bool dispatched;
#if CUO_DEVTOOLS
                var previousDispatch = Diagnostics.On ? Diagnostics.EnterDispatch(socket.Statistics,
                    packetlength == 2 && packetBuffer[0] == 0x73 && packetBuffer[1] == diagPingIndex ? diagPingIndex : -1,
                    diagRead, diagDequeued) : default;
                var trace = Trace;
                trace?.BeginPacket(packetBuffer.AsSpan(0, packetlength));
                try
                {
#endif
                    dispatched = AnalyzePacket(socket, world, packetBuffer.AsSpan(0, packetlength), offset);
#if CUO_DEVTOOLS
                }
                finally
                {
                    trace?.EndPacket(world);
                    if (Diagnostics.On) Diagnostics.LeaveDispatch(previousDispatch);
                }
#endif
                ++packetsCount;

                if (!dispatched)
                {
                    break;
                }
            }
#if CUO_DEVTOOLS

            SpanBuffers.LogSummary();
#endif

            return packetsCount;
        }

        // False when the packet failed (span_reader: a read past its end) and the connection was dropped.
        private bool AnalyzePacket(NetClient socket, World world, ReadOnlySpan<byte> data, int offset)
        {
            if (data.IsEmpty)
                return true;

            var bufferReader = _handlers[data[0]];

            if (bufferReader != null)
            {
                var mode = SpanBuffers.Mode;
#if CUO_DEVTOOLS
                if (mode >= SpanBufferMode.CheckOld)
                {
                    SpanBuffers.Packet(data);
                }
#endif
                var buffer = new NetReader(data, mode);
                buffer.Seek(offset, SeekOrigin.Begin);

                if (mode is SpanBufferMode.Old or SpanBufferMode.CheckOld)
                {
                    bufferReader(world, ref buffer);

                    return true;
                }

                // span_reader: a packet is read as the server sent it, and a read past its end throws. Like ModernUO's
                // NetState.HandleReceive, the dispatch catches it once: the packet is logged and the connection dropped.
                try
                {
                    bufferReader(world, ref buffer);
                }
                catch (Exception e)
                {
                    Log.Error($"[net] packet 0x{data[0]:X2} length {data.Length} failed: {e.GetType().Name}: {e.Message}; bytes {HexDump(data)}{Environment.NewLine}{e.StackTrace}");
                    socket.ProtocolError($"Invalid packet 0x{data[0]:X2} ({data.Length} bytes) from the server");

                    return false;
                }
            }

            return true;
        }

        private static string HexDump(ReadOnlySpan<byte> data)
        {
            int n = Math.Min(data.Length, 4096);
            var sb = new StringBuilder(n * 2 + 16);

            for (int i = 0; i < n; i++)
            {
                sb.Append(data[i].ToString("X2"));
            }

            if (n < data.Length)
            {
                sb.Append($"..(+{data.Length - n} bytes)");
            }

            return sb.ToString();
        }

        // False when the header is not complete yet: a variable-length packet needs 3 bytes to know its length.
        private static bool GetPacketInfo(
            NetClient socket,
            CircularBuffer buffer,
            int bufferLen,
            out byte packetID,
            out int packetOffset,
            out int packetLen
        )
        {
            if (buffer == null || bufferLen <= 0)
            {
                packetID = 0xFF;
                packetLen = 0;
                packetOffset = 0;

                return false;
            }

            packetLen = socket.PacketsTable.GetPacketLength(packetID = buffer[0]);
            packetOffset = 1;

            if (packetLen == -1)
            {
                if (bufferLen < 3)
                {
                    return false;
                }

                var b0 = buffer[1];
                var b1 = buffer[2];

                packetLen = (b0 << 8) | b1;
                packetOffset = 3;
            }

            return true;
        }

        static PacketHandlers()
        {
            Handler.Add(0x1B, EnterWorld);
            Handler.Add(0x55, LoginComplete);
            Handler.Add(0xBD, ClientVersion);
            Handler.Add(0x03, ClientTalk);
            Handler.Add(0x0B, Damage);
            Handler.Add(0x11, CharacterStatus);
            Handler.Add(0x15, FollowR);
            Handler.Add(0x16, NewHealthbarUpdate);
            Handler.Add(0x17, NewHealthbarUpdate);
            Handler.Add(0x1A, UpdateItem);
            Handler.Add(0x1C, Talk);
            Handler.Add(0x1D, DeleteObject);
            Handler.Add(0x20, UpdatePlayer);
            Handler.Add(0x21, DenyWalk);
            Handler.Add(0x22, ConfirmWalk);
            Handler.Add(0x23, DragAnimation);
            Handler.Add(0x24, OpenContainer);
            Handler.Add(0x25, UpdateContainedItem);
            Handler.Add(0x27, DenyMoveItem);
            Handler.Add(0x28, EndDraggingItem);
            Handler.Add(0x29, DropItemAccepted);
            Handler.Add(0x2C, DeathScreen);
            Handler.Add(0x2D, MobileAttributes);
            Handler.Add(0x2E, EquipItem);
            Handler.Add(0x2F, Swing);
            Handler.Add(0x32, Unknown_0x32);
            Handler.Add(0x38, Pathfinding);
            Handler.Add(0x3A, UpdateSkills);
            Handler.Add(0x3B, CloseVendorInterface);
            Handler.Add(0x3C, UpdateContainedItems);
            Handler.Add(0x4E, PersonalLightLevel);
            Handler.Add(0x4F, LightLevel);
            Handler.Add(0x54, PlaySoundEffect);
            Handler.Add(0x56, MapData);
            Handler.Add(0x5B, SetTime);
            Handler.Add(0x65, SetWeather);
            Handler.Add(0x66, BookData);
            Handler.Add(0x6C, TargetCursor);
            Handler.Add(0x6D, PlayMusic);
            Handler.Add(0x6F, SecureTrading);
            Handler.Add(0x6E, CharacterAnimation);
            Handler.Add(0x70, GraphicEffect);
            Handler.Add(0x71, BulletinBoardData);
            Handler.Add(0x72, Warmode);
            Handler.Add(0x73, Ping);
            Handler.Add(0x74, BuyList);
            Handler.Add(0x77, UpdateCharacter);
            Handler.Add(0x78, UpdateObject);
            Handler.Add(0x7C, OpenMenu);
            Handler.Add(0x88, OpenPaperdoll);
            Handler.Add(0x89, CorpseEquipment);
            Handler.Add(0x90, DisplayMap);
            Handler.Add(0x93, OpenBook);
            Handler.Add(0x95, DyeData);
            Handler.Add(0x97, MovePlayer);
            Handler.Add(0x98, UpdateName);
            Handler.Add(0x99, MultiPlacement);
            Handler.Add(0x9A, ASCIIPrompt);
            Handler.Add(0x9E, SellList);
            Handler.Add(0xA1, UpdateHitpoints);
            Handler.Add(0xA2, UpdateMana);
            Handler.Add(0xA3, UpdateStamina);
            Handler.Add(0xA5, OpenUrl);
            Handler.Add(0xA6, TipWindow);
            Handler.Add(0xAA, AttackCharacter);
            Handler.Add(0xAB, TextEntryDialog);
            Handler.Add(0xAF, DisplayDeath);
            Handler.Add(0xAE, UnicodeTalk);
            Handler.Add(0xB0, OpenGump);
            Handler.Add(0xB2, ChatMessage);
            Handler.Add(0xB7, Help);
            Handler.Add(0xB8, CharacterProfile);
            Handler.Add(0xB9, EnableLockedFeatures);
            Handler.Add(0xBA, DisplayQuestArrow);
            Handler.Add(0xBB, UltimaMessengerR);
            Handler.Add(0xBC, Season);
            Handler.Add(0xBE, AssistVersion);
            Handler.Add(0xBF, ExtendedCommand);
            Handler.Add(0xC0, GraphicEffect);
            Handler.Add(0xC1, DisplayClilocString);
            Handler.Add(0xC2, UnicodePrompt);
            Handler.Add(0xC4, Semivisible);
            Handler.Add(0xC6, InvalidMapEnable);
            Handler.Add(0xC7, GraphicEffect);
            Handler.Add(0xC8, ClientViewRange);
            Handler.Add(0xCA, GetUserServerPingGodClientR);
            Handler.Add(0xCB, GlobalQueCount);
            Handler.Add(0xCC, DisplayClilocString);
            Handler.Add(0xD0, ConfigurationFileR);
            Handler.Add(0xD1, Logout);
            Handler.Add(0xD2, UpdateCharacter);
            Handler.Add(0xD3, UpdateObject);
            Handler.Add(0xD4, OpenBook);
            Handler.Add(0xD6, MegaCliloc);
            Handler.Add(0xD7, GenericAOSCommandsR);
            Handler.Add(0xD8, CustomHouse);
            Handler.Add(0xDB, CharacterTransferLog);
            Handler.Add(0xDC, OPLInfo);
            Handler.Add(0xDD, OpenCompressedGump);
            Handler.Add(0xDE, UpdateMobileStatus);
            Handler.Add(0xDF, BuffDebuff);
            Handler.Add(0xE2, NewCharacterAnimation);
            Handler.Add(0xE3, KREncryptionResponse);
            Handler.Add(0xE5, DisplayWaypoint);
            Handler.Add(0xE6, RemoveWaypoint);
            Handler.Add(0xF0, KrriosClientSpecial);
            Handler.Add(0xF3, UpdateItemSA);
            Handler.Add(0xF5, DisplayMap);
            Handler.Add(0xF6, BoatMoving);
            Handler.Add(0xF7, PacketList);

            // login
            Handler.Add(0xA8, ServerListReceived);
            Handler.Add(0x8C, ReceiveServerRelay);
            Handler.Add(0x86, UpdateCharacterList);
            Handler.Add(0xA9, ReceiveCharacterList);
            Handler.Add(0x82, ReceiveLoginRejection);
            Handler.Add(0x85, ReceiveLoginRejection);
            Handler.Add(0x53, ReceiveLoginRejection);
            Handler.Add(0xFD, LoginDelay);
        }

        private static void PacketList(World world, ref NetReader p)
        {
            if (world.Player == null)
            {
                return;
            }

            int count = p.ReadUInt16();

            for (int i = 0; i < count; i++)
            {
                byte id = p.ReadByte();

                if (id == 0xF3)
                {
                    UpdateItemSA(world, ref p);
                }
                else
                {
                    Log.Warn($"Unknown packet ID: [0x{id:X2}] in 0xF7");

                    break;
                }
            }
        }
    }
}
