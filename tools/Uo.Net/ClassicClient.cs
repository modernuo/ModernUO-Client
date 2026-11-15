using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Uo.Net;

/// <summary>
/// Classic UO protocol client (unencrypted; game-server Huffman decompression). Single-threaded:
/// call everything, including <see cref="Poll"/>, from one thread. Packet formats follow ModernUO's
/// Server/Network/Packets and UOContent/Network/Packets.
/// </summary>
public sealed class ClassicClient : IGameClient
{
    private const int MaxPendingMoves = 5;   // what an unmodified client allows; ModernUO flags more
    private const int RecvChunk = 16 * 1024;
    private const uint ClientIp = 0x7F000001;

    private readonly IWorldSink _sink;
    private readonly HuffmanDecoder _huffman = new();
    private PacketLengths _lengths = new(PacketLengths.Pack(7, 0, 103, 0));

    private Socket? _socket;
    private bool _compressed;
    private LoginOptions? _opt;
    private string _versionString = "";

    private byte[] _recv = [];
    private byte[] _in = [];
    private int _inLen;
    private byte[] _send = [];
    private char[] _text = [];
    private char[] _name = [];
    private EquipEntry[] _equip = [];

    // Movement
    private byte _seq;
    private readonly PendingMove[] _pending = new PendingMove[MaxPendingMoves];
    private int _pendingCount;

    // Ping
    private readonly long[] _pingSent = new long[256];
    private byte _pingSeq;
    private long _nextPing;

    private struct PendingMove
    {
        public byte Seq;
        public Direction Dir;
        public long Sent;
    }

    public ClassicClient(IWorldSink sink) => _sink = sink;

    public ClientState State { get; private set; }
    public uint PlayerSerial { get; private set; }
    public int PendingMoves => _pendingCount;
    public string? LastError { get; private set; }

    /// <summary>Optional raw packet tap, e.g. for --log-packets.</summary>
    public PacketTap? Tap { get; set; }

    /// <summary>Max bytes per socket read (testing knob: 1 exercises Huffman/packet reassembly across reads).</summary>
    public int ReceiveChunkSize { get; set; } = RecvChunk;

    public long BytesReceivedRaw { get; private set; }
    public long BytesReceivedDecompressed { get; private set; }
    public long PacketsReceived { get; private set; }
    public long PacketsSent { get; private set; }

    public void Connect(LoginOptions options)
    {
        if (State is not (ClientState.Disconnected or ClientState.Failed))
            throw new InvalidOperationException("Already connected.");

        _opt = options;
        _versionString = options.ClientVersion;
        var v = ParseVersion(options.ClientVersion);
        _lengths = new PacketLengths(PacketLengths.Pack(v.a, v.b, v.c, v.d));

        _recv = ArrayPool<byte>.Shared.Rent(RecvChunk);
        _in = ArrayPool<byte>.Shared.Rent(256 * 1024);
        _send = ArrayPool<byte>.Shared.Rent(4096);
        _text = ArrayPool<char>.Shared.Rent(8192);
        _name = ArrayPool<char>.Shared.Rent(64);
        _equip = ArrayPool<EquipEntry>.Shared.Rent(256);
        _inLen = 0;
        _compressed = false;
        LastError = null;

        SetState(ClientState.LoginConnecting, options.Host);

        if (!OpenSocket(Resolve(options.Host), options.Port))
            return;

        // 0xEF seed: seed + client version (ModernUO uses this version for the whole login).
        var w = new PacketWriter(_send);
        w.U8(0xEF);
        w.U32(ClientIp);
        w.U32((uint)v.a);
        w.U32((uint)v.b);
        w.U32((uint)v.c);
        w.U32((uint)v.d);
        Send(w.Written);

        // 0x80 account login
        w = new PacketWriter(_send);
        w.U8(0x80);
        w.Latin1Fixed(options.Username, 30);
        w.Latin1Fixed(options.Password, 30);
        w.U8(0xFF);
        Send(w.Written);

        SetState(ClientState.AwaitingServerList, default);
    }

