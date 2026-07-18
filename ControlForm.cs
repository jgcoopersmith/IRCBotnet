using System.Diagnostics;
using System.Text.Json;
using IRCBot.Shared;

namespace IRCBot.ControlApp;

public sealed class ControlForm : Form
{
    private readonly BotControlClient _client = new();

    // Local, persisted roster of bot definitions. This is the source of truth
    // for identity, so bots can be added/edited with no host connection and
    // synced to the host when one is available.
    private readonly List<BotDef> _roster = new();
    private static string RosterPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IRCBot", "bots.json");

    // Bot host process controls
    private readonly Button _launchBtn = new() { Text = "Launch Bot Host", Width = 120 };
    private readonly Label _hostStatus = new() { Text = "Host not running", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(8, 8, 0, 0) };
    private Process? _hostProc;

    // Connection bar — one Host/Port used for both launching and connecting.
    private readonly TextBox _host = new() { Text = "127.0.0.1", Width = 90 };
    private readonly TextBox _port = new() { Text = "6690", Width = 55 };
    private readonly TextBox _pass = new() { Width = 90, UseSystemPasswordChar = true, PlaceholderText = "password" };
    private readonly Button _connectBtn = new() { Text = "Connect", Width = 90 };
    private readonly CheckBox _autoRefresh = new() { Text = "Auto-refresh (2s)", Checked = true, AutoSize = true };
    private readonly Label _status = new() { Text = "Disconnected", AutoSize = true, ForeColor = Color.Firebrick };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };

    // Bots grid
    private readonly ListView _botsView = new()
    {
        View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = false,
        HideSelection = false, Dock = DockStyle.Fill
    };

    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(24, 24, 24), ForeColor = Color.Gainsboro, Font = new Font("Consolas", 9)
    };

    // Second console: live IRC-level activity for the bots (connect, TLS,
    // register, join, errors…), streamed from the host's event log.
    private readonly TextBox _botLog = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(16, 24, 16), ForeColor = Color.FromArgb(170, 230, 170), Font = new Font("Consolas", 9)
    };
    private long _eventCursor;

    // Draggable splitters between the bot list, activity log, and bot activity.
    private SplitContainer? _mainSplit;
    private SplitContainer? _logSplit;

    public ControlForm()
    {
        Text = "IRC Bot Remote Control";
        Width = 940;
        Height = 640;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 500);

        _botsView.CheckBoxes = true; // check bots to run commands across the group
        _botsView.Columns.Add("Nick", 110);
        _botsView.Columns.Add("Server Host", 105);
        _botsView.Columns.Add("Port", 50);
        _botsView.Columns.Add("TLS", 40);
        _botsView.Columns.Add("Status", 85);
        _botsView.Columns.Add("Channels", 185);
        _botsView.Columns.Add("Last Event", 140);
        _botsView.Columns.Add("Id", 65);

        BuildLayout();
        LoadRoster();

        _launchBtn.Click += async (_, _) => await ToggleHostAsync();
        _connectBtn.Click += async (_, _) => await ToggleConnectAsync();
        _autoRefresh.CheckedChanged += (_, _) => { if (_client.IsConnected && _autoRefresh.Checked) _timer.Start(); else _timer.Stop(); };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _botsView.DoubleClick += async (_, _) => await EditBotAsync();
        FormClosing += (_, _) => { _timer.Stop(); _client.Dispose(); StopHost(); };

        // Set the splitter proportions once the form has its real size (setting
        // them in the initializer clamps against the tiny default size).
        Load += (_, _) =>
        {
            try
            {
                if (_mainSplit is { } m && m.Height > m.Panel1MinSize + m.Panel2MinSize + m.SplitterWidth)
                    m.SplitterDistance = (int)(m.Height * 0.55);
                if (_logSplit is { } l && l.Height > l.Panel1MinSize + l.Panel2MinSize + l.SplitterWidth)
                    l.SplitterDistance = (int)(l.Height * 0.5);
            }
            catch { }
        };

        RenderGrid(new());
        Log($"Roster loaded from {RosterPath} ({_roster.Count} bot(s)). Add/edit works offline.");
    }

    private void BuildLayout()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 76, Padding = new Padding(6, 6, 6, 0), WrapContents = true };
        Label Lbl(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(3, 8, 0, 0) };

        // One Bots Host/Port drives both Connect (reach out over TLS) and
        // Launch Bot Host (start a local host on that port, then connect).
        bar.Controls.Add(Lbl("Bots Host:"));
        bar.Controls.Add(_host);
        bar.Controls.Add(Lbl("Port:"));
        bar.Controls.Add(_port);
        bar.Controls.Add(_pass);
        bar.Controls.Add(_connectBtn);
        bar.Controls.Add(_launchBtn);
        bar.SetFlowBreak(_launchBtn, true);
        bar.Controls.Add(_autoRefresh);
        bar.Controls.Add(_status);
        bar.Controls.Add(_hostStatus);

        // Distances are set on Load (below) once the real size is known.
        _mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            SplitterWidth = 6, Panel1MinSize = 120, Panel2MinSize = 150
        };
        var split = _mainSplit;

        var botsPanel = new Panel { Dock = DockStyle.Fill };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 96, WrapContents = true };

        // Row 1 — bot lifecycle and messaging
        AddButton(actions, "☑ All", () => { SetAllChecked(true); return Task.CompletedTask; });
        AddButton(actions, "☐ None", () => { SetAllChecked(false); return Task.CompletedTask; });
        AddButton(actions, "Add Bot…", async () => await AddBotAsync());
        AddButton(actions, "Edit…", async () => await EditBotAsync());
        AddButton(actions, "Start", async () => await RunBatch(BotCommands.Start));
        AddButton(actions, "Stop", async () => await RunBatch(BotCommands.Stop));
        AddButton(actions, "Join…", async () => await BatchJoinPartAsync(BotCommands.Join));
        AddButton(actions, "Part…", async () => await BatchJoinPartAsync(BotCommands.Part));
        AddButton(actions, "Say…", async () => await BatchSayAsync());
        AddButton(actions, "Remove", async () => await RemoveBotsAsync());
        var refreshBtn = AddButton(actions, "Refresh", async () => await RefreshAsync());
        actions.SetFlowBreak(refreshBtn, true); // start channel commands on a new row

        // Row 2 — channel operator commands
        AddButton(actions, "Mode…", async () => await ChannelModeAsync());
        AddButton(actions, "Op", async () => await MemberModeAsync("+o"));
        AddButton(actions, "Deop", async () => await MemberModeAsync("-o"));
        AddButton(actions, "Voice", async () => await MemberModeAsync("+v"));
        AddButton(actions, "Devoice", async () => await MemberModeAsync("-v"));
        AddButton(actions, "Kick…", async () => await KickAsync());
        AddButton(actions, "Bans…", async () => await ManageBansAsync());

        botsPanel.Controls.Add(_botsView);
        botsPanel.Controls.Add(actions);

        split.Panel1.Controls.Add(botsPanel);

        // Two stacked consoles: panel/control activity on top, bot IRC activity below.
        _logSplit = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            SplitterWidth = 6, Panel1MinSize = 60, Panel2MinSize = 60
        };
        _logSplit.Panel1.Controls.Add(WithHeader(_log, "Activity log"));
        _logSplit.Panel2.Controls.Add(WithHeader(_botLog, "Bot connection activity"));
        split.Panel2.Controls.Add(_logSplit);

        Controls.Add(split);
        Controls.Add(bar);
    }

    private static Control WithHeader(Control inner, string title)
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        inner.Dock = DockStyle.Fill;
        panel.Controls.Add(inner);
        panel.Controls.Add(new Label
        {
            Text = title, Dock = DockStyle.Top, Height = 18,
            ForeColor = Color.Gray, Padding = new Padding(4, 2, 0, 0)
        });
        return panel;
    }

    // ── Roster persistence ──────────────────────────────────────────────
    private void LoadRoster()
    {
        try
        {
            if (!File.Exists(RosterPath)) return;
            var defs = JsonSerializer.Deserialize<List<BotDef>>(File.ReadAllText(RosterPath));
            if (defs != null) { _roster.Clear(); _roster.AddRange(defs); }
        }
        catch (Exception ex) { Log($"Could not load roster: {ex.Message}"); }
    }

    private void SaveRoster()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RosterPath)!);
            File.WriteAllText(RosterPath, JsonSerializer.Serialize(_roster, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log($"Could not save roster: {ex.Message}"); }
    }

    // ── Bot host process ────────────────────────────────────────────────
    private async Task ToggleHostAsync()
    {
        if (_hostProc is { HasExited: false }) { StopHost(); return; }

        var exe = FindHostExe();
        if (exe == null)
        {
            using var ofd = new OpenFileDialog { Title = "Locate IRCBotHost.exe", Filter = "IRCBotHost.exe|IRCBotHost.exe|Executables|*.exe" };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            exe = ofd.FileName;
        }
        if (!int.TryParse(_port.Text, out var controlPort)) { Warn("Invalid port"); return; }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)!
            };
            psi.ArgumentList.Add(controlPort.ToString());
            if (!string.IsNullOrEmpty(_pass.Text)) psi.ArgumentList.Add(_pass.Text);

            _hostProc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _hostProc.OutputDataReceived += (_, e) => { if (e.Data != null) UI(() => Log("[host] " + e.Data)); };
            _hostProc.ErrorDataReceived  += (_, e) => { if (e.Data != null) UI(() => Log("[host!] " + e.Data)); };
            _hostProc.Exited += (_, _) => UI(() =>
            {
                Log("Bot host exited");
                _launchBtn.Text = "Launch Bot Host";
                SetHostStatus("Host not running", false);
            });
            _hostProc.Start();
            _hostProc.BeginOutputReadLine();
            _hostProc.BeginErrorReadLine();

            _launchBtn.Text = "Stop Bot Host";
            SetHostStatus($"Host running (control {controlPort})", true);
            Log($"Launched {Path.GetFileName(exe)} {controlPort}");

            for (int i = 0; i < 12 && !_client.IsConnected; i++)
            {
                await Task.Delay(300);
                if (await ConnectAsync()) break;
            }
        }
        catch (Exception ex) { Warn($"Failed to launch bot host: {ex.Message}"); }
    }

    private void StopHost()
    {
        try
        {
            if (_client.IsConnected)
            {
                _timer.Stop(); _client.Dispose();
                SetStatus("Disconnected", false); _connectBtn.Text = "Connect";
            }
            if (_hostProc is { HasExited: false })
            {
                _hostProc.Kill(entireProcessTree: true);
                _hostProc.WaitForExit(2000);
            }
        }
        catch { }
        finally
        {
            _hostProc?.Dispose();
            _hostProc = null;
            if (!IsDisposed) { _launchBtn.Text = "Launch Bot Host"; SetHostStatus("Host not running", false); }
        }
    }

    private static string? FindHostExe()
    {
        var baseDir = AppContext.BaseDirectory;

        // 1) Bundled alongside the app (how it ships to other machines).
        foreach (var candidate in new[]
        {
            Path.Combine(baseDir, "BotHost", "IRCBotHost.exe"),
            Path.Combine(baseDir, "IRCBotHost.exe")
        })
            if (File.Exists(candidate)) return candidate;

        // 2) Dev tree: search up for the sibling Host build output.
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var hostBin = Path.Combine(dir.FullName, "Host", "bin");
            if (Directory.Exists(hostBin))
            {
                var exe = Directory.GetFiles(hostBin, "IRCBotHost.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (exe != null) return exe;
            }
        }
        return null;
    }

    // ── Connection ──────────────────────────────────────────────────────
    private async Task ToggleConnectAsync()
    {
        if (_client.IsConnected)
        {
            DisconnectUi("Disconnected");
            return;
        }
        await ConnectAsync();
    }

    // Tear down the control connection in the UI (manual disconnect or a drop).
    private void DisconnectUi(string reason)
    {
        _timer.Stop();
        _client.Dispose();
        if (IsDisposed) return;
        SetStatus("Disconnected", false);
        _connectBtn.Text = "Connect";
        Log(reason);
        RenderGrid(new());
    }

    private async Task<bool> ConnectAsync()
    {
        try
        {
            if (!int.TryParse(_port.Text, out var port)) { Warn("Invalid port"); return false; }
            await _client.ConnectAsync(_host.Text.Trim(), port, _pass.Text);
            SetStatus($"🔒 Connected (TLS) to {_host.Text}:{port}", true);
            _connectBtn.Text = "Disconnect";
            Log($"Connected over TLS to bots at {_host.Text}:{port}");
            _eventCursor = 0;              // pull recent bot-activity history
            _botLog.Clear();
            await SyncWithHostAsync();
            if (_autoRefresh.Checked) _timer.Start();
            await RefreshAsync();
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("Disconnected", false);
            Log($"Connection failed: {ex.Message}");
            return false;
        }
    }

    // Push local defs the host doesn't have, and import host bots we don't know.
    private async Task SyncWithHostAsync()
    {
        try
        {
            var r = await _client.SimpleAsync(BotCommands.List);
            var hostBots = r.Bots ?? new();
            var hostIds = hostBots.Select(b => b.Id).ToHashSet();

            foreach (var d in _roster.ToList())
                if (!hostIds.Contains(d.Id))
                    await _client.ActionAsync(BotCommands.Add, d.ToArgs());

            bool imported = false;
            foreach (var b in hostBots)
                if (_roster.All(d => d.Id != b.Id))
                {
                    _roster.Add(BotDef.FromInfo(b));
                    imported = true;
                }
            if (imported) SaveRoster();
            Log($"Synced roster with host ({_roster.Count} bot(s)).");
        }
        catch (Exception ex) { Log($"Sync error: {ex.Message}"); }
    }

    // ── Refresh / render ────────────────────────────────────────────────
    private async Task RefreshAsync()
    {
        Dictionary<string, BotInfo> live = new();
        if (_client.IsConnected)
        {
            try
            {
                var r = await _client.SimpleAsync(BotCommands.List);
                if (r.Bots != null) live = r.Bots.ToDictionary(b => b.Id);

                // Drain new bot-activity events into the second console.
                var er = await _client.ActionAsync(BotCommands.Events, ("since", _eventCursor.ToString()));
                if (er.Events != null)
                {
                    foreach (var e in er.Events)
                        _botLog.AppendText($"[{e.Utc.ToLocalTime():HH:mm:ss}] {e.Nick}: {e.Text}{Environment.NewLine}");
                    _eventCursor = er.Cursor;
                }
            }
            catch (Exception ex)
            {
                // A dropped host connection stops responding — tear down instead
                // of logging the same error every refresh tick.
                if (!_client.IsConnected) { DisconnectUi($"Lost connection to bots: {ex.Message}"); return; }
                Log($"Refresh error: {ex.Message}");
            }
        }
        RenderGrid(live);
    }

    private void RenderGrid(Dictionary<string, BotInfo> live)
    {
        var selected = SelectedId();
        var checkedIds = _botsView.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToHashSet();
        _botsView.BeginUpdate();
        _botsView.Items.Clear();
        foreach (var d in _roster.OrderBy(d => d.Nick, StringComparer.OrdinalIgnoreCase))
        {
            live.TryGetValue(d.Id, out var info);
            var status = info?.Status.ToString() ?? (_client.IsConnected ? "Not on host" : "Offline");
            var channels = info != null ? string.Join(", ", info.Channels) : string.Join(", ", d.Channels);
            var item = new ListViewItem(new[]
            {
                d.Nick, d.Host, d.Port.ToString(), d.UseTls ? "🔒" : "", status, channels, info?.LastEvent ?? "", d.Id
            }) { Tag = d.Id };
            item.ForeColor = info?.Status switch
            {
                BotStatus.Connected => Color.ForestGreen,
                BotStatus.Connecting => Color.DarkOrange,
                BotStatus.Error => Color.Firebrick,
                _ => Color.DimGray
            };
            _botsView.Items.Add(item);
            if (checkedIds.Contains(d.Id)) item.Checked = true;
            if (d.Id == selected) item.Selected = true;
        }
        _botsView.EndUpdate();
    }

    // ── Bot actions ─────────────────────────────────────────────────────
    private async Task RunAction(Task<BotResponse> call)
    {
        try
        {
            var r = await call;
            Log(r.Ok ? "✓ " + (r.Message ?? "OK") : "✗ " + (r.Error ?? "Error"));
            await RefreshAsync();
        }
        catch (Exception ex) { Log("✗ " + ex.Message); }
    }

    // Run a command across the checked bots (or the selected row if none checked).
    private async Task RunBatch(string cmd, params (string, string)[] extra)
    {
        var ids = TargetIds();
        if (ids.Count == 0) { Warn("Check one or more bots, or select a row"); return; }
        if (!_client.IsConnected) { Warn("Connect to a bot host to control bots"); return; }

        int ok = 0, fail = 0;
        foreach (var id in ids)
        {
            try
            {
                var args = new List<(string, string)> { ("id", id) };
                args.AddRange(extra);
                var r = await _client.ActionAsync(cmd, args.ToArray());
                if (r.Ok) ok++; else { fail++; Log($"✗ {NickOf(id)}: {r.Error}"); }
            }
            catch (Exception ex) { fail++; Log($"✗ {NickOf(id)}: {ex.Message}"); }
        }
        Log($"{cmd} → {ok} ok{(fail > 0 ? $", {fail} failed" : "")} (across {ids.Count} bot(s))");
        await RefreshAsync();
    }

    private async Task BatchJoinPartAsync(string cmd)
    {
        var ids = TargetIds();
        if (ids.Count == 0) { Warn("Check one or more bots, or select a row"); return; }
        var channel = Prompt($"{cmd} channel for {ids.Count} bot(s) (e.g. #test):", "#test");
        if (string.IsNullOrWhiteSpace(channel)) return;
        await RunBatch(cmd, ("channel", channel));
    }

    private async Task BatchSayAsync()
    {
        var ids = TargetIds();
        if (ids.Count == 0) { Warn("Check one or more bots, or select a row"); return; }
        var target = Prompt("Target (channel or nick):", "#test");
        if (string.IsNullOrWhiteSpace(target)) return;
        var text = Prompt($"Message from {ids.Count} bot(s):", "");
        if (string.IsNullOrEmpty(text)) return;
        await RunBatch(BotCommands.Say, ("target", target), ("text", text));
    }

    // Set an arbitrary channel mode string (e.g. +m, +t, -s, +l 20).
    private async Task ChannelModeAsync()
    {
        var ids = TargetIds();
        if (ids.Count == 0) { Warn("Check one or more bots, or select a row"); return; }
        var channel = Prompt("Channel:", "#test");
        if (string.IsNullOrWhiteSpace(channel)) return;
        var modes = Prompt("Modes (e.g. +m, +t, -s, +l 20):", "+t");
        if (string.IsNullOrWhiteSpace(modes)) return;
        await RunBatch(BotCommands.Mode, ("channel", channel), ("modes", modes));
    }

    // Op/deop/voice/devoice on a channel (flag is +o/-o/+v/-v).
    //  - If bots are checked: ask only for the channel and apply the mode to the
    //    checked bots' own nicks (each checked bot issues it, so whichever holds
    //    op applies it to the whole group).
    //  - If nothing is checked: fall back to targeting an arbitrary nick via the
    //    highlighted bot.
    private async Task MemberModeAsync(string flag)
    {
        if (!_client.IsConnected) { Warn("Connect to a bot host first"); return; }
        var sign = flag[0];
        var modeChar = flag[1];

        var checkedIds = CheckedIds();
        if (checkedIds.Count > 0)
        {
            var channel = Prompt($"Channel to {flag} the {checkedIds.Count} selected bot(s) in:", "#test");
            if (string.IsNullOrWhiteSpace(channel)) return;

            var nicks = checkedIds.Select(NickOf).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (nicks.Count == 0) return;
            // e.g. "+ooo Alice Bob Carol"
            var modes = $"{sign}{new string(modeChar, nicks.Count)} {string.Join(" ", nicks)}";
            await RunBatch(BotCommands.Mode, ("channel", channel), ("modes", modes));
            return;
        }

        var id = SelectedId();
        if (id == null) { Warn("Check bots to target them, or select a bot to issue the command"); return; }
        var ch = Prompt("Channel:", "#test");
        if (string.IsNullOrWhiteSpace(ch)) return;
        var nick = Prompt($"Nick to {flag}:", "");
        if (string.IsNullOrWhiteSpace(nick)) return;
        await RunAction(_client.ActionAsync(BotCommands.Mode, ("id", id), ("channel", ch), ("modes", $"{flag} {nick}")));
    }

    // Kick a nick from a channel, issued across the checked bots (or the row).
    private async Task KickAsync()
    {
        var ids = TargetIds();
        if (ids.Count == 0) { Warn("Check one or more bots, or select a row"); return; }
        if (!_client.IsConnected) { Warn("Connect to a bot host first"); return; }
        var channel = Prompt("Channel:", "#test");
        if (string.IsNullOrWhiteSpace(channel)) return;
        var nick = Prompt("Nick to kick:", "");
        if (string.IsNullOrWhiteSpace(nick)) return;
        var reason = Prompt("Reason (optional):", "Kicked");
        if (reason == null) return;
        await RunBatch(BotCommands.Kick, ("channel", channel), ("nick", nick), ("reason", reason));
    }

    // View / add / remove channel bans through one bot's eyes.
    private async Task ManageBansAsync()
    {
        var ids = TargetIds();
        if (ids.Count != 1) { Warn("Select or check exactly one bot to manage bans"); return; }
        if (!_client.IsConnected) { Warn("Connect to a bot host first"); return; }
        var channel = Prompt("Channel to manage bans for:", "#test");
        if (string.IsNullOrWhiteSpace(channel)) return;

        using var dlg = new BanManagerDialog(_client, ids[0], NickOf(ids[0]), channel.Trim());
        dlg.ShowDialog(this);
        await RefreshAsync();
    }

    // Add works offline: writes to the local roster, and pushes to the host if connected.
    private async Task AddBotAsync()
    {
        using var dlg = new AddBotDialog("Add Bot");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(dlg.Nick)) { Warn("Nick is required"); return; }

        var def = new BotDef
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Nick = dlg.Nick,
            Host = dlg.HostName,
            Port = int.TryParse(dlg.Port, out var p) ? p : 6667,
            UseTls = dlg.UseTls,
            Password = dlg.Password,
            Ident = dlg.Ident,
            RealName = dlg.RealName,
            CtcpVersion = dlg.CtcpVersion,
            Channels = SplitChannels(dlg.Channels)
        };
        _roster.Add(def);
        SaveRoster();
        Log($"Added bot {def.Nick} (local roster).");

        if (_client.IsConnected)
            await RunAction(_client.ActionAsync(BotCommands.Add, def.ToArgs()));
        else
            RenderGrid(new());
    }

    // Edit works offline too. If connected and the bot is running, the host
    // rejects the edit and asks you to stop it first.
    private async Task EditBotAsync()
    {
        // Edit is single-target: prefer the highlighted row, else the sole
        // checked bot. Editing more than one at a time isn't meaningful.
        var id = SelectedId();
        if (id == null)
        {
            var ids = TargetIds();
            if (ids.Count == 1) id = ids[0];
            else if (ids.Count > 1) { Warn("Editing is one bot at a time — check just one, or click a row"); return; }
        }
        if (id == null) { Warn("Select or check a bot to edit"); return; }
        var def = _roster.FirstOrDefault(d => d.Id == id);
        if (def == null) return;

        using var dlg = new AddBotDialog("Edit Bot", def.Nick, def.Host, def.Port.ToString(),
            def.UseTls, def.Password, def.Ident, def.RealName, def.CtcpVersion, string.Join(", ", def.Channels));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(dlg.Nick)) { Warn("Nick is required"); return; }

        def.Nick = dlg.Nick;
        def.Host = dlg.HostName;
        def.Port = int.TryParse(dlg.Port, out var p) ? p : 6667;
        def.UseTls = dlg.UseTls;
        def.Password = dlg.Password;
        def.Ident = dlg.Ident;
        def.RealName = dlg.RealName;
        def.CtcpVersion = dlg.CtcpVersion;
        def.Channels = SplitChannels(dlg.Channels);
        SaveRoster();
        Log($"Edited bot {def.Nick} (local roster).");

        if (_client.IsConnected)
            await RunAction(_client.ActionAsync(BotCommands.Edit, def.ToArgs()));
        else
            RenderGrid(new());
    }

    // Remove works offline across the checked bots (or the selected row).
    private async Task RemoveBotsAsync()
    {
        var ids = TargetIds();
        if (ids.Count == 0) { Warn("Check one or more bots, or select a row"); return; }
        if (ids.Count > 1 &&
            MessageBox.Show(this, $"Remove {ids.Count} bots from the roster?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        foreach (var id in ids) _roster.RemoveAll(d => d.Id == id);
        SaveRoster();
        Log($"Removed {ids.Count} bot(s) from roster.");

        if (_client.IsConnected)
            foreach (var id in ids)
                try { await _client.ActionAsync(BotCommands.Remove, ("id", id)); } catch { }

        await RefreshAsync();
    }

    // ── Helpers ─────────────────────────────────────────────────────────
    private static List<string> SplitChannels(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private string? SelectedId() =>
        _botsView.SelectedItems.Count == 0 ? null : _botsView.SelectedItems[0].Tag as string;

    // The bots a command targets: all checked, or the selected row if none checked.
    private List<string> TargetIds()
    {
        var ids = CheckedIds();
        if (ids.Count > 0) return ids;
        var sel = SelectedId();
        return sel != null ? new List<string> { sel } : new();
    }

    // Only the checked bots (no fallback to the highlighted row).
    private List<string> CheckedIds() =>
        _botsView.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();

    private void SetAllChecked(bool value)
    {
        foreach (ListViewItem it in _botsView.Items) it.Checked = value;
    }

    private string NickOf(string id) => _roster.FirstOrDefault(d => d.Id == id)?.Nick ?? id;

    private static Button AddButton(Control parent, string text, Func<Task> onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3) };
        b.Click += async (_, _) => await onClick();
        parent.Controls.Add(b);
        return b;
    }

    private void UI(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { if (InvokeRequired) BeginInvoke(a); else a(); } catch { }
    }

    private void SetStatus(string text, bool connected)
    {
        _status.Text = text;
        _status.ForeColor = connected ? Color.ForestGreen : Color.Firebrick;
    }

    private void SetHostStatus(string text, bool running)
    {
        _hostStatus.Text = text;
        _hostStatus.ForeColor = running ? Color.ForestGreen : Color.Gray;
    }

    private void Log(string msg) => _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
    private void Warn(string msg) => MessageBox.Show(this, msg, "IRC Bot Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private string? Prompt(string label, string def)
    {
        using var dlg = new Form { Text = "IRC Bot Control", Width = 440, Height = 180, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var lbl = new Label { Text = label, Left = 12, Top = 12, Width = 400 };
        var box = new TextBox { Left = 12, Top = 44, Width = 400, Text = def };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 256, Top = 90, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 337, Top = 90, Width = 75 };
        dlg.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        return dlg.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }
}

// A persisted bot definition owned by the front end. Each bot carries its own
// full server connection settings.
public sealed class BotDef
{
    public string Id { get; set; } = "";
    public string Nick { get; set; } = "";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6667;
    public bool UseTls { get; set; }
    public string Password { get; set; } = "";
    public string Ident { get; set; } = "";
    public string RealName { get; set; } = "";
    public string CtcpVersion { get; set; } = "Hihi!";
    public List<string> Channels { get; set; } = new();

    public (string, string)[] ToArgs() => new[]
    {
        ("id", Id), ("nick", Nick), ("host", Host), ("port", Port.ToString()),
        ("tls", UseTls ? "true" : "false"), ("password", Password),
        ("ident", Ident), ("realname", RealName), ("ctcpversion", CtcpVersion),
        ("channels", string.Join(",", Channels))
    };

    public static BotDef FromInfo(BotInfo b) => new()
    {
        Id = b.Id, Nick = b.Nick, Host = b.Host, Port = b.Port, UseTls = b.UseTls,
        Ident = b.Ident, RealName = b.RealName,
        CtcpVersion = string.IsNullOrEmpty(b.CtcpVersion) ? "Hihi!" : b.CtcpVersion,
        Channels = b.Channels.ToList()
    };
}

// Dialog for creating or editing a bot with its own full connection settings.
public sealed class AddBotDialog : Form
{
    private readonly TextBox _nick = new() { Width = 250 };
    private readonly TextBox _host = new() { Width = 250 };
    private readonly TextBox _port = new() { Width = 250 };
    private readonly CheckBox _tls = new() { Text = "Use TLS/SSL", AutoSize = true };
    private readonly TextBox _pass = new() { Width = 250, UseSystemPasswordChar = true };
    private readonly TextBox _ident = new() { Width = 250 };
    private readonly TextBox _real = new() { Width = 250 };
    private readonly TextBox _ctcp = new() { Width = 250 };
    private readonly TextBox _channels = new() { Width = 250 };

    public string Nick => _nick.Text.Trim();
    public string HostName => _host.Text.Trim();
    public string Port => _port.Text.Trim();
    public bool UseTls => _tls.Checked;
    public string Password => _pass.Text;
    public string Ident => _ident.Text.Trim();
    public string RealName => _real.Text.Trim();
    public string CtcpVersion => _ctcp.Text;
    public string Channels => _channels.Text.Trim();

    public AddBotDialog(string title = "Add Bot", string nick = "MyBot", string host = "localhost",
        string port = "6667", bool tls = false, string password = "", string ident = "",
        string realName = "", string ctcpVersion = "Hihi!", string channels = "#test")
    {
        Text = title;
        Width = 420; Height = 382;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;

        _nick.Text = nick; _host.Text = host; _port.Text = port; _tls.Checked = tls;
        _pass.Text = password; _ident.Text = ident; _real.Text = realName;
        _ctcp.Text = ctcpVersion; _channels.Text = channels;

        int y = 14;
        Label Row(string label, Control field)
        {
            var lbl = new Label { Text = label, Left = 12, Top = y + 3, Width = 120 };
            field.Left = 140; field.Top = y;
            y += 32;
            return lbl;
        }

        var lNick = Row("Nick:", _nick);
        var lHost = Row("Server host:", _host);
        var lPort = Row("Server port:", _port);
        _tls.Left = 140; _tls.Top = y; y += 30;
        var lPass = Row("Server password:", _pass);
        var lIdent = Row("Ident (optional):", _ident);
        var lReal = Row("Real name (optional):", _real);
        var lCtcp = Row("CTCP version reply:", _ctcp);
        var lChan = Row("Channels (csv):", _channels);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Left = 234, Top = y + 6, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 315, Top = y + 6, Width = 75 };

        Controls.AddRange(new Control[]
        {
            lNick, _nick, lHost, _host, lPort, _port, _tls,
            lPass, _pass, lIdent, _ident, lReal, _real, lCtcp, _ctcp, lChan, _channels,
            ok, cancel
        });
        AcceptButton = ok; CancelButton = cancel;
    }
}

