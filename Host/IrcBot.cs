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

    public BotStatus Status { get; private set; } = BotStatus.Stopped;
    public string LastEvent { get; private set; } = "created";
    public DateTime? ConnectedUtc { get; private set; }

    private readonly object _lock = new();
    private readonly HashSet<string> _channels = new(StringComparer.OrdinalIgnoreCase);

    private TcpClient? _tcp;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;

    public IrcBot(string id, BotConfig cfg)
    {
        Id = id;
        Apply(cfg);
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
        _channels.Clear();
        foreach (var c in cfg.Channels) _channels.Add(Normalize(c));
    }

    public BotInfo ToInfo()
    {
        lock (_lock)
            return new BotInfo
            {
                Id = Id, Nick = Nick, Host = Host, Port = Port, UseTls = UseTls,
                Ident = Ident, RealName = RealName,
                Status = Status, LastEvent = LastEvent, ConnectedUtc = ConnectedUtc,
                Channels = _channels.ToList()
            };
    }

    // Update configuration. Only permitted while stopped, so a running
    // connection is never mutated out from under itself.
    public bool UpdateConfig(BotConfig cfg)
    {
        lock (_lock)
        {
            if (Status is BotStatus.Connecting or BotStatus.Connected) return false;
            Apply(cfg);
            LastEvent = "edited";
            return true;
        }
    }

    public void Start()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (Status is BotStatus.Connecting or BotStatus.Connected) return;
            Status = BotStatus.Connecting;
            LastEvent = "connecting";
            cts = _cts = new CancellationTokenSource();
        }
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

    private async Task RunAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(Host, Port, ct);

            Stream stream = _tcp.GetStream();
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
                stream = ssl;
            }

            var reader = new StreamReader(stream, new UTF8Encoding(false));
            lock (_lock) _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

            var ident = string.IsNullOrWhiteSpace(Ident) ? Nick : Ident;
            var real = string.IsNullOrWhiteSpace(RealName) ? $"IRCBot {Nick}" : RealName;
            if (!string.IsNullOrEmpty(Password)) Send($"PASS {Password}");
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
            // Only report the failure if this is still the active run — a newer
            // Start() may have superseded us after a rapid Stop/Start.
            lock (_lock)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    Status = BotStatus.Error;
                    LastEvent = $"error: {ex.Message}";
                    ConnectedUtc = null;
                }
            }
            return;
        }

        lock (_lock)
        {
            if (ReferenceEquals(_cts, cts))
            {
                if (Status != BotStatus.Stopped) { Status = BotStatus.Stopped; LastEvent = "disconnected"; }
                ConnectedUtc = null;
            }
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

        var body = raw;
        if (body.StartsWith(':'))
        {
            int sp = body.IndexOf(' ');
            if (sp < 0) return;
            body = body[(sp + 1)..];
        }
        var cmd = body.Split(' ', 2)[0];

        if (cmd == "001") // RPL_WELCOME → registration complete
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
