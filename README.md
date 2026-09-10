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
- Right-click to refresh, open details, change settings, or exit.
- Left-click or right-click the notification-area icon to open the same complete menu.
- Quotas refresh every 60 seconds. A transient failed read is retried once; rate-limit errors are not retried. If reading still fails, the last successful value remains visible and is clearly marked as stale.
- Server error codes and messages are shown in the tooltip to help distinguish rate limiting, authentication, and protocol failures.
- The meter appears on the empty left side of every enabled, bottom-aligned Windows 11 taskbar.
- Enable or disable startup after Windows sign-in from the settings window.
- Opening settings closes any open quota details panel. The settings window opens larger, follows per-monitor DPI, and can be resized when moving between 1080p and 4K displays.
- If Codex is installed outside the standard npm location, set `AGENT_METER_CODEX_PATH` to its `codex.exe` path.

Run the renderer self-check without opening the tray UI:

```powershell
dotnet run -- --self-test
```
