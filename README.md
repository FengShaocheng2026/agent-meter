# AgentMeter

AgentMeter is a lightweight Windows taskbar companion for viewing the current Codex quota.

## Status

A small companion window embedded into the Windows 11 taskbar displays the most constrained live Codex quota. Click it to see every quota window returned by the local Codex app server, its reset time, connection state, and last refresh.

AgentMeter reads `account/rateLimits/read` from the locally installed Codex CLI. It does not ask for, store, or print account tokens or cookies. The app-server protocol is experimental and may change with Codex CLI updates.

## Run

```powershell
dotnet run
```

- Left-click the taskbar meter to open or close quota details.
- Right-click to refresh, open details, or exit.
- Quotas refresh every 60 seconds. Read failures display `--`; simulated or cached values are never substituted.
- The current host targets the empty left side of the primary, bottom-aligned Windows 11 taskbar.
- If Codex is installed outside the standard npm location, set `AGENT_METER_CODEX_PATH` to its `codex.exe` path.

Run the renderer self-check without opening the tray UI:

```powershell
dotnet run -- --self-test
```
