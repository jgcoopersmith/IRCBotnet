using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using IRCBot.Shared;

namespace IRCBot.Host;

// The bots' control endpoint. The control panel reaches out to this port over
// TLS to drive the bots (list/add/edit/remove/start/stop/join/part/say).
// Loopback-only; the channel is always encrypted with a self-signed cert.
public sealed class ControlInterface(BotHost host, int port, string? password)
{
    private readonly X509Certificate2 _cert = CreateSelfSignedCert();

    public async Task RunAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"Bots control endpoint listening on 127.0.0.1:{port} (TLS)" +
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
        Console.WriteLine("[control] connection opened");
        try
        {
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _cert,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            });

            using var reader = new StreamReader(ssl, new UTF8Encoding(false));
            await using var writer = new StreamWriter(ssl, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

            bool authed = password == null;
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
        catch (Exception ex) { Console.WriteLine($"[control] session ended: {ex.Message}"); }
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
                var cfg = BotConfig.FromArgs(req);
                if (string.IsNullOrWhiteSpace(cfg.Nick)) return Fail("Nick required");
                var (ok, msg) = host.Upsert(req.Arg("id"), cfg);
                return new BotResponse { Ok = ok, Message = ok ? msg : null, Error = ok ? null : msg, Bots = host.List().ToList() };
            }

            case BotCommands.Edit:
            {
                var cfg = BotConfig.FromArgs(req);
                if (string.IsNullOrWhiteSpace(cfg.Nick)) return Fail("Nick required");
                var (ok, msg) = host.Edit(req.Arg("id"), cfg);
                return new BotResponse { Ok = ok, Message = ok ? msg : null, Error = ok ? null : msg, Bots = host.List().ToList() };
            }

            case BotCommands.Remove:
                return host.Remove(req.Arg("id")) ? Ok("Removed") : Fail("No such bot");
            case BotCommands.Start:
                return host.Start(req.Arg("id")) ? Ok("Starting") : Fail("No such bot");
            case BotCommands.Stop:
                return host.Stop(req.Arg("id")) ? Ok("Stopped") : Fail("No such bot");
            case BotCommands.Join:
            {
                var res = host.Join(req.Arg("id"), req.Arg("channel"));
                return res is null ? Fail("No such bot")
                     : res.Value ? Ok($"Joining {req.Arg("channel")}")
                     : Ok($"Bot not connected — {req.Arg("channel")} queued for next connect");
            }
            case BotCommands.Part:
            {
                var res = host.Part(req.Arg("id"), req.Arg("channel"));
                return res is null ? Fail("No such bot")
                     : res.Value ? Ok($"Parting {req.Arg("channel")}")
                     : Ok($"Bot not connected — {req.Arg("channel")} removed from queue");
            }
            case BotCommands.Say:
            {
                var res = host.Say(req.Arg("id"), req.Arg("target"), req.Arg("text"));
                return res is null ? Fail("No such bot")
                     : res.Value ? Ok("Sent") : Fail("Bot is not connected");
            }
            // MODE and KICK are fire-and-forget: the server only answers when it
            // refuses. "Ok" here means the command left the bot, not that it took
            // effect — any rejection shows up in the bot activity log.
            case BotCommands.Mode:
            {
                var res = host.Mode(req.Arg("id"), req.Arg("channel"), req.Arg("modes"));
                return res is null ? Fail("No such bot")
                     : res.Value ? Ok("MODE sent (unconfirmed — check bot activity)") : Fail("Bot is not connected");
            }
            case BotCommands.Kick:
            {
                var res = host.Kick(req.Arg("id"), req.Arg("channel"), req.Arg("nick"), req.Arg("reason"));
                return res is null ? Fail("No such bot")
                     : res.Value ? Ok($"KICK {req.Arg("nick")} sent (unconfirmed — check bot activity)") : Fail("Bot is not connected");
            }

            case BotCommands.BanList:
            {
                var (ok, bans) = host.BanList(req.Arg("id"), req.Arg("channel"));
                return ok ? new BotResponse { Ok = true, ChannelBans = bans } : Fail("No such bot");
            }

            case BotCommands.Events:
            {
                long since = long.TryParse(req.Arg("since"), out var c) ? c : 0;
                var (events, cursor) = host.Events.Since(since);
                return new BotResponse { Ok = true, Events = events, Cursor = cursor };
            }

            default:
                return Fail($"Unknown command: {req.Cmd}");
        }
    }

    // Self-signed cert generated fresh each run. Re-imported via PFX so the
    // private key is usable by SslStream as a server on Windows.
    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=IRCBotHost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    private static BotResponse Ok(string msg) => new() { Ok = true, Message = msg };
    private static BotResponse Fail(string err) => new() { Ok = false, Error = err };
}
