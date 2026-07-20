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
    // This bot's own status per channel: "@" op, "+" voice, "" none.
    private readonly Dictionary<string, string> _channelPrefix = new(StringComparer.OrdinalIgnoreCase);
    // Channels for which a WHO was issued to enforce the auto list, so unrelated
    // WHO replies are ignored.
    private readonly HashSet<string> _autoWhoPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly EventLog? _events;
    private readonly AutoRules? _auto;

    // Channel ban lists captured from the server's 367/368 replies.
    private readonly Dictionary<string, List<ChannelBan>> _banCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ChannelBan>> _banBuilding = new(StringComparer.OrdinalIgnoreCase);

    private TcpClient? _tcp;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;

    public IrcBot(string id, BotConfig cfg, EventLog? events = null, AutoRules? auto = null)
    {
        Id = id;
        _events = events;
        _auto = auto;
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
                Channels = _channels.ToList(),
                ChannelsDisplay = _channels.Select(c => _channelPrefix.GetValueOrDefault(c, "") + c).ToList()
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
            _channelPrefix.Clear(); _autoWhoPending.Clear();
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
        lock (_lock) { _channels.Remove(ch); _channelPrefix.Remove(ch); connected = Status == BotStatus.Connected; }
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
        var ch = Normalize(channel);
        bool connected;
        lock (_lock) connected = Status == BotStatus.Connected;
        if (!connected) return false;
        Event($"MODE {ch} {modes}");
        Send($"MODE {ch} {modes}");
        return true;
    }

    // Kick a nick from a channel. Requires operator status. False if not connected.
    public bool Kick(string channel, string nick, string reason)
    {
        var ch = Normalize(channel);
        bool connected;
        lock (_lock) connected = Status == BotStatus.Connected;
        if (!connected) return false;
        Event($"KICK {nick} from {ch}");
        Send(string.IsNullOrEmpty(reason) ? $"KICK {ch} {nick}" : $"KICK {ch} {nick} :{reason}");
        return true;
    }

    // Ask the server for the channel's ban list (populates the cache via 367/368).
    public bool RequestBanList(string channel)
    {
        var ch = Normalize(channel);
        bool connected;
        lock (_lock) { connected = Status == BotStatus.Connected; _banBuilding[ch] = new(); }
        if (!connected) return false;
        Event($"requesting ban list for {ch}");
        Send($"MODE {ch} +b");
        return true;
    }

    public List<ChannelBan> GetBans(string channel)
    {
        var ch = Normalize(channel);
        lock (_lock)
            return _banCache.TryGetValue(ch, out var l) ? new List<ChannelBan>(l) : new();
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
                if (owner) { Status = BotStatus.Error; ConnectedUtc = null; _channelPrefix.Clear(); _autoWhoPending.Clear(); }
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
                _channelPrefix.Clear(); _autoWhoPending.Clear();
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
        else if (cmd == "353") HandleNames(body);   // RPL_NAMREPLY → learn our own prefix
        else if (cmd == "MODE") HandleModeLine(body); // track op/voice changes to us
        else if (cmd == "KICK") HandleKickLine(body); // leave a channel we were kicked from
        else if (cmd == "JOIN") HandleJoinLine(sender, body); // enforce the auto list
        else if (cmd == "352") HandleWhoReply(body);   // RPL_WHOREPLY → enforce on current members
        else if (cmd == "315") HandleEndOfWho(body);   // RPL_ENDOFWHO → stop enforcing this WHO
        // Error replies occupy 400–599. A rejected MODE or KICK is only ever
        // reported this way, so surface it rather than dropping it silently.
        else if (int.TryParse(cmd, out var numeric) && numeric is >= 400 and <= 599)
            HandleErrorNumeric(cmd, body);
    }

    // "<numeric> <me> [<context>…] :<message>" — e.g.
    // "482 opbot #kungfu :You're not channel operator"
    private void HandleErrorNumeric(string numeric, string body)
    {
        var text = "";
        int colon = body.IndexOf(" :", StringComparison.Ordinal);
        if (colon >= 0) text = body[(colon + 2)..];

        // Tokens between our own nick and the trailing message name what was
        // rejected (a channel, a nick), which is the useful part.
        var head = (colon >= 0 ? body[..colon] : body)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var context = head.Length > 2 ? string.Join(" ", head.Skip(2)) : "";

        Event($"server error {numeric}"
              + (context.Length > 0 ? $" ({context})" : "")
              + (text.Length > 0 ? $": {text}" : ""));
    }

    // RPL_WHOREPLY: "352 <me> <channel> <user> <host> <server> <nick> <flags> :<hop> <real>"
    // Only acts for channels we asked about for auto-enforcement.
    private void HandleWhoReply(string body)
    {
        var p = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 8) return;
        var chan = p[2];
        lock (_lock) if (!_autoWhoPending.Contains(chan)) return;

        var user = p[3];
        var host = p[4];
        var nick = p[6];
        var whoFlags = p[7]; // e.g. "H", "H@", "G+"
        if (string.Equals(nick, Nick, StringComparison.OrdinalIgnoreCase)) return;

        var current = whoFlags.Contains('@') ? "@" : whoFlags.Contains('+') ? "+" : "";
        ApplyAuto(chan, nick, $"{nick}!{user}@{host}", current);
    }

    // RPL_ENDOFWHO: "315 <me> <mask> :End of /WHO list."
    private void HandleEndOfWho(string body)
    {
        var p = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length >= 3) lock (_lock) _autoWhoPending.Remove(p[2]);
    }

    // ":<nick>!<user>@<host> JOIN <channel>" — apply any matching auto rules.
    // Only a bot that currently holds op can act, so in a channel with several
    // bots the opped ones will each issue the same change; a redundant +o or +v
    // is harmless, and a second KICK simply finds the user already gone.
    private void HandleJoinLine(string? sender, string body)
    {
        if (_auto == null || string.IsNullOrEmpty(sender)) return;

        var parts = body.Split(' ', 2);
        if (parts.Length < 2) return;
        var chan = parts[1].TrimStart(':').Trim();
        if (chan.Length == 0) return;

        var nick = sender.Split('!')[0];
        if (string.Equals(nick, Nick, StringComparison.OrdinalIgnoreCase)) return; // our own join
        ApplyAuto(chan, nick, sender, "");
    }

    // Apply the auto list to one user. prefix is their nick!user@host; current
    // is their existing channel status ("@", "+" or "") so we don't re-grant it.
    // Only an opped bot may act. Kick wins over status, op over voice.
    private void ApplyAuto(string chan, string nick, string prefix, string current)
    {
        if (_auto == null) return;
        bool isOp;
        lock (_lock) isOp = _channelPrefix.GetValueOrDefault(chan, "") == "@";
        if (!isOp) return;

        var flags = _auto.FlagsFor(prefix, chan);
        if (flags.Length == 0) return;

        if (flags.Contains('k'))
        {
            Event($"auto-kick {nick} from {chan}");
            Send($"KICK {chan} {nick} :auto-kick");
        }
        else if (flags.Contains('o') && current != "@")
        {
            Event($"auto-op {nick} in {chan}");
            Send($"MODE {chan} +o {nick}");
        }
        else if (flags.Contains('v') && current != "@" && current != "+")
        {
            Event($"auto-voice {nick} in {chan}");
            Send($"MODE {chan} +v {nick}");
        }
    }

    // Enforce the auto list against everyone already in a channel by asking the
    // server who is there (WHO → 352). Only meaningful once we hold op.
    public void EnforceChannel(string channel)
    {
        var ch = Normalize(channel);
        bool go;
        lock (_lock)
        {
            go = Status == BotStatus.Connected && _channelPrefix.GetValueOrDefault(ch, "") == "@";
            if (go) _autoWhoPending.Add(ch);
        }
        if (go) Send($"WHO {ch}");
    }

    // Re-enforce every channel we are in (e.g. after the auto list is edited).
    public void EnforceAll()
    {
        string[] chans;
        lock (_lock) chans = _channels.ToArray();
        foreach (var ch in chans) EnforceChannel(ch);
    }

    // "KICK <channel> <target> [:<reason>]" — if we're the target, leave the channel.
    private void HandleKickLine(string body)
    {
        var parts = body.Split(' ', 4);
        if (parts.Length < 3) return;
        var chan = parts[1];
        var target = parts[2];
        if (!string.Equals(target, Nick, StringComparison.OrdinalIgnoreCase)) return;

        lock (_lock) { _channels.Remove(chan); _channelPrefix.Remove(chan); }
        var reason = parts.Length > 3 ? parts[3].TrimStart(':') : "";
        Event($"kicked from {chan}" + (reason.Length > 0 ? $" ({reason})" : ""));
    }

    // RPL_NAMREPLY: "353 <me> = <channel> :<prefixed nicks>" — find our own prefix.
    private void HandleNames(string body)
    {
        int colon = body.IndexOf(':');
        if (colon < 0) return;
        var header = body[..colon].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var chan = header.LastOrDefault(t => t.StartsWith('#') || t.StartsWith('&'));
        if (chan == null) return;

        foreach (var entry in body[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var prefix = "";
            var name = entry;
            if (name.Length > 0 && (name[0] == '@' || name[0] == '+')) { prefix = name[0].ToString(); name = name[1..]; }
            if (string.Equals(name, Nick, StringComparison.OrdinalIgnoreCase))
            {
                lock (_lock) if (_channels.Contains(chan)) _channelPrefix[chan] = prefix;
                // Already opped on join → enforce the auto list on who's here.
                if (prefix == "@") EnforceChannel(chan);
                break;
            }
        }
    }

    // Track channel MODE changes that op/deop/voice/devoice this bot.
    private void HandleModeLine(string body)
    {
        var parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return;
        var chan = parts[1];
        if (!chan.StartsWith('#') && !chan.StartsWith('&')) return;

        var flags = parts[2];
        var args = parts.Skip(3).ToArray();
        int argIdx = 0;
        char sign = '+';
        bool gainedOp = false;
        foreach (var c in flags)
        {
            if (c is '+' or '-') { sign = c; continue; }
            string? param = (c is 'o' or 'v' or 'k' or 'l' or 'b') && argIdx < args.Length ? args[argIdx++] : null;
            if ((c == 'o' || c == 'v') && param != null && string.Equals(param, Nick, StringComparison.OrdinalIgnoreCase))
            {
                lock (_lock)
                {
                    var cur = _channelPrefix.GetValueOrDefault(chan, "");
                    if (sign == '+') { if (c == 'o') { _channelPrefix[chan] = "@"; gainedOp = true; } else if (cur != "@") _channelPrefix[chan] = "+"; }
                    else { if (c == 'o' && cur == "@") _channelPrefix[chan] = ""; else if (c == 'v' && cur == "+") _channelPrefix[chan] = ""; }
                }
            }
        }
        // Just got op → apply the auto list to everyone already in the channel.
        if (gainedOp) EnforceChannel(chan);
    }

    // Handle incoming PRIVMSG: surface messages and CTCP in the activity log,
    // and reply to CTCP VERSION requests.
    private void HandlePrivmsg(string? sender, string body)
    {
        // body = "PRIVMSG <target> :<text>"
        var parts = body.Split(' ', 3);
        if (parts.Length < 3) return;
        var target = parts[1];
        var text = parts[2];
        if (text.StartsWith(':')) text = text[1..];
        var from = sender?.Split('!')[0] ?? "?";

        // CTCP is wrapped in \x01 … \x01
        if (text.Length >= 2 && text[0] == '\u0001' && text[^1] == '\u0001')
        {
            var inner = text.Trim('\u0001');
            var ctcpType = inner.Split(' ')[0].ToUpperInvariant();
            if (ctcpType == "VERSION")
            {
                Event($"CTCP VERSION from {from} → \"{CtcpVersion}\"");
                Send($"NOTICE {from} :\u0001VERSION {CtcpVersion}\u0001");
            }
            else Event($"CTCP {ctcpType} from {from}");
            return;
        }

        // Regular message the bot received (channel message or private message).
        bool isChannel = target.StartsWith('#') || target.StartsWith('&');
        Event(isChannel ? $"<{from}> {target}: {text}" : $"PM <{from}>: {text}");
    }

    private void Send(string line)
    {
        try { lock (_lock) _writer?.WriteLine(line); } catch { }
    }

    private static string Normalize(string channel) =>
        channel.StartsWith('#') || channel.StartsWith('&') ? channel : "#" + channel;
}
