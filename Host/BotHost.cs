using System.Collections.Concurrent;
using IRCBot.Shared;

namespace IRCBot.Host;

// Owns the collection of bots and applies control commands to them.
public sealed class BotHost
{
    private readonly ConcurrentDictionary<string, IrcBot> _bots = new();

    public IReadOnlyCollection<BotInfo> List() => _bots.Values.Select(b => b.ToInfo()).ToList();

    // Create a bot, or update it if the id already exists (idempotent so the
    // front end can push its local roster on connect). Returns (ok, message).
    public (bool ok, string message) Upsert(string id, string nick, string host, int port, IEnumerable<string> channels)
    {
        if (string.IsNullOrEmpty(id)) id = Guid.NewGuid().ToString("N")[..8];
        if (_bots.TryGetValue(id, out var existing))
        {
            return existing.UpdateConfig(nick, host, port, channels)
                ? (true, "Bot updated")
                : (false, "Bot is running — stop it before editing");
        }
        _bots[id] = new IrcBot(id, nick, host, port, channels);
        return (true, "Bot added");
    }

    // Edit an existing bot; fails if it doesn't exist.
    public (bool ok, string message) Edit(string id, string nick, string host, int port, IEnumerable<string> channels)
    {
        if (!_bots.TryGetValue(id, out var bot)) return (false, "No such bot");
        return bot.UpdateConfig(nick, host, port, channels)
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
    public bool Join(string id, string channel) => With(id, b => b.Join(channel));
    public bool Part(string id, string channel) => With(id, b => b.Part(channel));
    public bool Say(string id, string target, string text) => With(id, b => b.Say(target, text));

    public void StopAll() { foreach (var b in _bots.Values) b.Stop(); }

    private bool With(string id, Action<IrcBot> action)
    {
        if (_bots.TryGetValue(id, out var bot)) { action(bot); return true; }
        return false;
    }
}
