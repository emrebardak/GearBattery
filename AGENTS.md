# Notes for AI coding agents

- The whole app is `src/GearBattery.cs`, a single file. Keep it that way: no projects, no NuGet packages.
- Build it with `.\build.ps1`, which uses the in-box `csc.exe` (C# 5). Avoid C# 6+ syntax: `$""`, `?.`,
  `nameof`, expression-bodied members, `out var`.
- Test with `.\bin\GearBattery.exe --print | Out-String`, with the real devices awake.
- Device protocols: `docs/PROTOCOLS.md`. To add a device, follow "Adding a device" in
  `docs/AI-INTEGRATION.md`.
- Safety: only send commands you have identified as read-only or status queries. Never send firmware,
  OTA, erase or reset opcodes. Never guess opcodes on real hardware.
