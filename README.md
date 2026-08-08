# AgentMeter

AgentMeter is a lightweight Windows app for viewing AI agent quotas, usage, and cost in one place.

## Status

Visual feasibility probe. A small companion window embedded into the Windows 11 taskbar displays the Codex icon and remaining quota without requiring a hover; hovering shows the quota window and reset time.

The probe uses simulated values only. Codex data integration comes after visual acceptance.

## Run the visual probe

```powershell
dotnet run
```

- Left-click the taskbar meter to cycle through `100`, `84`, `7`, and `--`.
- Right-click to select a state, compare the `系统极简` and `状态强调` styles, or exit.
- The probe currently targets the empty left side of the primary, bottom-aligned Windows 11 taskbar.

Run the renderer self-check without opening the tray UI:

```powershell
dotnet run -- --self-test
```
