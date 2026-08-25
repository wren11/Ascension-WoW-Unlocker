namespace HeadlessClient.Infrastructure.Config;

public sealed class PacketLogOptions
{
    /// <summary>Enable WPE-style hex/ASCII packet logging.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Directory for rotating packet logs (created if missing).</summary>
    public string Directory { get; set; } = @"c:\Users\Dean\gamet\HeadlessClient\logs\packets";

    /// <summary>Also mirror short packet summaries to console (bodies only when tiny).</summary>
    public bool MirrorToConsole { get; set; } = false;

    /// <summary>Max bytes per log file before rotate.</summary>
    public long MaxFileBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>Max retained rotated files per account tag.</summary>
    public int MaxFilesPerAccount { get; set; } = 8;

    /// <summary>Max payload bytes hex-dumped per packet (rest truncated).</summary>
    public int MaxPayloadDumpBytes { get; set; } = 2048;
}

public sealed class FleetOptions
{
    /// <summary>Auto-reconnect after disconnect / auth failure.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Delay before first reconnect attempt.</summary>
    public int ReconnectDelaySeconds { get; set; } = 5;

    /// <summary>Max delay between reconnect attempts (exponential backoff cap).</summary>
    public int ReconnectMaxDelaySeconds { get; set; } = 120;

    /// <summary>Keepalive pulse interval (ping / keep-alive / heartbeat).</summary>
    public int KeepAliveSeconds { get; set; } = 30;

    /// <summary>Stagger account logins by this many ms to avoid auth thundering herd.</summary>
    public int LoginStaggerMs { get; set; } = 1500;

    /// <summary>Channels to auto-join after enter-world. Empty = use built-in Ascension/WotLK set.</summary>
    public List<string> AutoJoinChannels { get; set; } = new();

    /// <summary>Seconds to wait after enter-world before joining channels (player must be fully in-world).</summary>
    public int ChannelJoinDelaySeconds { get; set; } = 8;
}
