using System.Net;
using System.Net.Sockets;
using System.Text;
using IRCBot.Shared;

namespace IRCBot.Host;

// Loopback-only JSON control port. The remote-control front end connects here
// to list bots and drive them (add/remove/start/stop/join/part/say).
public sealed class ControlInterface(BotHost host, int port, string? password)
{
    public async Task RunAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"Control interface listening on 127.0.0.1:{port}" +
                          (password != null ? " (password required)" : " (no password)"));
        while (true)
        {
            var tcp = await listener.AcceptTcpClientAsync();
            _ = HandleAsync(tcp);
        }
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        using var _ = tcp;
        var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        bool authed = password == null;
        Console.WriteLine("[control] connection opened");
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                BotResponse resp;
                try
                {
                    var req = ControlJson.Deserialize<BotRequest>(line) ?? new BotRequest();
                    resp = Dispatch(req, ref authed);
                }
                catch (Exception ex) { resp = new BotResponse { Ok = false, Error = ex.Message }; }
                await writer.WriteLineAsync(ControlJson.Serialize(resp));
            }
        }
        catch { }
        finally { Console.WriteLine("[control] connection closed"); }
    }

    private BotResponse Dispatch(BotRequest req, ref bool authed)
    {
        var cmd = req.Cmd.ToUpperInvariant();

        if (cmd == BotCommands.Auth)
        {
            authed = password != null && req.Arg("pass") == password;
            return authed ? Ok("Authenticated") : Fail("Invalid password");
        }
        if (!authed) return Fail("Authentication required");

        switch (cmd)
        {
            case BotCommands.List:
                return new BotResponse { Ok = true, Bots = host.List().ToList() };

            case BotCommands.Add:
            {
                var nick = req.Arg("nick");
                if (string.IsNullOrWhiteSpace(nick)) return Fail("Nick required");
                var chost = req.Arg("host") is { Length: > 0 } h ? h : "localhost";
                if (!int.TryParse(req.Arg("port"), out var port)) port = 6667;
                var channels = req.Arg("channels").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var (ok, msg) = host.Upsert(req.Arg("id"), nick, chost, port, channels);
                return new BotResponse { Ok = ok, Message = ok ? msg : null, Error = ok ? null : msg, Bots = host.List().ToList() };
            }

            case BotCommands.Edit:
            {
                var id = req.Arg("id");
                var nick = req.Arg("nick");
                if (string.IsNullOrWhiteSpace(nick)) return Fail("Nick required");
                var chost = req.Arg("host") is { Length: > 0 } h ? h : "localhost";
                if (!int.TryParse(req.Arg("port"), out var port)) port = 6667;
                var channels = req.Arg("channels").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var (ok, msg) = host.Edit(id, nick, chost, port, channels);
                return new BotResponse { Ok = ok, Message = ok ? msg : null, Error = ok ? null : msg, Bots = host.List().ToList() };
            }

            case BotCommands.Remove:
                return host.Remove(req.Arg("id")) ? Ok("Removed") : Fail("No such bot");
            case BotCommands.Start:
                return host.Start(req.Arg("id")) ? Ok("Starting") : Fail("No such bot");
            case BotCommands.Stop:
                return host.Stop(req.Arg("id")) ? Ok("Stopped") : Fail("No such bot");
            case BotCommands.Join:
                return host.Join(req.Arg("id"), req.Arg("channel")) ? Ok("Joining") : Fail("No such bot");
            case BotCommands.Part:
                return host.Part(req.Arg("id"), req.Arg("channel")) ? Ok("Parting") : Fail("No such bot");
            case BotCommands.Say:
                return host.Say(req.Arg("id"), req.Arg("target"), req.Arg("text")) ? Ok("Sent") : Fail("No such bot");

            default:
                return Fail($"Unknown command: {req.Cmd}");
        }
    }

    private static BotResponse Ok(string msg) => new() { Ok = true, Message = msg };
    private static BotResponse Fail(string err) => new() { Ok = false, Error = err };
}