    public int Poll(int waitMs = 0)
    {
        var socket = _socket;
        if (socket == null)
            return 0;

        var dispatched = 0;

        try
        {
            if (waitMs > 0 && socket.Available == 0)
                socket.Poll(waitMs * 1000, SelectMode.SelectRead);

            while (_socket == socket)
            {
                var n = socket.Receive(_recv.AsSpan(0, Math.Clamp(ReceiveChunkSize, 1, RecvChunk)), SocketFlags.None, out var err);

                if (err == SocketError.WouldBlock)
                    break;

                if (err != SocketError.Success || n == 0)
                {
                    Fail(err == SocketError.Success ? "Server closed the connection." : "Socket error: " + err);
                    break;
                }

                BytesReceivedRaw += n;
                Append(_recv.AsSpan(0, n));
                dispatched += ParseBuffer();
            }
        }
        catch (ObjectDisposedException)
        {
            // closed during dispatch (relay or Disconnect)
        }

        if (State == ClientState.InWorld && Stopwatch.GetTimestamp() - _nextPing >= 0)
            SendPing();

        return dispatched;
    }

    public int RequestMove(Direction direction)
    {
        if (State != ClientState.InWorld || _pendingCount >= MaxPendingMoves)
            return -1;

        var seq = _seq;
        Span<byte> p = stackalloc byte[7];
        p[0] = 0x02;
        p[1] = (byte)direction;
        p[2] = seq;
        BinaryPrimitives.WriteUInt32BigEndian(p[3..], 0); // fast-walk key: unused by ModernUO
        _pending[_pendingCount++] = new PendingMove { Seq = seq, Dir = direction, Sent = Stopwatch.GetTimestamp() };
        _seq = seq == 255 ? (byte)1 : (byte)(seq + 1);
        Send(p);
        return seq;
    }

    public void Say(ReadOnlySpan<char> text)
    {
        if (State != ClientState.InWorld)
            return;

        // 0xAD unicode speech; type 0 (regular), no keyword encoding.
        var w = new PacketWriter(_send);
        w.U8(0xAD);
        w.U16(0);
        w.U8(0x00);
        w.U16(0x02B2);
        w.U16(3);
        w.Latin1Fixed("ENU", 4);
        w.UnicodeBEZ(text[..Math.Min(text.Length, 1000)]);
        w.FixLength();
        Send(w.Written);
    }

    public void Disconnect()
    {
        CloseSocket();
        SetState(ClientState.Disconnected, "client disconnect");
    }

    public void Dispose()
    {
        CloseSocket();
        Return(ref _recv);
        Return(ref _in);
        Return(ref _send);
        if (_text.Length > 0) { ArrayPool<char>.Shared.Return(_text); _text = []; }
        if (_name.Length > 0) { ArrayPool<char>.Shared.Return(_name); _name = []; }
        if (_equip.Length > 0) { ArrayPool<EquipEntry>.Shared.Return(_equip); _equip = []; }
    }

    // ---------------------------------------------------------------- transport

    private bool OpenSocket(IPAddress address, int port)
    {
        var s = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (!s.ConnectAsync(address, port).Wait(TimeSpan.FromSeconds(5)))
                throw new SocketException((int)SocketError.TimedOut);
        }
        catch (Exception e)
        {
            s.Dispose();
            Fail($"Connect to {address}:{port} failed: {(e as AggregateException)?.InnerException?.Message ?? e.Message}");
            return false;
        }

