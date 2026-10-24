namespace Uo.Net;

public sealed class LoginOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 2593;
    public required string Username { get; init; }
    public required string Password { get; init; }
    /// <summary>Character to play; null = first character on the account. Created if absent.</summary>
    public string? CharacterName { get; init; }
    /// <summary>Name for a new character when the account has none.</summary>
    public string NewCharacterName { get; init; } = "Pocwalker";
    /// <summary>Starting city to prefer for a new character (matched by name; falls back to index 0).</summary>
    public string NewCharacterCity { get; init; } = "Britain";
    /// <summary>Version reported in 0xEF and 0xBD. ModernUO requires at least the client it loaded data from.</summary>
    public string ClientVersion { get; init; } = "7.0.103.0";
    /// <summary>Use the relay (0x8C) address; when false or unreachable, reconnect to <see cref="Host"/>.</summary>
    public bool UseRelayAddress { get; init; } = true;
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The packet layer as the rest of the client sees it. The classic-protocol implementation is a
/// stand-in; a future delta protocol implements the same surface.
/// </summary>
public interface IGameClient : IDisposable
{
    ClientState State { get; }
    uint PlayerSerial { get; }

    /// <summary>Starts the login flow. Progress happens in <see cref="Poll"/>.</summary>
    void Connect(LoginOptions options);

    /// <summary>
    /// Drains the socket and dispatches every complete packet to the sink. Non-blocking unless
    /// <paramref name="waitMs"/> &gt; 0, in which case it waits up to that long for data.
    /// Returns the number of packets dispatched.
    /// </summary>
    int Poll(int waitMs = 0);

    /// <summary>
    /// Sends a move request. Returns the sequence number, or -1 if not in world or too many requests
    /// are unacknowledged (classic clients allow 5).
    /// </summary>
    int RequestMove(Direction direction);

    int PendingMoves { get; }

    void Say(ReadOnlySpan<char> text);

    void Disconnect();
}
