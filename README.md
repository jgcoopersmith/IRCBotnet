# IRCBot

Simple IRC bots with a remote-control WinForms front end. A **bot host** runs
any number of bots (each a client connection to an IRC server) and exposes a
loopback-only, **TLS-encrypted** JSON control endpoint on a configurable port.
The **control front end** reaches out to that endpoint over TLS to see and
drive the bots: add/remove, start/stop, join/part, send messages, and watch
live status. You point the panel at the bots — never at an IRC server.

Designed to work with the local [IRCServer](https://github.com/joc00p/IRCServer),
but any IRC server works. Each bot has its own independent connection settings —
host, port, TLS/SSL, server password, ident, real name, and CTCP VERSION reply
(default `Hihi!`) — so different bots can point at different servers at once.

Bots auto-respond to CTCP VERSION requests with their configured reply. Runtime
commands (Start/Stop/Join/Part/Say/Mode) report back when a bot isn't connected
rather than silently succeeding — Say/Mode fail with "Bot is not connected",
while Join/Part queue the channel for the next connect.

## Projects

| Project | Output | Description |
|---------|--------|-------------|
| *(root)* | WinForms exe | `IRCBotControl` — remote-control front end (default run target) |
| `Host/` | console exe | `IRCBotHost` — runs the bots + loopback control port |
| `Shared/` | library | `IRCBot.Shared` — DTOs and JSON control protocol |

## Build

```
dotnet build IRCBot.slnx -c Release
```

Building the control app also builds the bots host and bundles it into a
`BotHost\` subfolder of the control app's output. So to deploy to another
machine, copy the control app's output folder (e.g.
`bin\Release\net10.0-windows\`) as a whole — **Launch Bot Host** finds the
bundled `BotHost\IRCBotHost.exe` there. (The target machine needs the .NET 10
Windows Desktop runtime installed.)

## Run (control front end)

From the repo root, with no arguments:

```
dotnet run
```

This opens the control panel. From there:

1. Click **Add Bot…**, give it a nick and its **own server connection**
   (host, port, optional TLS/SSL, server password, ident, real name) plus
   initial channels. **This works with no host connection** — bots are stored
   in a local roster.
2. **Edit…** (or double-click a row) to change any of a bot's settings, also
   offline.
3. Set the **Control Port** (default `6690`) and click **Launch Bot Host** — it
   starts the host process and auto-connects. (Or start `IRCBotHost` yourself
   and click **Connect**.) On connect, your roster is synced to the host and any
   bots already on the host are imported into your roster.
4. Use **Start / Stop / Join / Part / Say / Remove** (these act on the running
   host, so they need a connection).

### Group commands across bots

Each bot row has a checkbox. Commands run across **all checked bots** — so you
can tick every bot and **Start** them at once, or tick a subset and **Join** a
channel with just those. **☑ All** / **☐ None** toggle the whole list. When no
boxes are checked, a command falls back to the single highlighted row. Join/Part
prompt once for the channel and Say once for the message, then apply to the
whole group; per-bot failures are reported in the log.

A second button row provides **channel operator commands**: **Mode…** (any
channel mode string, e.g. `+m`, `+t`, `+l 20`), **Op** / **Deop** (`+o`/`-o`),
**Voice** / **Devoice** (`+v`/`-v`), **Kick…**, and **Bans…**. **Kick…** prompts
for a channel, a nick, and an optional reason, and each targeted bot issues the
`KICK` (whichever holds op carries it out).

For **Op / Deop / Voice / Devoice**: if one or more bots are **checked**, you're
asked only for the channel, and the mode is applied to the checked bots' own
nicks (each checked bot issues the combined `MODE`, so whichever holds op applies
it to the whole group). If nothing is checked, it falls back to asking for a
channel and an arbitrary target nick, issued by the highlighted bot. A `MODE`
only takes effect if the issuing bot holds operator status on the channel.

**Bans…** opens a ban manager for a single bot and channel: it lists the
channel's `+b` entries (fetched from the server's ban-list reply), and lets you
**add** a ban mask (`+b`) or **remove** the selected one (`-b`). This needs a
server that supports channel bans (real IRC servers do; the bundled local
IRCServer's bans are admin-only).

### Offline roster

The bot roster is owned by the front end and persisted to
`%APPDATA%\IRCBot\bots.json`, so you can define and edit bots any time — no
server or host required. Each bot has a stable id the host honours, so the
roster syncs cleanly when you connect. Editing a **running** bot is rejected by
the host; stop it first.

The grid shows each bot's nick, server host, port, status (colour-coded:
green = connected, orange = connecting, red = error, grey = stopped/offline),
channels, and last event. It auto-refreshes every 2 seconds while connected.

Below the grid are two consoles: **Activity log** (panel/control activity and
command results) and **Bot connection activity** — a live, per-bot IRC-level
narrative streamed from the host (connecting, socket connected, TLS established,
registering, connected, joining, PING/PONG, errors, disconnects), so you can
see exactly what each bot is trying to do.

The bot list and the two consoles are separated by **draggable splitters**, so
you can resize each pane up or down; each pane scrolls independently when its
content runs off-screen.

## Run the bot host on its own

```
dotnet run --project Host -- [controlPort] [controlPassword]
```

- `controlPort` — loopback-only control port (default `6690`)
- `controlPassword` — optional; if set, the front end must authenticate

## Control protocol

Line-delimited JSON over a TLS-encrypted connection to the control port
(the host presents a self-signed cert; the panel accepts it for loopback use).
An optional password can be required via the `AUTH` command on top of TLS.
Request:

```json
{"cmd":"ADD","args":{"nick":"MyBot","host":"localhost","port":"6667","channels":"#test"}}
```

Response:

```json
{"ok":true,"message":"Added bot MyBot","bots":[ ... ]}
```

Commands: `AUTH` `LIST` `ADD` `EDIT` `REMOVE` `START` `STOP` `JOIN` `PART` `SAY`.
`ADD`/`EDIT` accept the full per-bot connection: `nick`, `host`, `port`, `tls`
(`true`/`false`), `password`, `ident`, `realname`, `channels`. See
`Shared/ControlProtocol.cs`.
