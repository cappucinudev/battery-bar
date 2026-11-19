# Battery Bar

Windows taskbar app: battery % of wireless headphones, mice, keyboards. Public repo `cappucinudev/battery-bar`, download page `docs/index.html` (GitHub Pages), releases carry `dist/BatteryBar.exe`. Read `README.md` *How it works* first.

- C# 5 only (built by `build.ps1` with `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). No NuGet, no SDK, no `$""`, no `?.`, no `nameof`, no expression-bodied members.
- Files: `src/Sources.cs` (battery sources), `src/Native.cs` (Win32/HID/SetupAPI), `src/Bar.cs` (taskbar strip), `src/Flyout.cs` (click panel), `src/App.cs` (startup, install, settings file).
- Test without devices: `dist\BatteryBar.exe --portable --demo` (fake devices), `--demo-settings`, `--dump out.txt` (what each source sees). Stop the running exe before a build (`Stop-Process -Name BatteryBar`).
- Device protocols: only send read-only "get" requests from a known source (Synapse/G HUB behaviour seen on this PC, HeadsetControl, OpenRazer, Solaar). Never send set/write commands to a device.
- Keep it monochrome (black/white), like Windows' own tray.
- LF line endings. Commit to `main`; release = `gh release create vX.Y.Z dist/BatteryBar.exe` (bump `Program.Version` in `src/App.cs`).

## Save tokens
The user (2026-11-17): "use caveman and token optimizer from now on". Weekly usage is tight, so:
- **Caveman replies.** Short words, short sentences, no filler. Paths, numbers, errors stay exact.
- **Grep first,** then read only the lines needed (offset and limit). Never re-read what is in context.
- **Keep tool output short** (`| head`, `| tail`, `Select-String ... | select -First N`).
- **Delegate big jobs to the `bb-dev` agent** (`.claude/agents/bb-dev.md`) so the main session stays small.
- **Stop when the job is done.** No extra features unasked.
