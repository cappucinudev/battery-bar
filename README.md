# Battery Bar

Shows the battery level of your wireless headphones, mouse and keyboard right on the Windows taskbar, next to the tray icons.

![Battery Bar on the taskbar](docs/taskbar.png)

**[Download BatteryBar.exe](https://github.com/cappucinudev/battery-bar/releases/latest/download/BatteryBar.exe)** · [Install page](https://cappucinudev.github.io/battery-bar/)

- Tiny: one 60 KB exe, no admin rights, no internet, no background services.
- Finds devices on its own. Nothing to set up.
- Hides with the taskbar (full-screen games stay clean). Follows light/dark mode and display scaling.
- Settings: hide a device, change its icon, check interval, text size, position, start with Windows.

## Install

1. Download `BatteryBar.exe` from the [latest release](https://github.com/cappucinudev/battery-bar/releases/latest).
2. Run it. If Windows SmartScreen says "Windows protected your PC", click **More info → Run anyway** (the exe is not code-signed).
3. Click **Yes** to install. It copies itself to `%LOCALAPPDATA%\Programs\BatteryBar`, starts with Windows and adds a Start menu entry.
   Click **No** to just run it once without installing.

Click the numbers on the taskbar (or open Battery Bar from the Start menu) for settings. Right-click them for Refresh / Exit.

## Supported devices

| Brand / type | How it reads the battery |
| --- | --- |
| Logitech (G HUB running) | G HUB's local connection: every G HUB device with a battery (mice, keyboards, headsets) |
| Logitech (no G HUB) | Directly over HID++ 2.0: Lightspeed and Unifying receivers, Bolt, USB and Bluetooth mice and keyboards |
| Razer mice and keyboards | Directly over Razer's USB protocol (Synapse not needed) |
| Razer HyperSpeed headsets | BlackShark V2 Pro and others using the same dongle protocol |
| Any Bluetooth device | Whatever Windows shows in Settings → Bluetooth & devices (headphones, earbuds, mice, keyboards, controllers) |

Not shown? Open settings → **Copy diagnostics** and paste it into an [issue](https://github.com/cappucinudev/battery-bar/issues).
SteelSeries, Corsair and HyperX dongles are not supported yet.

Devices that nap (most mice sleep after a few idle minutes) keep their last value for 30 minutes.

## Uninstall

Settings → **Uninstall**. It removes the startup entry, the Start menu entry, its settings and the installed exe.

## Build it yourself

Needs only Windows (uses the C# compiler that ships with .NET Framework 4.x):

```powershell
.\build.ps1        # -> dist\BatteryBar.exe
```

Useful switches: `BatteryBar.exe --portable` (run without the install prompt), `--demo` (fake devices), `--dump out.txt` (write what each source sees and quit).

## How it works

- `src/Sources.cs` – battery sources (G HUB websocket, Logitech HID++, Razer HID, Windows device properties).
- `src/Bar.cs` – a transparent child window inside `Shell_TrayWnd`, placed left of the tray, drawn with per-pixel alpha.
- `src/App.cs` – startup, install/uninstall, polling, settings file (`%APPDATA%\BatteryBar\settings.txt`).
- `src/SettingsForm.cs` – the settings window.

## License

MIT