        s.Blocking = false;
        _socket = s;
        return true;
    }

    private void CloseSocket()
    {
        var s = _socket;
        _socket = null;
        if (s == null) return;
        try { s.Shutdown(SocketShutdown.Both); } catch { /* already reset */ }
        s.Dispose();
    }

    private void Send(ReadOnlySpan<byte> packet)
    {
        var s = _socket;
        if (s == null) return;
        Tap?.Invoke(true, packet, true);
        PacketsSent++;

        // Small packets on a non-blocking socket; spin on WouldBlock (the send buffer is never full in practice).
        while (!packet.IsEmpty)
        {
            var n = s.Send(packet, SocketFlags.None, out var err);
            if (err == SocketError.WouldBlock) { Thread.Yield(); continue; }
            if (err != SocketError.Success) { Fail("Send failed: " + err); return; }
            packet = packet[n..];
        }
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (!_compressed)
        {
            EnsureIn(data.Length);
            data.CopyTo(_in.AsSpan(_inLen));
            _inLen += data.Length;
            BytesReceivedDecompressed += data.Length;
            return;
        }

        while (!data.IsEmpty)
        {
            EnsureIn(data.Length * 4 + 64); // shortest code is 2 bits: at most 4x expansion
            var written = _huffman.Decompress(data, _in.AsSpan(_inLen), out var consumed);
            _inLen += written;
            BytesReceivedDecompressed += written;
            data = data[consumed..];
        }
    }

    private void EnsureIn(int extra)
    {
        if (_inLen + extra <= _in.Length) return;
        var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_in.Length * 2, _inLen + extra));
        _in.AsSpan(0, _inLen).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(_in);
        _in = bigger;
    }

    private int ParseBuffer()
    {
        var off = 0;
        var count = 0;

        while (off < _inLen && _socket != null)
        {
            var id = _in[off];
            int len = _lengths[id];

            if (len < 0)
            {
                if (_inLen - off < 3) break;
                len = BinaryPrimitives.ReadUInt16BigEndian(_in.AsSpan(off + 1));
                if (len < 3) { Fail($"Bad length {len} for packet 0x{id:X2}"); return count; }
            }
            else if (len == 0)
            {
                Fail($"Unknown packet 0x{id:X2}");
                return count;
            }

            if (_inLen - off < len) break;

            var socketBefore = _socket;
            Dispatch(_in.AsSpan(off, len));
            off += len;
            count++;

            // The relay (or a failure) swaps sockets: anything left from the old socket is discarded.
            if (_socket != socketBefore) { _inLen = 0; return count; }
        }

        if (off > 0)
        {
            _in.AsSpan(off, _inLen - off).CopyTo(_in);
            _inLen -= off;
        }

        return count;
    }

    // ---------------------------------------------------------------- dispatch

    private static bool IsHandled(ReadOnlySpan<byte> p) => p[0] switch
    {
        0x82 or 0x53 or 0xA8 or 0x8C or 0xB9 or 0xA9 or 0x86 or 0xBD or 0x1B or 0x55 or 0x20 or 0x77 or 0x78
            or 0xF3 or 0x1A or 0x2E or 0x1D or 0x22 or 0x21 or 0x73 or 0x1C or 0xAE or 0xC1 or 0xCC => true,
        0xBF => p.Length >= 5 && BinaryPrimitives.ReadUInt16BigEndian(p[3..]) == 0x08,
        _ => false,
    };

    private void Dispatch(ReadOnlySpan<byte> p)
    {
        PacketsReceived++;
        Tap?.Invoke(false, p, IsHandled(p));
        var r = new PacketReader(p, 1);

        switch (p[0])
        {
            // ---- login
            case 0x82: // login rejected
                Fail("Account login rejected, reason " + p[1]);
                break;
            case 0x53: // popup message (e.g. character already in world)
                Fail("Server popup message " + p[1]);
                break;
            case 0xA8: OnServerList(ref r); break;
            case 0x8C: OnRelay(ref r); break;
            case 0xB9: break; // supported features
            case 0xA9: OnCharacterList(ref r); break;
            case 0x86: break; // character list update
            case 0xBD: SendClientVersion(); break;

            // ---- world entry
            case 0x1B: OnLoginConfirm(ref r); break;
            case 0x55:
                SetState(ClientState.InWorld, default);
                _nextPing = Stopwatch.GetTimestamp();
                _sink.OnLoginComplete();
                break;

            // ---- entities
            case 0x20: OnMobileUpdate(ref r); break;
            case 0x77: OnMobileMoving(ref r); break;
            case 0x78: OnMobileIncoming(ref r); break;
            case 0xF3: OnWorldObject(ref r); break;
            case 0x1A: OnWorldItem(ref r); break;
            case 0x2E: OnEquip(ref r); break;
            case 0x1D: _sink.OnRemove(r.U32()); break;
            case 0xBF: OnExtended(ref r); break;

            // ---- movement
            case 0x22: OnMoveAck(ref r); break;
            case 0x21: OnMoveReject(ref r); break;
            case 0x73:
                {
                    var seq = r.U8();
                    var sent = _pingSent[seq];
                    if (sent != 0)
                    {
                        _pingSent[seq] = 0;
                        _sink.OnPing(seq, (Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency);
                    }
                    break;
                }

            // ---- text
            case 0x1C: OnAsciiMessage(ref r); break;
            case 0xAE: OnUnicodeMessage(ref r); break;
            case 0xC1:
                {
                    var serial = r.U32();
                    r.Skip(2 + 1 + 2 + 2);
                    _sink.OnClilocMessage(serial, r.I32());
                    break;
                }
            case 0xCC:
                {
                    var serial = r.U32();
                    r.Skip(2 + 1 + 2 + 2);
                    _sink.OnClilocMessage(serial, r.I32());
                    break;
                }

        }
    }

    private void OnServerList(ref PacketReader r)
    {
        r.Skip(2 + 1);
        var count = r.U16();
        if (count == 0) { Fail("Empty server list."); return; }
        var index = r.U16(); // first entry
        SetState(ClientState.AwaitingRelay, default);

        Span<byte> p = [0xA0, (byte)(index >> 8), (byte)index];
        Send(p);
    }

    private void OnRelay(ref PacketReader r)
    {
        var ip = r.Bytes(4);
        var address = new IPAddress(ip);
        var port = r.U16();
        var key = r.U32();

        CloseSocket();
        SetState(ClientState.GameConnecting, default);

        var opt = _opt!;
        var target = opt.UseRelayAddress && !address.Equals(IPAddress.Any) ? address : Resolve(opt.Host);
        if (!OpenSocket(target, port))
        {
            if (target.Equals(Resolve(opt.Host)) || !OpenSocket(Resolve(opt.Host), port))
                return;
            LastError = null;
            SetState(ClientState.GameConnecting, "relay address unreachable, using login host");
        }

        _inLen = 0;
        _compressed = true; // everything the game server sends is Huffman-compressed
        _huffman.Reset();

        // The first 4 bytes on the game connection are the relay key (raw, no packet id).
        Span<byte> seed = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(seed, key);
        Send(seed);

        var w = new PacketWriter(_send);
        w.U8(0x91);
        w.U32(key);
        w.Latin1Fixed(opt.Username, 30);
        w.Latin1Fixed(opt.Password, 30);
        Send(w.Written);

        SetState(ClientState.AwaitingCharacterList, default);
    }

    private void OnCharacterList(ref PacketReader r)
    {
        var opt = _opt!;
        r.Skip(2);
        int slots = r.U8();
        var pick = -1;
        var firstUsed = -1;
        var firstEmpty = -1;
        var name = _name.AsSpan(0, 30);
        var pickName = 0;

        for (var i = 0; i < slots; i++)
        {
            var len = r.Latin1(30, name);
            r.Skip(30); // password
            if (len == 0) { if (firstEmpty < 0) firstEmpty = i; continue; }
            if (firstUsed < 0) firstUsed = i;
            if (opt.CharacterName != null && name[..len].Equals(opt.CharacterName, StringComparison.OrdinalIgnoreCase))
            {
                pick = i;
                pickName = len;
            }
        }

        int cities = r.U8();
        var perCity = cities > 0 ? (r.Remaining - 6) / cities : 0;
        var newFormat = perCity == 89; // 7.0.13+: 32-char names + coords + cliloc
        var textLen = newFormat ? 32 : 31;
        var cityIndex = 0;
        Span<char> city = stackalloc char[32];
        for (var i = 0; i < cities; i++)
        {
            var idx = r.U8();
            var len = r.Latin1(textLen, city);
            r.Skip(textLen + (newFormat ? 4 * 5 + 4 : 0));
            if (city[..len].Equals(opt.NewCharacterCity, StringComparison.OrdinalIgnoreCase))
                cityIndex = idx;
        }

        if (pick < 0 && opt.CharacterName == null && firstUsed >= 0)
        {
            pick = firstUsed;
        }

        SetState(ClientState.EnteringWorld, default);

        if (pick >= 0)
        {
            // Re-read the chosen name (the buffer was reused by the loop).
            var nr = new PacketReader(r.Span, 4 + pick * 60);
            pickName = nr.Latin1(30, name);
            SendSelectCharacter(pick, name[..pickName]);
        }
        else
        {
            if (cities == 0) { Fail("No characters and no starting cities."); return; }
            if (firstEmpty < 0) { Fail("No free character slot."); return; }
            SendCreateCharacter(opt.CharacterName ?? opt.NewCharacterName, cityIndex, firstEmpty);
        }
    }

    private void SendSelectCharacter(int slot, ReadOnlySpan<char> name)
    {
        var w = new PacketWriter(_send);
        w.U8(0x5D);
        w.U32(0xEDEDEDED);
        w.Latin1Fixed(name, 30);
        w.Zero(2);
        w.U32(0x3F); // client flags: Felucca..TerMur
        w.Zero(24);
        w.U32((uint)slot);
        w.U32(ClientIp);
        Send(w.Written);
    }

    private void SendCreateCharacter(string name, int cityIndex, int slot)
    {
        // 0xF8 (7.0.16+), 106 bytes. ModernUO overrides stats/skills from the profession preset.
        var w = new PacketWriter(_send);
        w.U8(0xF8);
        w.U32(0xEDEDEDED);
        w.U32(0xFFFFFFFF);
        w.U8(0);
        w.Latin1Fixed(name, 30);
        w.Zero(2);
        w.U32(0x3F);       // client flags
        w.U32(1);
        w.U32(0);
        w.U8(1);           // profession: warrior
        w.Zero(15);
        w.U8(2);           // 7.0+: human male
        w.U8(45); w.U8(35); w.U8(10);           // str dex int
        w.U8(40); w.U8(50);                     // swords
        w.U8(27); w.U8(50);                     // tactics
        w.U8(1); w.U8(20);                      // anatomy
        w.U8(17); w.U8(0);                      // healing
        w.U16(0x03EA);     // skin hue
        w.U16(0x203B); w.U16(0x044E);           // short hair
        w.U16(0); w.U16(0);                     // no beard
        w.U16((ushort)cityIndex);
        w.Zero(2);
        w.U16((ushort)slot);
        w.U32(ClientIp);
        w.U16(0); w.U16(0);                     // shirt / pants hue
        Debug.Assert(w.Length == 106);
        Send(w.Written);
    }

    private void SendClientVersion()
    {
        var w = new PacketWriter(_send);
        w.U8(0xBD);
        w.U16(0);
        w.Latin1Z(_versionString);
        w.FixLength();
        Send(w.Written);
    }

    private void SendPing()
    {
        var seq = ++_pingSeq;
        _pingSent[seq] = Stopwatch.GetTimestamp();
        _nextPing = _pingSent[seq] + (long)(_opt!.PingInterval.TotalSeconds * Stopwatch.Frequency);
        Span<byte> p = [0x73, seq];
        Send(p);
    }

    private void OnLoginConfirm(ref PacketReader r)
    {
        var serial = r.U32();
        r.Skip(4);
        var body = r.U16();
        var x = r.U16();
        var y = r.U16();
        var z = r.I16();
        var dir = (Direction)r.U8();
        r.Skip(1 + 4 + 4);
        var w = r.U16();
        var h = r.U16();
        PlayerSerial = serial;
        ResetMoves(MoveResetReason.PlayerUpdate, report: false);
        _sink.OnLoginConfirm(new PlayerInfo
        {
            Serial = serial, Body = body, X = x, Y = y, Z = (sbyte)z, Direction = dir, MapWidth = w, MapHeight = h,
        });
    }

    private void OnMobileUpdate(ref PacketReader r)
    {
        var serial = r.U32();
        var body = r.U16();
        r.Skip(1);
        var hue = r.U16();
        var flags = r.U8();
        var x = r.U16();
        var y = r.U16();
        r.Skip(2);
        var dir = (Direction)r.U8();
        var z = r.I8();
        var isPlayer = serial == PlayerSerial;

        // ModernUO resets its movement sequence whenever it sends 0x20 for the player (teleport, login).
        if (isPlayer) ResetMoves(MoveResetReason.PlayerUpdate, report: true);

        _sink.OnMobile(new MobileState
        {
            Serial = serial, Body = body, X = x, Y = y, Z = z, Direction = dir, Hue = hue, Flags = flags,
            IsPlayer = isPlayer, Source = MobileSource.Update20,
        }, default);
    }

    private void OnMobileMoving(ref PacketReader r)
    {
        var serial = r.U32();
        var body = r.U16();
        var x = r.U16();
        var y = r.U16();
        var z = r.I8();
        var dir = (Direction)r.U8();
        var hue = r.U16();
        var flags = r.U8();
        var noto = r.U8();
        _sink.OnMobile(new MobileState
        {
            Serial = serial, Body = body, X = x, Y = y, Z = z, Direction = dir, Hue = hue, Flags = flags,
            Notoriety = noto, IsPlayer = serial == PlayerSerial, Source = MobileSource.Moving77,
        }, default);
    }

    private void OnMobileIncoming(ref PacketReader r)
    {
        r.Skip(2);
        var serial = r.U32();
        var body = r.U16();
        var x = r.U16();
        var y = r.U16();
        var z = r.I8();
        var dir = (Direction)r.U8();
        var hue = r.U16();
        var flags = r.U8();
        var noto = r.U8();

        // 7.0.33+: every equipment entry carries a hue; the list ends with serial 0.
        var n = 0;
        while (r.Remaining >= 4 && n < _equip.Length)
        {
            var itemSerial = r.U32();
            if (itemSerial == 0) break;
            var itemId = r.U16();
            var layer = r.U8();
            var itemHue = r.U16();
            _equip[n++] = new EquipEntry { Serial = itemSerial, ItemId = itemId, Layer = layer, Hue = itemHue };
        }

        _sink.OnMobile(new MobileState
        {
            Serial = serial, Body = body, X = x, Y = y, Z = z, Direction = dir, Hue = hue, Flags = flags,
            Notoriety = noto, IsPlayer = serial == PlayerSerial, Source = MobileSource.Incoming78,
        }, _equip.AsSpan(0, n));
    }

    private void OnWorldObject(ref PacketReader r)
    {
        r.Skip(2); // command 0x0001
        var kind = (ItemKind)r.U8();
        var serial = r.U32();
        var gfx = r.U16();
        r.Skip(1); // graphic increment
        var amount = r.U16();
        r.Skip(2); // amount (max)
        var x = r.U16();
        var y = r.U16();
        var z = r.I8();
        var light = r.U8();
        var hue = r.U16();
        var flags = r.U8();

        if (kind == ItemKind.Mobile)
        {
            _sink.OnMobile(new MobileState
            {
                Serial = serial, Body = gfx, X = (ushort)(x & 0x7FFF), Y = (ushort)(y & 0x3FFF), Z = z,
                Direction = (Direction)light, Hue = hue, Flags = flags, IsPlayer = serial == PlayerSerial,
                Source = MobileSource.WorldObjF3,
            }, default);
            return;
        }

        _sink.OnItem(new ItemState
        {
            Serial = serial, ItemId = gfx, Amount = amount, X = (ushort)(x & 0x7FFF), Y = (ushort)(y & 0x3FFF), Z = z,
            Direction = light, Hue = hue, Flags = flags, Kind = kind,
        });
    }

    private void OnWorldItem(ref PacketReader r)
    {
        r.Skip(2);
        var serial = r.U32();
        var itemId = r.U16();
        ushort amount = 1;
        if ((serial & 0x80000000) != 0) amount = r.U16();
        serial &= 0x7FFFFFFF;
        var x = r.U16();
        var y = r.U16();
        byte dir = 0;
        if ((x & 0x8000) != 0) dir = r.U8();
        var z = r.I8();
        ushort hue = 0;
        if ((y & 0x8000) != 0) hue = r.U16();
        byte flags = 0;
        if ((y & 0x4000) != 0) flags = r.U8();
        var multi = (itemId & 0x4000) != 0;

        _sink.OnItem(new ItemState
        {
            Serial = serial, ItemId = (ushort)(itemId & 0x3FFF), Amount = amount, X = (ushort)(x & 0x7FFF),
            Y = (ushort)(y & 0x3FFF), Z = z, Direction = dir, Hue = hue, Flags = flags,
            Kind = multi ? ItemKind.Multi : ItemKind.Item,
        });
    }

    private void OnEquip(ref PacketReader r)
    {
        var serial = r.U32();
        var itemId = r.U16();
        r.Skip(1);
        var layer = r.U8();
        var parent = r.U32();
        var hue = r.U16();
        _sink.OnEquip(new EquipState { Serial = serial, ItemId = itemId, Layer = layer, Parent = parent, Hue = hue });
    }

    private void OnExtended(ref PacketReader r)
    {
        r.Skip(2);
        var sub = r.U16();
        switch (sub)
        {
            case 0x08:
                var map = r.U8();
                ResetMoves(MoveResetReason.MapChange, report: State == ClientState.InWorld);
                _sink.OnMapChange(map);
                break;
        }
    }

    private void OnMoveAck(ref PacketReader r)
    {
        var seq = r.U8();
        var noto = r.U8();
        var now = Stopwatch.GetTimestamp();

        var i = IndexOfPending(seq);
        if (i < 0)
        {
            // Ack for a request dropped by a sequence reset.
            _sink.OnMoveResult(new MoveResult { Sequence = seq, Accepted = true, Notoriety = noto, ReceivedTimestamp = now, SentTimestamp = now, StillPending = _pendingCount });
            return;
        }

        var pm = _pending[i];
        RemovePendingThrough(i);
        _sink.OnMoveResult(new MoveResult
        {
            Sequence = seq, Direction = pm.Dir, Accepted = true, Notoriety = noto,
            SentTimestamp = pm.Sent, ReceivedTimestamp = now, StillPending = _pendingCount,
        });
    }

    private void OnMoveReject(ref PacketReader r)
    {
        var seq = r.U8();
        var x = r.U16();
        var y = r.U16();
        var dir = (Direction)r.U8();
        var z = r.I8();
        var now = Stopwatch.GetTimestamp();

        var i = IndexOfPending(seq);
        var pm = i >= 0 ? _pending[i] : new PendingMove { Seq = seq, Sent = now };
        var dropped = _pendingCount - (i >= 0 ? 1 : 0);
        _pendingCount = 0;
        _seq = 0;

        _sink.OnMoveResult(new MoveResult
        {
            Sequence = seq, Direction = pm.Dir, Accepted = false, X = x, Y = y, Z = z, ServerDirection = dir,
            SentTimestamp = pm.Sent, ReceivedTimestamp = now, StillPending = 0,
        });
        _sink.OnMoveSequenceReset(MoveResetReason.Rejected, dropped);
    }

    private int IndexOfPending(byte seq)
    {
        for (var i = 0; i < _pendingCount; i++)
            if (_pending[i].Seq == seq) return i;
        return -1;
    }

    private void RemovePendingThrough(int index)
    {
        var keep = _pendingCount - (index + 1);
        for (var i = 0; i < keep; i++) _pending[i] = _pending[index + 1 + i];
        _pendingCount = keep;
    }

    private void ResetMoves(MoveResetReason reason, bool report)
    {
        var dropped = _pendingCount;
        _pendingCount = 0;
        _seq = 0;
        if (report) _sink.OnMoveSequenceReset(reason, dropped);
    }

    private void OnAsciiMessage(ref PacketReader r)
    {
        r.Skip(2);
        var serial = r.U32();
        r.Skip(2);
        var type = r.U8();
        var hue = r.U16();
        r.Skip(2);
        var nameLen = r.Latin1(30, _name);
        var textLen = r.Latin1(-1, _text);
        _sink.OnMessage(serial, type, hue, _name.AsSpan(0, nameLen), _text.AsSpan(0, textLen));
    }

    private void OnUnicodeMessage(ref PacketReader r)
    {
        r.Skip(2);
        var serial = r.U32();
        r.Skip(2);
        var type = r.U8();
        var hue = r.U16();
        r.Skip(2 + 4);
        var nameLen = r.Latin1(30, _name);
        var textLen = r.UnicodeBE(_text);
        _sink.OnMessage(serial, type, hue, _name.AsSpan(0, nameLen), _text.AsSpan(0, textLen));
    }

    // ---------------------------------------------------------------- helpers

    private void SetState(ClientState s, ReadOnlySpan<char> detail)
    {
        State = s;
        _sink.OnStateChanged(s, detail);
    }

    private void Fail(string reason)
    {
        LastError = reason;
        CloseSocket();
        SetState(ClientState.Failed, reason);
    }

    private static IPAddress Resolve(string host) =>
        IPAddress.TryParse(host, out var ip) ? ip : Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);

    private static (int a, int b, int c, int d) ParseVersion(string v)
    {
        Span<int> parts = stackalloc int[4];
        var i = 0;
        foreach (var range in v.AsSpan().Split('.'))
        {
            if (i == 4) break;
            int.TryParse(v.AsSpan(range), out parts[i++]);
        }
        return (parts[0], parts[1], parts[2], parts[3]);
    }

    private static void Return(ref byte[] a)
    {
        if (a.Length > 0) ArrayPool<byte>.Shared.Return(a);
        a = [];
    }
}
