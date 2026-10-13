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
- AgentMeter reuses one background Codex app-server process, restarts it after connection failures, and closes it on exit.
- Quotas refresh every 60 seconds. A transient failed read is retried once; rate-limit errors are not retried. If reading still fails, the last successful value remains visible and is clearly marked as stale.
- Server error codes and messages are shown in the tooltip to help distinguish rate limiting, authentication, and protocol failures.
- The meter appears on the empty left side of every enabled, bottom-aligned Windows 11 taskbar.
- Enable or disable startup after Windows sign-in from the settings window.
- Opening settings closes any open quota details panel. The settings window opens larger, follows per-monitor DPI, and can be resized when moving between 1080p and 4K displays.
- AgentMeter automatically checks the Codex desktop app, native `codex.exe` entries on `PATH`, and standard or custom npm global directories. If Codex is installed elsewhere, set `AGENT_METER_CODEX_PATH` to its `codex.exe` path.

Run the renderer self-check without opening the tray UI:

```powershell
dotnet run -- --self-test
```

Run the local fake-server lifecycle check (no account requests):

    pwsh -NoProfile -File tools/test-quota-reader.ps1

## Portable release

Download `AgentMeter-0.1.3-win-x64.zip` from GitHub Releases and extract it to a permanent folder. Run `AgentMeter.exe`. The Windows x64 package includes the .NET runtime; Codex must still be installed and signed in. The package is currently unsigned.

ABoring Launcher can install the ZIP directly from this repository's Release. Select `AgentMeter.exe` as the entry point. Exit AgentMeter from its tray menu before updating. If Windows startup is enabled, disable and re-enable it after moving or updating the executable so that the startup path follows the new location.

Build and run the release self-check with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1
```

The ZIP and SHA-256 checksum are written to `artifacts/`. Move the existing versioned output directory before rebuilding the same version. The source checkout now lives under `ABoringStudio/agent-meter`.

### Memory and packaging

Version 0.1.1 disables compression inside the single-file executable. The download is still ZIP-compressed and self-contained. Internal compression saved executable disk space but raised runtime private memory in the measured Windows workload; do not re-enable it without a memory comparison.

On 2026-10-13, fresh processes built from the same application code were sampled over one minute: compressed single-file private bytes were 53.8-58.7 MiB after startup, while uncompressed single-file private bytes were 22.1-31.1 MiB at the same sampling points. The previously running installed process used 95.8-98.6 MiB, but had a different lifetime and interaction history. These are private committed bytes, not Task Manager's private working set. Actual usage varies with UI interactions, runtime caches and garbage collection.

A separate 40-second .NET counter sample of the installed process showed the managed heap falling from about 10.4 MB to 0.49 MB after garbage collection. This short check did not show retained managed-heap growth; it does not prove the absence of a long-term leak. No forced GC or working-set trimming is used.

Version 0.1.2 allows only one AgentMeter instance per Windows session, including launches from different folders. A repeated launch exits immediately and leaves the existing taskbar meter running. Exit older versions once before using this protection. The --self-test path remains independent.
