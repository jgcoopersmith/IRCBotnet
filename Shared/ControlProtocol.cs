using System.Text.Json;

namespace IRCBot.Shared;

// Line-delimited JSON protocol spoken on the bot host's control port.
// The remote-control front end sends one JSON request per line and reads one
// JSON response per line.

public static class BotCommands
{
    public const string Auth   = "AUTH";   // args: pass
    public const string List   = "LIST";   // -> Bots
    public const string Add    = "ADD";    // args: id(optional), nick, host, port, channels(comma) -> Bots
    public const string Edit   = "EDIT";   // args: id, nick, host, port, channels(comma) -> Bots
    public const string Remove = "REMOVE"; // args: id
    public const string Start  = "START";  // args: id
    public const string Stop   = "STOP";   // args: id
    public const string Join   = "JOIN";   // args: id, channel
    public const string Part   = "PART";   // args: id, channel
    public const string Say    = "SAY";    // args: id, target, text
    public const string Mode    = "MODE";    // args: id, channel, modes (e.g. "+o nick", "+m")
    public const string Kick    = "KICK";    // args: id, channel, nick, reason
    public const string BanList = "BANLIST"; // args: id, channel -> ChannelBans (and refreshes cache)
    public const string Events  = "EVENTS";  // args: since (cursor) -> Events, Cursor
}

public enum BotStatus { Stopped, Connecting, Connected, Error }

public sealed class BotRequest
{
    public string Cmd { get; set; } = "";
    public Dictionary<string, string> Args { get; set; } = new();
    public string Arg(string key) => Args.TryGetValue(key, out var v) ? v : "";
}

public sealed class BotResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public List<BotInfo>? Bots { get; set; }
    public List<BotEvent>? Events { get; set; }
    public long Cursor { get; set; }
    public List<ChannelBan>? ChannelBans { get; set; }
}

// A +b entry on a channel, as reported by the server (RPL_BANLIST / 367).
public sealed class ChannelBan
{
    public string Mask { get; set; } = "";
    public string SetBy { get; set; } = "";
    public string SetAt { get; set; } = "";
}

// A single line of bot activity (connecting, TLS, registering, join, error…).
public sealed class BotEvent
{
    public long Seq { get; set; }
    public string BotId { get; set; } = "";
    public string Nick { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Utc { get; set; }
}

public sealed class BotInfo
{
    public string Id { get; set; } = "";
    public string Nick { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool UseTls { get; set; }
    public string Ident { get; set; } = "";
    public string RealName { get; set; } = "";
    public string CtcpVersion { get; set; } = "";
    public BotStatus Status { get; set; }
    public List<string> Channels { get; set; } = new();
    public string LastEvent { get; set; } = "";
    public DateTime? ConnectedUtc { get; set; }
}

// Full per-bot connection configuration. Each bot connects independently.
public sealed class BotConfig
{
    public string Nick { get; set; } = "";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6667;
    public bool UseTls { get; set; }
    public string Password { get; set; } = "";   // server PASS; empty = none
    public string Ident { get; set; } = "";       // USER ident; empty = nick
    public string RealName { get; set; } = "";     // empty = "IRCBot <nick>"
    public string CtcpVersion { get; set; } = "Hihi!"; // reply to CTCP VERSION
    public List<string> Channels { get; set; } = new();

    public static BotConfig FromArgs(BotRequest r) => new()
    {
        Nick = r.Arg("nick"),
        Host = r.Arg("host") is { Length: > 0 } h ? h : "localhost",
        Port = int.TryParse(r.Arg("port"), out var p) ? p : 6667,
        UseTls = r.Arg("tls").Equals("true", StringComparison.OrdinalIgnoreCase),
        Password = r.Arg("password"),
        Ident = r.Arg("ident"),
        RealName = r.Arg("realname"),
        CtcpVersion = r.Args.TryGetValue("ctcpversion", out var cv) ? cv : "Hihi!",
        Channels = r.Arg("channels").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
    };
}

public static class ControlJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
