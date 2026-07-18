using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using IRCBot.Shared;

namespace IRCBot.Host;

// A single IRC bot: one client connection to an IRC server, with its own
// independent connection settings (host, port, TLS, password, ident, realname).
// Connects, registers, keeps itself alive (PING/PONG), joins/parts channels,
// and can send messages. All public methods are safe to call from the control thread.
public sealed class IrcBot
{
    public string Id { get; }
    public string Nick { get; private set; } = "";
    public string Host { get; private set; } = "localhost";
    public int Port { get; private set; }
    public bool UseTls { get; private set; }
    public string Password { get; private set; } = "";
    public string Ident { get; private set; } = "";
    public string RealName { get; private set; } = "";
    public string CtcpVersion { get; private set; } = "Hihi!";

    public BotStatus Status { get; private set; } = BotStatus.Stopped;
    public string LastEvent { get; private set; } = "created";
    public DateTime? ConnectedUtc { get; private set; }

    private readonly object _lock = new();
    private readonly HashSet<string> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly EventLog? _events;

    // Channel ban lists captured from the server's 367/368 replies.
    private readonly Dictionary<string, List<ChannelBan>> _banCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ChannelBan>> _banBuilding = new(StringComparer.OrdinalIgnoreCase);

    private TcpClient? _tcp;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;

    public IrcBot(string id, BotConfig cfg, EventLog? events = null)
    {
        Id = id;
        _events = events;
        Apply(cfg);
        Event("created");
    }

    private void Apply(BotConfig cfg)
    {
        Nick = cfg.Nick;
        Host = cfg.Host;
        Port = cfg.Port;
        UseTls = cfg.UseTls;
        Password = cfg.Password;
        Ident = cfg.Ident;
        RealName = cfg.RealName;
        CtcpVersion = cfg.CtcpVersion;
        _channels.Clear();
        foreach (var c in cfg.Channels) _channels.Add(Normalize(c));
    }

    // Record an activity line: updates LastEvent (for the grid) and appends to
    // the shared event log (for the bot-activity console).
    private void Event(string text)
    {
        lock (_lock) LastEvent = text;
        _events?.Append(Id, Nick, text);
    }

    public BotInfo ToInfo()
    {
        lock (_lock)
            return new BotInfo
            {
                Id = Id, Nick = Nick, Host = Host, Port = Port, UseTls = UseTls,
                Ident = Ident, RealName = RealName, CtcpVersion = CtcpVersion,
                Status = Status, LastEvent = LastEvent, ConnectedUtc = ConnectedUtc,
                Channels = _channels.ToList()
            };
    }

    // Update configuration. Only permitted while stopped, so a running
    // connection is never mutated out from under itself.
    public bool UpdateConfig(BotConfig cfg)
    {
        bool ok;
        lock (_lock)
        {
            if (Status is BotStatus.Connecting or BotStatus.Connected) ok = false;
            else { Apply(cfg); ok = true; }
        }
        if (ok) Event("reconfigured");
        return ok;
    }

