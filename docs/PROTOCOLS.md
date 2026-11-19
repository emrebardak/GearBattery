# Device protocols

All devices are read through the Windows HID API. A device is found by enumerating HID interfaces
(`SetupDiGetClassDevs` with the HID GUID). An interface matches when its path contains `vid_XXXX&pid_YYYY`
and its top-level collection (`HidP_GetCaps`) has the expected **usage page**. Reports are written and read
with overlapped `CreateFile` + `FileStream`. Report lengths below **include** the report ID byte, as Windows
expects.

A device that is asleep or off usually does not answer. GearBattery treats a timeout as "offline" and keeps
showing the last value in gray.

---

## ATK X1 Pro mouse

| | |
|---|---|
| Interfaces | `373B:101B` 8K dongle; `373B:1016` mouse on its USB cable |
| Collection | usage page `0xFF02` (`MI_01&COL05`), in/out length 17 |
| Source | ATK HUB renderer bundle (`resources/main/app.asar`): controller `YUt`, packet `qkt`, command enum `lEt` |

Request: output report ID `0x08`, 16 data bytes.

```
[0]  0x08          report ID
[1]  0x04          command: GetBatteryLevel
[2..15] 0x00
[16] checksum = 0x55 - ((0x08 + sum(bytes 1..15)) & 0xFF)   -> 0x49 for this request
```

Reply: input report `0x08`.

```
[0] 0x08  [1] 0x04 (command echo)  [2] status (0 = OK, otherwise mouse not reachable)
[6] battery %  [7] charging (0/1)  [8..9] voltage mV (big-endian)
```

Example: `08 04 00 00 00 02 0A 00 0D A3` means 10%, not charging, 3491 mV.

Both the dongle and the cable use the same protocol. GearBattery tries the dongle first. If the mouse charges
on the cable while the dongle is also plugged in, the dongle answers with status != 0, and the cable
interface is used instead.

---

## ATK A87 Pro keyboard

| | |
|---|---|
| Interfaces | `373B:111F` 2.4 GHz dongle; `373B:111E` keyboard on its USB cable |
| Collection | usage page `0xFF60` (VIA-style raw HID, `MI_03` on the dongle, `MI_02` on the cable), in/out length 33 |
| Source | ATK HUB: controller `_Gt` / base `FGt`, packets `vGt` (32 bytes, baseOffset 1) |

Report ID `0x00` + 32 data bytes.

**Dongle link status** (dongle only):

```
send  00 AA FD 00...
reply 00 55 FD <status>      status 1 = keyboard connected, 2 = disconnected / asleep
```

**Battery** (packet `PGt`):

```
send  00 02 96 24 00...
reply 00 02 96 24 <level> <charge>
```

`charge` 0 means on battery, and `level` is valid. When `charge` != 0 the keyboard is charging and `level` is
**not valid** (it reads 0). ATK HUB also hides the level while charging. GearBattery then shows the last known
level in green, or `+` if it has none.

---

## Logitech G435 headset (Lightspeed dongle `046D:0ADF`)

| | |
|---|---|
| Collection | usage page `0xFF00` (`MI_00`), 64-byte reports (65 with the report ID byte) |
| Source | ported from [deddroid/logitech-g435-battery](https://github.com/deddroid/logitech-g435-battery) (MIT), `adf` path |

This dongle does **not** speak HID++ (`0x10`/`0x11`/`0x12` requests get no answer). It uses 64-byte frames:

```
[0] cmd   [1] body length   [2..3] CRC-16/XMODEM (little-endian)   [4..] body   zero padding to 64
```

The CRC is CRC-16/XMODEM (poly `0x1021`, init 0, not reflected, no final XOR). It is computed over all 64
bytes with `[2..3]` set to zero. Check value: `CRC("123456789") = 0x31C3`. Each frame is written as 65 bytes,
with a leading `0x00` report ID.

"PA" commands are carried in frame `cmd = 0x80` with body `50 41 <category> <seq> <len> <payload>`.

Session, in order (waits are how long to collect replies before the next write):

| # | Frame | Meaning | Wait |
|---|---|---|---|
| 1 | `84` body `01` | open | 50 ms |
| 2 | `85` body `01` | open | 50 ms |
| 3 | `80` body `50 41 06 01 0D 00 25 34 12 5A 5A 01 00 00 00 F0 00 00 00` | session init (OTA ioctl read max len) | 450 ms |
| 4 | PA cat `0E` seq 2 len 1 `E0` | get remote mode | 120 ms |
| 5 | PA cat `0E` seq 3 len 2 `E1 01` | set remote mode 1 (needed so the query reaches the headset) | 120 ms |
| 6 | PA cat `0E` seq 4 len 1 `E0` | get remote mode | 120 ms |
| 7 | PA cat `06` seq 5 len 1 `00 31` | **get battery** | 250 ms |
| 8 | `85` body `02` | close | 50 ms |

Do not cancel a read between steps. A reply can arrive after a step's wait, so keep the same pending read
going into the next step.

Battery reply: search the collected bytes for `07 00 06 <seq|0x80> 00 <mV lo> <mV hi>`, and accept
2500–4600 mV. Observed reply: `00 11 50 49 01 CC E9 12 04 00 07 00 06 85 00 85 0E ...` gives `0x0E85` = 3717 mV.
The session-init reply has the same pattern with 240 mV, which the range check rejects.

Voltage to %, with linear interpolation between points:

```
4200:100 4150:95 4100:90 4050:85 4000:80 3950:75 3900:70 3850:62
3800:55 3750:45 3700:35 3650:25 3600:15 3550:10 3500:5 3400:0
```

Charging is not reported. If the headset is off, no battery pattern is found, and the headset shows as offline.

The other G435 dongle (`046D:0ACB`) uses a different bridge, through report 6 with feature reads. See the
upstream repo. GearBattery does not implement it yet.
