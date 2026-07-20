using System.Text.Json;
using System.Text.RegularExpressions;
using IRCBot.Shared;

namespace IRCBot.Host;

// The auto list: hostmask rules the bots enforce on join (+o auto-op,
// +v auto-voice, +k auto-kick). The host owns this list so enforcement does
// not depend on the control panel being connected; the panel just edits it.
public sealed class AutoRules
{
    private readonly object _lock = new();
    private List<AutoEntry> _entries = new();
    private readonly string _storePath;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IRCBot");

    // Per-port file so two hosts on one account keep separate lists. The old
    // unversioned "auto.json" is migrated in on first load.
    private static string LegacyPath => Path.Combine(Dir, "auto.json");

    public AutoRules(int controlPort = 6690)
    {
        _storePath = Path.Combine(Dir, $"auto-{controlPort}.json");
        Load();
    }

    public List<AutoEntry> All()
    {
        lock (_lock) return _entries.Select(Copy).ToList();
    }

    public void Replace(IEnumerable<AutoEntry> entries)
    {
        lock (_lock)
            _entries = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Mask))
                .Select(Copy)
                .ToList();
        Save();
    }

    // Flags that apply to a user in a channel, merged across every matching
    // rule, so a global rule and a channel-specific one combine.
    public string FlagsFor(string prefix, string channel)
    {
        var flags = "";
        lock (_lock)
            foreach (var e in _entries)
            {
                if (e.Channel != "*" && !string.Equals(e.Channel, channel, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!MaskMatches(e.Mask, prefix)) continue;
                foreach (var c in e.Flags)
                {
                    var lower = char.ToLowerInvariant(c);
                    if (lower is 'o' or 'v' or 'k' && !flags.Contains(lower)) flags += lower;
                }
            }
        return flags;
    }

    // Match a mask against a full "nick!user@host". A full nick!user@host mask
    // matches the whole thing; the common shorthands are also accepted so a rule
    // reads the way people actually write it:
    //   user@host  → matched against the user@host part (nick ignored)
    //   nick       → matched against the nick alone
    public static bool MaskMatches(string mask, string target)
    {
        if (string.IsNullOrEmpty(mask)) return false;
        if (Glob(mask, target)) return true;
        if (mask.Contains('!')) return false; // a full mask must match in full

        int bang = target.IndexOf('!');
        if (mask.Contains('@'))
        {
            var userHost = bang >= 0 ? target[(bang + 1)..] : target;
            return Glob(mask, userHost);
        }
        var nick = bang >= 0 ? target[..bang] : target;
        return Glob(mask, nick);
    }

    // IRC-style glob: '*' spans any run, '?' one character, case-insensitive.
    private static bool Glob(string mask, string value)
    {
        var pattern = "^" + Regex.Escape(mask).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        try { return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase); }
        catch { return false; }
    }

    private static AutoEntry Copy(AutoEntry e) => new()
    {
        Mask = e.Mask.Trim(),
        Channel = string.IsNullOrWhiteSpace(e.Channel) ? "*" : e.Channel.Trim(),
        Flags = new string(e.Flags.ToLowerInvariant().Where(c => c is 'o' or 'v' or 'k').Distinct().ToArray())
    };

    private void Load()
    {
        try
        {
            // Prefer this host's own file; fall back to the legacy shared file so
            // an existing list carries over on the first run after this change.
            var path = File.Exists(_storePath) ? _storePath
                     : File.Exists(LegacyPath) ? LegacyPath
                     : null;
            if (path == null) return;
            var list = JsonSerializer.Deserialize<List<AutoEntry>>(File.ReadAllText(path));
            if (list != null) lock (_lock) _entries = list.Select(Copy).ToList();
        }
        catch { /* a corrupt or unreadable store must not stop the host */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            List<AutoEntry> snapshot;
            lock (_lock) snapshot = _entries.Select(Copy).ToList();
            File.WriteAllText(_storePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