    public void Start()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (Status is BotStatus.Connecting or BotStatus.Connected) return;
            Status = BotStatus.Connecting;
            cts = _cts = new CancellationTokenSource();
        }
        Event($"connecting to {Host}:{Port}{(UseTls ? " over TLS" : "")}");
        _ = RunAsync(cts);
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
            ConnectedUtc = null;
        }
        Event("stopped");
        try { w?.WriteLine("QUIT :bye"); } catch { }
        try { cts?.Cancel(); } catch { }
        try { _tcp?.Close(); } catch { }
    }

    // Returns true if the JOIN went out now; false if queued (bot not connected).
    public bool Join(string channel)
    {
        var ch = Normalize(channel);
        bool connected;
        lock (_lock) { _channels.Add(ch); connected = Status == BotStatus.Connected; }
        if (connected) { Event($"JOIN {ch}"); Send($"JOIN {ch}"); }
        else Event($"JOIN {ch} queued (not connected)");
        return connected;
    }

    // Returns true if the PART went out now; false if only de-queued.
    public bool Part(string channel)
    {
        var ch = Normalize(channel);
        bool connected;
        lock (_lock) { _channels.Remove(ch); connected = Status == BotStatus.Connected; }
        if (connected) { Event($"PART {ch}"); Send($"PART {ch}"); }
        else Event($"PART {ch} (de-queued, not connected)");
        return connected;
    }

    // Returns true if sent; false if the bot is not connected.
    public bool Say(string target, string text)
    {
        bool connected;
        lock (_lock) connected = Status == BotStatus.Connected;
        if (!connected) return false;
        Event($"PRIVMSG {target}");
        Send($"PRIVMSG {target} :{text}");
        return true;
    }

    // Send a channel MODE change (op/deop, voice/devoice, ban/unban, or channel
    // modes). Requires operator status on the channel. Returns false if not connected.
    public bool Mode(string channel, string modes)
    {
        bool connected;
        lock (_lock) connected = Status == BotStatus.Connected;
        if (!connected) return false;
        Event($"MODE {channel} {modes}");
        Send($"MODE {channel} {modes}");
        return true;
    }

    // Kick a nick from a channel. Requires operator status. False if not connected.
    public bool Kick(string channel, string nick, string reason)
    {
        bool connected;
        lock (_lock) connected = Status == BotStatus.Connected;
        if (!connected) return false;
        Event($"KICK {nick} from {channel}");
        Send(string.IsNullOrEmpty(reason) ? $"KICK {channel} {nick}" : $"KICK {channel} {nick} :{reason}");
        return true;
    }

    // Ask the server for the channel's ban list (populates the cache via 367/368).
    public bool RequestBanList(string channel)
    {
        bool connected;
        lock (_lock) { connected = Status == BotStatus.Connected; _banBuilding[channel] = new(); }
        if (!connected) return false;
        Event($"requesting ban list for {channel}");
        Send($"MODE {channel} +b");
        return true;
    }

    public List<ChannelBan> GetBans(string channel)
    {
        lock (_lock)
            return _banCache.TryGetValue(channel, out var l) ? new List<ChannelBan>(l) : new();
    }

    private async Task RunAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        TcpClient? tcp = null;
        Stream? netStream = null;   // outermost stream (SslStream or NetworkStream)
        StreamReader? reader = null;
        bool errored = false;
        try
        {
            tcp = _tcp = new TcpClient();
            await tcp.ConnectAsync(Host, Port, ct);
            Event("socket connected");

            Stream stream = netStream = tcp.GetStream();
            if (UseTls)
            {
                // Local test tooling: accept any server certificate.
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false,
                    (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, ct);
                Event($"TLS established ({ssl.SslProtocol})");
                stream = netStream = ssl;
            }

            reader = new StreamReader(stream, new UTF8Encoding(false));
            lock (_lock) _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

            var ident = string.IsNullOrWhiteSpace(Ident) ? Nick : Ident;
            var real = string.IsNullOrWhiteSpace(RealName) ? $"IRCBot {Nick}" : RealName;
            if (!string.IsNullOrEmpty(Password)) { Event("sent PASS (hidden)"); Send($"PASS {Password}"); }
            Event($"registering as {Nick} (ident {ident})");
            Send($"NICK {Nick}");
            Send($"USER {ident} 0 * :{real}");

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
            errored = true;
            // Only report if this is still the active run — a newer Start()
            // may have superseded us after a rapid Stop/Start.
            bool owner;
            lock (_lock)
            {
                owner = ReferenceEquals(_cts, cts);
                if (owner) { Status = BotStatus.Error; ConnectedUtc = null; }
            }
            if (owner) Event($"error: {ex.Message}");
        }
        finally
        {
            // Release this run's socket/streams on every exit path.
            try { reader?.Dispose(); } catch { }
            try { netStream?.Dispose(); } catch { }
            try { tcp?.Dispose(); } catch { }
        }
        if (errored) return;

        bool stillOwner, wasStopped;
        lock (_lock)
        {
            stillOwner = ReferenceEquals(_cts, cts);
            wasStopped = Status == BotStatus.Stopped;
            if (stillOwner)
            {
                if (!wasStopped) Status = BotStatus.Stopped;
                ConnectedUtc = null;
            }
        }
        if (stillOwner && !wasStopped) Event("disconnected");
    }

    private void HandleLine(string raw)
    {
        // Respond to PING to stay connected
        if (raw.StartsWith("PING", StringComparison.OrdinalIgnoreCase))
        {
            var arg = raw.Length > 5 ? raw[5..].TrimStart(':') : "";
            Event("PING/PONG");
            Send($"PONG :{arg}");
            return;
        }

        string? sender = null;
        var body = raw;
        if (body.StartsWith(':'))
        {
            int sp = body.IndexOf(' ');
            if (sp < 0) return;
            sender = body[1..sp]; // nick!user@host
            body = body[(sp + 1)..];
        }
        var cmd = body.Split(' ', 2)[0];

        if (cmd == "PRIVMSG") { HandlePrivmsg(sender, body); return; }

        if (cmd == "001") // RPL_WELCOME → registration complete
        {
            string[] chans;
            lock (_lock)
            {
                Status = BotStatus.Connected;
                ConnectedUtc = DateTime.UtcNow;
                chans = _channels.ToArray();
            }
            Event("connected (registered)");
            foreach (var ch in chans) { Event($"joining {ch}"); Send($"JOIN {ch}"); }
        }
        else if (cmd == "433") // nick in use → append suffix and retry
        {
            lock (_lock) Nick += "_";
            Event($"nick in use, retrying as {Nick}");
            Send($"NICK {Nick}");
        }
        else if (cmd == "367") // RPL_BANLIST: <me> <channel> <mask> [<setBy> <setAt>]
        {
            var p = body.Split(' ');
            if (p.Length >= 4)
            {
                var ban = new ChannelBan
                {
                    Mask = p[3],
                    SetBy = p.Length > 4 ? p[4] : "",
                    SetAt = p.Length > 5 ? p[5] : ""
                };
                lock (_lock)
                {
                    if (!_banBuilding.TryGetValue(p[2], out var list)) { list = new(); _banBuilding[p[2]] = list; }
                    list.Add(ban);
                }
            }
        }
        else if (cmd == "368") // RPL_ENDOFBANLIST: <me> <channel> :End of ban list
        {
            var p = body.Split(' ');
            if (p.Length >= 3)
            {
                int count;
                lock (_lock)
                {
                    var list = _banBuilding.TryGetValue(p[2], out var l) ? l : new();
                    _banCache[p[2]] = list;
                    _banBuilding[p[2]] = new();
                    count = list.Count;
                }
                Event($"ban list for {p[2]}: {count} entr{(count == 1 ? "y" : "ies")}");
            }
        }
    }

    // Handle incoming PRIVMSG, replying to CTCP VERSION requests.
    private void HandlePrivmsg(string? sender, string body)
    {
        // body = "PRIVMSG <target> :<text>"
        var parts = body.Split(' ', 3);
        if (parts.Length < 3) return;
        var text = parts[2];
        if (text.StartsWith(':')) text = text[1..];

        // CTCP is wrapped in \x01 … \x01
        if (text.Length < 2 || text[0] != '\u0001' || text[^1] != '\u0001') return;
        var inner = text.Trim('\u0001');
        var from = sender?.Split('!')[0];
        if (from == null) return;

        if (inner.Equals("VERSION", StringComparison.OrdinalIgnoreCase))
        {
            Event($"CTCP VERSION from {from} → \"{CtcpVersion}\"");
            Send($"NOTICE {from} :\u0001VERSION {CtcpVersion}\u0001");
        }
    }

    private void Send(string line)
    {
        try { lock (_lock) _writer?.WriteLine(line); } catch { }
    }

    private static string Normalize(string channel) =>
        channel.StartsWith('#') || channel.StartsWith('&') ? channel : "#" + channel;
}
