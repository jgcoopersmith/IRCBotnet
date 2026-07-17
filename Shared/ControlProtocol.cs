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
}

public sealed class BotInfo
{
    public string Id { get; set; } = "";
    public string Nick { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public BotStatus Status { get; set; }
    public List<string> Channels { get; set; } = new();
    public string LastEvent { get; set; } = "";
    public DateTime? ConnectedUtc { get; set; }
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
