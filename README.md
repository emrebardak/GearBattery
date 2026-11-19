# GearBattery

Tiny Windows tray app that shows the battery level of wireless gear, without the vendor apps running.

One tray icon per device: a small device symbol on top and the battery percentage below.

| Color | Meaning |
|---|---|
| White | On battery |
| Red | 20% or less |
| Green | Charging |
| Gray | Device asleep or off. Shows the last known value. |

Hover an icon for details. Right-click for **Refresh** and **Exit**.

## Supported devices

| Device | Connection | Level | Charging |
|---|---|---|---|
| ATK X1 Pro mouse | 8K dongle (`373B:101B`) or USB cable (`373B:1016`) | yes | yes |
| ATK A87 Pro keyboard | 2.4 GHz dongle (`373B:111F`) or USB cable (`373B:111E`) | yes (not reported while charging) | yes |
| Logitech G435 headset | Lightspeed dongle (`046D:0ADF`) | yes (from voltage) | no |

Other ATK mice and keyboards on the same platforms probably work with only a PID change. See [docs/PROTOCOLS.md](docs/PROTOCOLS.md).

## Install

Requires Windows 10/11. The .NET Framework 4.x that ships with Windows is enough; no SDK is needed.

```powershell
git clone <this repo>
cd GearBattery
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

This builds the app, copies it to `%LOCALAPPDATA%\GearBattery`, adds it to Windows startup and starts it.

To remove it:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
```

To only build it, run `.\build.ps1`. The output goes to `bin\GearBattery.exe`.

## Command line

```powershell
.\bin\GearBattery.exe --print | Out-String
.\bin\GearBattery.exe --json  | Out-String
```

`| Out-String` is needed because the exe is a GUI app; piping makes PowerShell wait for and capture its output.

```json
[
  {"id": "mouse", "name": "ATK X1 Pro", "online": true, "level": 95, "charging": false},
  {"id": "keyboard", "name": "ATK A87 Pro", "online": true, "level": null, "charging": true},
  {"id": "headset", "name": "Logitech G435", "online": true, "level": 38, "charging": false}
]
```

For scripts, dashboards and AI assistants, see [docs/AI-INTEGRATION.md](docs/AI-INTEGRATION.md).

## Resource use

Measured on the author's PC: about 0.003% CPU and 24 MB of memory.

- The mouse and keyboard are polled once a minute: one short HID request each.
- The headset is polled every 5 minutes, because a read takes about 1.2 s and talks to the headset radio.
- No handles are kept open between polls.

## How it works

The vendor apps (ATK HUB, Logitech's tools) talk to the devices over vendor-specific HID reports. GearBattery sends the same battery requests directly through the Windows HID API (`hid.dll`, `setupapi.dll`). It only sends read and query commands, plus one radio-mode command on the G435 that the battery read needs. It never writes settings or firmware.

Protocol notes: [docs/PROTOCOLS.md](docs/PROTOCOLS.md).

## Project layout

```
src/GearBattery.cs     the whole app (single file, C# 5)
build.ps1              compile with the in-box csc.exe
install.ps1            build + install + startup shortcut (-Uninstall to remove)
docs/PROTOCOLS.md      byte-level protocol of every supported device
docs/AI-INTEGRATION.md reading devices from scripts/AI, and how an AI agent should add a device
```

## Contributing

New devices are welcome. Read the "Adding a device" section in [docs/AI-INTEGRATION.md](docs/AI-INTEGRATION.md). It is written for both people and AI coding agents. Keep the app in one file, with no dependencies and no NuGet packages.

## Credits

- The G435 battery protocol is ported from [deddroid/logitech-g435-battery](https://github.com/deddroid/logitech-g435-battery) (MIT), which builds on portyporty's g435-battery-percentage. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- The ATK protocols were read from the ATK HUB desktop app.

Not affiliated with ATK or Logitech. All trademarks belong to their owners.

## License

MIT. See [LICENSE](LICENSE).
