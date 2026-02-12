# ai-watcher

A vibe coded desktop app that monitors running Claude Code sessions and shows their real-time status.

![screenshot](screenshot.png)

**NOTICE**: This has many bugs, but it works ok. Currently only has Windows support. I will add support for Linux and Mac soon because I develop on all three operating systems. I would also like to add codex support, but my free pro plan was discontinued and I've been unable to resubscribe with my company credit card for some reason.

## Features

- **Auto-detects Claude Code sessions** — both CLI (terminal) and VS Code extension instances
- **Real-time status** — Working, Waiting for Input, Waiting for Permission, with elapsed time
- **Click to activate** — click any session to bring its window to the foreground
- **Always on top** — stays visible while you work

## Building

Requires .NET 10 SDK with MAUI workload.

```
dotnet build -f net10.0-windows10.0.19041.0
```

## Running

```
dotnet run -f net10.0-windows10.0.19041.0
```

Or run the built executable from `bin/Debug/net10.0-windows10.0.19041.0/win-x64/AIWatcher.exe`.
