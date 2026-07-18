using System.Collections.Concurrent;
using IRCBot.Shared;

namespace IRCBot.Host;

// Owns the collection of bots and applies control commands to them.
public sealed class BotHost
{
    private readonly ConcurrentDictionary<string, IrcBot> _bots = new();

    // Shared, ordered log of bot activity that the panel streams via EVENTS.
    public EventLog Events { get; } = new();

    public IReadOnlyCollection<BotInfo> List() => _bots.Values.Select(b => b.ToInfo()).ToList();

    // Create a bot, or update it if the id already exists (idempotent so the
    // front end can push its local roster on connect). Returns (ok, message).
    public (bool ok, string message) Upsert(string id, BotConfig cfg)
    {
        if (string.IsNullOrEmpty(id)) id = Guid.NewGuid().ToString("N")[..8];
        if (_bots.TryGetValue(id, out var existing))
        {
            return existing.UpdateConfig(cfg)
                ? (true, "Bot updated")
                : (false, "Bot is running — stop it before editing");
        }
        _bots[id] = new IrcBot(id, cfg, Events);
        return (true, "Bot added");
    }

    // Edit an existing bot; fails if it doesn't exist.
    public (bool ok, string message) Edit(string id, BotConfig cfg)
    {
        if (!_bots.TryGetValue(id, out var bot)) return (false, "No such bot");
        return bot.UpdateConfig(cfg)
            ? (true, "Bot updated")
            : (false, "Bot is running — stop it before editing");
    }

    public bool Remove(string id)
    {
        if (_bots.TryRemove(id, out var bot)) { bot.Stop(); return true; }
        return false;
    }

    public bool Start(string id) => With(id, b => b.Start());
    public bool Stop(string id) => With(id, b => b.Stop());

    // null = no such bot; true = acted while connected; false = bot not connected.
    public bool? Join(string id, string channel) => WithResult(id, b => b.Join(channel));
    public bool? Part(string id, string channel) => WithResult(id, b => b.Part(channel));
    public bool? Say(string id, string target, string text) => WithResult(id, b => b.Say(target, text));
    public bool? Mode(string id, string channel, string modes) => WithResult(id, b => b.Mode(channel, modes));
    public bool? Kick(string id, string channel, string nick, string reason) => WithResult(id, b => b.Kick(channel, nick, reason));

    // Return the bot's cached ban list for a channel and trigger a fresh fetch.
    public (bool ok, List<ChannelBan> bans) BanList(string id, string channel)
    {
        if (_bots.TryGetValue(id, out var b))
        {
            var bans = b.GetBans(channel);
            b.RequestBanList(channel);
            return (true, bans);
        }
        return (false, new());
    }

    public void StopAll() { foreach (var b in _bots.Values) b.Stop(); }

    private bool With(string id, Action<IrcBot> action)
    {
        if (_bots.TryGetValue(id, out var bot)) { action(bot); return true; }
        return false;
    }

    private bool? WithResult(string id, Func<IrcBot, bool> action) =>
        _bots.TryGetValue(id, out var bot) ? action(bot) : null;
}

// Thread-safe, capped, ordered ring buffer of bot activity. Each event gets a
// monotonic sequence number so the panel can pull "everything since cursor".
public sealed class EventLog
{
    private const int Capacity = 1000;
    private readonly object _lock = new();
    private readonly Queue<BotEvent> _events = new();
    private long _seq;

    public void Append(string botId, string nick, string text)
    {
        lock (_lock)
        {
            _events.Enqueue(new BotEvent { Seq = ++_seq, BotId = botId, Nick = nick, Text = text, Utc = DateTime.UtcNow });
            while (_events.Count > Capacity) _events.Dequeue();
        }
    }

    public (List<BotEvent> events, long cursor) Since(long cursor)
    {
        lock (_lock)
        {
            var list = _events.Where(e => e.Seq > cursor).OrderBy(e => e.Seq).ToList();
            return (list, _seq);
        }
    }
}
