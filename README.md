# IRCBot

Simple IRC bots with a remote-control WinForms front end. A **bot host** runs
any number of bots (each a client connection to an IRC server) and exposes a
loopback-only JSON control port. The **control front end** connects to that
port to see and drive the bots: add/remove, start/stop, join/part, send
messages, and watch live status.

Designed to work with the local [IRCServer](https://github.com/joc00p/IRCServer),
but any IRC server works.

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

## Run (control front end)

From the repo root, with no arguments:

```
dotnet run
```

This opens the control panel. From there:

1. Click **Add Bot…**, give it a nick, the IRC server host/port
   (e.g. `localhost` / `6667`), and initial channels. **This works with no
   host connection** — bots are stored in a local roster.
2. **Edit…** (or double-click a row) to change a bot's nick/host/port/channels,
   also offline.
3. Set the **Control Port** (default `6690`) and click **Launch Bot Host** — it
   starts the host process and auto-connects. (Or start `IRCBotHost` yourself
   and click **Connect**.) On connect, your roster is synced to the host and any
   bots already on the host are imported into your roster.
4. Select a bot and use **Start / Stop / Join / Part / Say / Remove** (these
   act on the running host, so they need a connection).

### Offline roster

The bot roster is owned by the front end and persisted to
`%APPDATA%\IRCBot\bots.json`, so you can define and edit bots any time — no
server or host required. Each bot has a stable id the host honours, so the
roster syncs cleanly when you connect. Editing a **running** bot is rejected by
the host; stop it first.

The grid shows each bot's nick, server host, port, status (colour-coded:
green = connected, orange = connecting, red = error, grey = stopped/offline),
channels, and last event. It auto-refreshes every 2 seconds while connected.

## Run the bot host on its own

```
dotnet run --project Host -- [controlPort] [controlPassword]
```

- `controlPort` — loopback-only control port (default `6690`)
- `controlPassword` — optional; if set, the front end must authenticate

## Control protocol

Line-delimited JSON on the control port. Request:

```json
{"cmd":"ADD","args":{"nick":"MyBot","host":"localhost","port":"6667","channels":"#test"}}
```

Response:

```json
{"ok":true,"message":"Added bot MyBot","bots":[ ... ]}
```

Commands: `AUTH` `LIST` `ADD` `REMOVE` `START` `STOP` `JOIN` `PART` `SAY`.
See `Shared/ControlProtocol.cs`.