// View and edit a channel's +b ban list through one bot. Ban/unban go out as
// MODE +b/-b; the list is fetched from the server (367/368) via BANLIST.
public sealed class BanManagerDialog : Form
{
    private readonly BotControlClient _client;
    private readonly string _botId;
    private readonly TextBox _channel;
    private readonly ListView _list;
    private readonly Label _status;

    public BanManagerDialog(BotControlClient client, string botId, string botNick, string channel)
    {
        _client = client;
        _botId = botId;

        Text = $"Channel bans — via {botNick}";
        Width = 560; Height = 420;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        _channel = new TextBox { Left = 70, Top = 12, Width = 200, Text = channel };
        var listBtn = new Button { Text = "List", Left = 278, Top = 10, Width = 70 };
        _status = new Label { Left = 356, Top = 15, Width = 180, Text = "" };

        _list = new ListView
        {
            Left = 12, Top = 44, Width = 524, Height = 280, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = false
        };
        _list.Columns.Add("Mask", 260);
        _list.Columns.Add("Set by", 130);
        _list.Columns.Add("Set at", 120);

        var banBtn = new Button { Text = "Ban mask…", Left = 12, Top = 332, Width = 100, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        var unbanBtn = new Button { Text = "Remove", Left = 118, Top = 332, Width = 90, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        var closeBtn = new Button { Text = "Close", DialogResult = DialogResult.OK, Left = 456, Top = 332, Width = 80, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };

        listBtn.Click += async (_, _) => await RefreshBansAsync();
        banBtn.Click += async (_, _) => await BanAsync();
        unbanBtn.Click += async (_, _) => await UnbanAsync();

        Controls.AddRange(new Control[]
        {
            new Label { Text = "Channel:", Left = 12, Top = 15, Width = 55 },
            _channel, listBtn, _status, _list, banBtn, unbanBtn, closeBtn
        });
        AcceptButton = closeBtn;
        Load += async (_, _) => await RefreshBansAsync();
    }

    private async Task RefreshBansAsync()
    {
        var ch = _channel.Text.Trim();
        if (string.IsNullOrWhiteSpace(ch)) return;
        try
        {
            _status.Text = "Fetching…";
            // First call triggers the server query; wait, then read the cache.
            await _client.ActionAsync(BotCommands.BanList, ("id", _botId), ("channel", ch));
            await Task.Delay(800);
            var r = await _client.ActionAsync(BotCommands.BanList, ("id", _botId), ("channel", ch));

            _list.Items.Clear();
            if (r.ChannelBans != null)
                foreach (var b in r.ChannelBans)
                    _list.Items.Add(new ListViewItem(new[] { b.Mask, b.SetBy, b.SetAt }));
            _status.Text = $"{_list.Items.Count} ban(s)";
        }
        catch (Exception ex) { _status.Text = "Error"; MessageBox.Show(this, ex.Message, "Bans"); }
    }

    private async Task BanAsync()
    {
        var ch = _channel.Text.Trim();
        var mask = ShowPrompt("Ban mask (e.g. nick!*@*, *!*@host):", "*!*@*");
        if (string.IsNullOrWhiteSpace(mask)) return;
        await _client.ActionAsync(BotCommands.Mode, ("id", _botId), ("channel", ch), ("modes", $"+b {mask}"));
        await Task.Delay(300);
        await RefreshBansAsync();
    }

    private async Task UnbanAsync()
    {
        if (_list.SelectedItems.Count == 0) { MessageBox.Show(this, "Select a ban to remove", "Bans"); return; }
        var ch = _channel.Text.Trim();
        var mask = _list.SelectedItems[0].SubItems[0].Text;
        await _client.ActionAsync(BotCommands.Mode, ("id", _botId), ("channel", ch), ("modes", $"-b {mask}"));
        await Task.Delay(300);
        await RefreshBansAsync();
    }

    private string? ShowPrompt(string label, string def)
    {
        using var dlg = new Form { Text = "Ban mask", Width = 440, Height = 170, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var lbl = new Label { Text = label, Left = 12, Top = 12, Width = 400 };
        var box = new TextBox { Left = 12, Top = 40, Width = 400, Text = def };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 256, Top = 80, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 337, Top = 80, Width = 75 };
        dlg.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        return dlg.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
    }
}
