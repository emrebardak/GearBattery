# AI integration

This guide has two parts:

1. **Reading devices.** How scripts, dashboards and AI assistants get battery data from GearBattery.
2. **Adding a device.** A step-by-step method for a person or an AI coding agent to add a new device.

---

## 1. Reading devices

### The `--json` contract

```powershell
& "$env:LOCALAPPDATA\GearBattery\GearBattery.exe" --json | Out-String
```

Output is a JSON array with one object per known device, always in the same order:

| Field | Type | Meaning |
|---|---|---|
| `id` | string | Stable key: `mouse`, `keyboard`, `headset` |
| `name` | string | Display name, e.g. `ATK X1 Pro` |
| `online` | bool | `false` when the device is off, asleep, unplugged, or did not answer in time |
| `level` | int or null | Battery 0–100. `null` when offline, or when the device can't report it (ATK keyboard while charging) |
| `charging` | bool | `true` while charging. Always `false` for the G435, which can't report it |

Rules for consumers:

- **Treat `online: false` as "unknown", not "empty".** ATK devices go to sleep after a few minutes idle and
  stop answering until they are touched.
- A run takes about 2–4 s. That is roughly 1.5 s per sleeping ATK device, which ends in a timeout, plus
  about 1.2 s for the headset session.
- **Don't poll faster than once a minute.** Each headset read briefly switches the dongle into a query mode,
  so poll the headset every 5 minutes or less often.
- The tray app and a `--json` run can collide on the headset if they read it at the same moment. The worst
  case is one `offline` result, so retry once.
- `| Out-String` (PowerShell) or capturing stdout (any language) is required, because the exe is a
  GUI-subsystem app.

### Examples

**PowerShell**

```powershell
$gear = & "$env:LOCALAPPDATA\GearBattery\GearBattery.exe" --json | Out-String | ConvertFrom-Json
$gear | Where-Object { $_.online -and $_.level -ne $null -and $_.level -le 20 } |
    ForEach-Object { "$($_.name) is at $($_.level)%" }
```

**Python**

```python
import json, os, subprocess
exe = os.path.expandvars(r"%LOCALAPPDATA%\GearBattery\GearBattery.exe")
devices = json.loads(subprocess.run([exe, "--json"], capture_output=True, text=True, timeout=15).stdout)
low = [d for d in devices if d["online"] and d["level"] is not None and d["level"] <= 20]
```

**Node.js**

```js
const { execFileSync } = require("child_process");
const exe = `${process.env.LOCALAPPDATA}\\GearBattery\\GearBattery.exe`;
const devices = JSON.parse(execFileSync(exe, ["--json"], { encoding: "utf8", timeout: 15000 }));
```

### As a tool for an AI assistant (MCP)

Any MCP-capable assistant (Claude Desktop, Claude Code, others) can call GearBattery through a tiny MCP
server. A minimal Python version, using the official `mcp` package (`pip install mcp`), is below.

```python
# gearbattery_mcp.py
import json, os, subprocess
from mcp.server.fastmcp import FastMCP

EXE = os.path.expandvars(r"%LOCALAPPDATA%\GearBattery\GearBattery.exe")
mcp = FastMCP("gearbattery")

@mcp.tool()
def get_gear_battery() -> list[dict]:
    """Battery of the user's wireless mouse, keyboard and headset.
    online=false means asleep/off (unknown), not empty. level may be null while charging."""
    out = subprocess.run([EXE, "--json"], capture_output=True, text=True, timeout=15).stdout
    return json.loads(out)

if __name__ == "__main__":
    mcp.run()
```

Register it with the assistant. For example, in Claude Code:

```powershell
claude mcp add gearbattery -- python C:\path\to\gearbattery_mcp.py
```

The assistant can then answer "how much battery does my headset have?" or "warn me if anything is below 20%".

An assistant with only a shell tool doesn't need MCP: tell it to run the PowerShell one-liner above.

---

## 2. Adding a device

This method is how the current devices were added. It works for most wireless peripherals whose vendor
app shows a battery level. **AI agents: follow these steps in order, verify each one on real hardware, and
never send commands you have not identified as read-only.**

### Step 1: Find the device and its vendor collection

```powershell
Get-PnpDevice -PresentOnly | ? InstanceId -match '^HID\\VID_' |
    Select FriendlyName, InstanceId | Sort InstanceId
```

- Note the `VID_xxxx&PID_yyyy`. Dongle and cable usually have **different PIDs** (ATK: `101B` vs `1016`),
  so check both connection modes.
- Battery requests go to a **vendor-defined** collection (usage page `0xFF00`–`0xFFFF`), not to the
  keyboard or mouse collections. Read each collection's usage page and in/out report lengths with
  `HidP_GetCaps`. `Hid.Find` in the source does this.

### Step 2: Get the protocol, preferring sources that already exist

In this order:

1. **Existing open-source projects**: HeadsetControl, Solaar, OpenRGB, libratbag, and per-device repos.
   Search for "<model> battery" plus "hid" on GitHub. Check that the PID matches; one model name can
   cover several dongles with different protocols (the G435 has `0ACB` and `0ADF`).
2. **The vendor app.** Many are Electron apps. Extract `resources\*.asar` (`npx @electron/asar extract`)
   and search the JS for the battery feature: look for `battery`, `power`, `getPower`, `charging`, and for
   packet classes and command enums. ATK HUB's real UI was in `resources\main\app.asar`, not in the
   top-level `app.asar`.
3. **USB capture** (last resort): run Wireshark with USBPcap while the vendor app reads the battery.

### Step 3: Probe safely

- Send **only** the battery or status query you identified. Never guess opcodes on a live device, and
  never send anything labeled flash, OTA update, erase, NVDS, reset or factory.
- Write a throwaway probe. It can be compiled together with `src/GearBattery.cs` so it reuses `Hid`:
  `csc /main:Probe /out:probe.exe src\GearBattery.cs probe.cs`
- Wake the device by moving or pressing it right before probing. A missing reply usually means the device
  is asleep, not that the protocol is wrong.
- Check the output report length from `HidP_GetCaps`. A write with the wrong length fails silently in
  `Hid.Query`, which returns null on any error.
- If a device needs several request/reply steps, keep **one** pending read across steps (see the `G435`
  class). Starting a new read while another is pending loses replies.
- Compare the result with the vendor app before trusting it.

### Step 4: Implement

- Add a `static Reading YourDevice()` method that returns `Reading.On(level, charging)` or `Reading.Off()`.
  Use `Reading.On(-1, true)` when the device reports "charging" but no level.
- Try every connection mode (dongle, then cable), the way `Gear.Mouse()` and `Gear.Keyboard()` do.
- Add a `Tray` in `Main` with a Segoe MDL2 glyph (`\uE962` mouse, `\uE765` keyboard, `\uE7F6` headphone,
  `\uE7FC` game controller), and add the device to the `--print`/`--json` list.
- Keep C# 5 syntax (no `$""`, no `?.`, no `nameof`), because the in-box `csc.exe` is C# 5.
- If a read is slow or disruptive, poll it less often. The headset uses every 5th tick.

### Step 5: Document

Add a section to [PROTOCOLS.md](PROTOCOLS.md) with:

- VID/PID for each connection mode, and the usage page.
- Exact request bytes and the reply layout.
- One real captured reply as an example.
- Known limits, such as no charging flag or no level while charging.
- The source of the protocol, with credit and license. Also add it to `THIRD-PARTY-NOTICES.md` if the
  protocol was ported from someone else's code.

Then add a row to the device table in the README.
