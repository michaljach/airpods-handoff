# AirPods Handoff for Windows

Tray app that gives a Windows PC handoff-style switching for AirPods (built and tuned for AirPods Pro 2 USB-C).

- Grab: when the PC has played audio for about 1 s and a bud is in your ear, it connects the AirPods. This takes them from your iPhone/Mac.
- Release: after 2 min without PC audio, it disconnects so your Apple devices can take them back.
- Don't fight: if another device takes the AirPods while the PC is still playing, auto-grab pauses until the PC goes quiet.
- Ear detection: removing a bud pauses media; putting it back resumes it.
- Tray icon: shows the lowest bud battery, green when connected to the PC. Double-click toggles: connect, or give the AirPods back (auto-grab waits until PC audio stops).

Calls (mic in use) count as activity, so the AirPods are never released mid-meeting.

## Build & run
Needs the .NET 8 SDK. `dotnet run`, or `dotnet publish -c Release -r win-x64 --self-contained false -o publish`.
Pair the AirPods with Windows first.

## First-run checklist
Config: %APPDATA%\AirPodsHandoff\config.json. Log: log.txt next to it. Restart the app after editing the config.
1. DeviceNameContains must match the Sound device name. Test with tray → Disconnect now / Connect now; the log shows `bt: connect '…' -> 0x00000000` on success.
2. Set LogAdvertisements=true and check the `ble:` lines: model should be 0x2024 (if not, set ModelIds); in-ear and battery values should follow the buds; tune MinRssi (default -70). Turn logging off afterwards.
3. If BLE never works, set RequireInEar=false.

## Limitations
- Phone → PC is automatic. PC → phone depends on iOS reconnecting, usually when you play on the phone or pick the AirPods in Control Center.
- The first second or so of PC audio plays from the speakers until the AirPods connect.
- Noise-control modes need Apple's private L2CAP channel (AAP), which Windows doesn't expose to apps.
