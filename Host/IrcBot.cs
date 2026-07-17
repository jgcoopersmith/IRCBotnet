using System.Net.Sockets;
using System.Text;
using IRCBot.Shared;

namespace IRCBot.Host;

// A single IRC bot: one client connection to an IRC server. Connects,
// registers, keeps itself alive (PING/PONG), joins/parts channels, and can
// send messages. All public methods are safe to call from the control thread.
public sealed class IrcBot
{
    public string Id { get; }
    public string Nick { get; private set; }
    public string Host { get; private set; }
    public int Port { get; private set; }

    public BotStatus Status { get; private set; } = BotStatus.Stopped;
    public string LastEvent { get; private set; } = "created";
    public DateTime? ConnectedUtc { get; private set; }

    private readonly object _lock = new();
    private readonly HashSet<string> _channels = new(StringComparer.OrdinalIgnoreCase);

    private TcpClient? _tcp;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;

    public IrcBot(string id, string nick, string host, int port, IEnumerable<string> channels)
    {
        Id = id;
        Nick = nick;
        Host = host;
        Port = port;
        foreach (var c in channels) _channels.Add(Normalize(c));
    }

    // Update a bot's configuration. Only permitted while stopped, so a running
    // connection is never mutated out from under itself.
    public bool UpdateConfig(string nick, string host, int port, IEnumerable<string> channels)
    {
        lock (_lock)
        {
            if (Status is BotStatus.Connecting or BotStatus.Connected) return false;
            Nick = nick;
            Host = host;
            Port = port;
            _channels.Clear();
            foreach (var c in channels) _channels.Add(Normalize(c));
            LastEvent = "edited";
            return true;
        }
    }

    public BotInfo ToInfo()
    {
        lock (_lock)
            return new BotInfo
            {
                Id = Id, Nick = Nick, Host = Host, Port = Port,
                Status = Status, LastEvent = LastEvent, ConnectedUtc = ConnectedUtc,
                Channels = _channels.ToList()
            };
    }

    public void Start()
    {
        lock (_lock)
        {
            if (Status is BotStatus.Connecting or BotStatus.Connected) return;
            Status = BotStatus.Connecting;
            LastEvent = "connecting";
            _cts = new CancellationTokenSource();
        }
        _ = RunAsync(_cts!.Token);
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        StreamWriter? w;
        lock (_lock)
        {
            cts = _cts;
            w = _writer;
            Status = BotStatus.Stopped;
            LastEvent = "stopped";
            ConnectedUtc = null;
        }
        try { w?.WriteLine("QUIT :bye"); } catch { }
        try { cts?.Cancel(); } catch { }
        try { _tcp?.Close(); } catch { }
    }

    public void Join(string channel)
    {
        var ch = Normalize(channel);
        bool connected;
        lock (_lock) { _channels.Add(ch); connected = Status == BotStatus.Connected; LastEvent = $"join {ch}"; }
        if (connected) Send($"JOIN {ch}");
    }

    public void Part(string channel)
    {
        var ch = Normalize(channel);
        bool connected;
        lock (_lock) { _channels.Remove(ch); connected = Status == BotStatus.Connected; LastEvent = $"part {ch}"; }
        if (connected) Send($"PART {ch}");
    }

    public void Say(string target, string text)
    {
        lock (_lock) { if (Status != BotStatus.Connected) return; LastEvent = $"say {target}"; }
        Send($"PRIVMSG {target} :{text}");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(Host, Port, ct);
            var stream = _tcp.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            lock (_lock) _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

            Send($"NICK {Nick}");
            Send($"USER {Nick} 0 * :IRCBot {Nick}");

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                HandleLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (_lock) { Status = BotStatus.Error; LastEvent = $"error: {ex.Message}"; ConnectedUtc = null; }
            return;
        }

        lock (_lock)
        {
            if (Status != BotStatus.Stopped) { Status = BotStatus.Stopped; LastEvent = "disconnected"; }
            ConnectedUtc = null;
        }
    }

    private void HandleLine(string raw)
    {
        // Respond to PING to stay connected
        if (raw.StartsWith("PING", StringComparison.OrdinalIgnoreCase))
        {
            var arg = raw.Length > 5 ? raw[5..].TrimStart(':') : "";
            Send($"PONG :{arg}");
            return;
        }

        // Parse command token (skip optional :prefix)
        var body = raw;
        if (body.StartsWith(':'))
        {
            int sp = body.IndexOf(' ');
            if (sp < 0) return;
            body = body[(sp + 1)..];
        }
        var cmd = body.Split(' ', 2)[0];

        // 001 = RPL_WELCOME → registration complete
        if (cmd == "001")
        {
            string[] chans;
            lock (_lock)
            {
                Status = BotStatus.Connected;
                ConnectedUtc = DateTime.UtcNow;
                LastEvent = "connected";
                chans = _channels.ToArray();
            }
            foreach (var ch in chans) Send($"JOIN {ch}");
        }
        else if (cmd == "433") // nick in use → append suffix and retry
        {
            lock (_lock) { Nick += "_"; LastEvent = "nick in use, retrying"; }
            Send($"NICK {Nick}");
        }
    }

    private void Send(string line)
    {
        try { lock (_lock) _writer?.WriteLine(line); } catch { }
    }

    private static string Normalize(string channel) =>
        channel.StartsWith('#') || channel.StartsWith('&') ? channel : "#" + channel;
}
