# pace-wheel-control

A Windows tool for configuring and inspecting the **Rampage Pace V987** racing wheel. The wheel
has no official PC software; its settings normally go through a mobile app only.

Done so far:
- **Phase 1, discovery and diagnostics:** enumerate the wheel's HID interfaces, decode their
  report descriptors, show the DirectInput view and monitor input live.
- **Phase 2, force feedback via DirectInput:** gain, autocenter and low-strength test effects.
  Finding: on this wheel DIPROP_FFGAIN and DIPROP_AUTOCENTER have no measurable effect, so
  nothing persists. Persistent settings need the vendor protocol.
- **Phase 4, vendor protocol:** the wheel is configured over **Bluetooth LE** (service 0xFFE0),
  found by analyzing the mobile app. Rotation angle, firmware FFB strength and the centering
  spring can now be read and changed from Windows, and the changes are stored in the wheel.

Findings are in [docs/device-notes.md](docs/device-notes.md) and
[docs/protocol-research.md](docs/protocol-research.md).

- **GUI** (`src/PaceWheel.App`, WPF): live input view (steering, pedals, D-pad, buttons, gear)
  and sliders for rotation angle, FFB strength and centering spring. Every change shows the
  exact bytes and asks for confirmation, saves a backup, reads the result back and can be undone.
  A **button test** page lays the keys out like the wheel; pressing a key lights it and marks it
  as working, so dead buttons stand out.

Planned next:
- Phase 3 (optional): input processing; pedal curves and button mapping may be better done
  through the wheel's own profile

## Supported hardware

| Wheel | USB ID | Notes |
| --- | --- | --- |
| Rampage Pace V987 | 11FF:2370 | Lite Star OEM ("LITE STAR" / "FFB WHEEL PACE V987"); HID PID force feedback |

Other Lite Star / PXN wheels (VID 0x11FF) will probably work with small changes to
`src/PaceWheel.Core/WheelIds.cs`, but they are untested.

## Building

Requirements: Windows 10/11 and the .NET 8 SDK or newer.

```bash
dotnet build
```

The CLI executable is called `pacewheel`. Run it through `dotnet run`:

```bash
dotnet run --project src/PaceWheel.Cli -- list
```

## Usage

| Command | What it does |
| --- | --- |
| `list` | Lists every HID node of the wheel with its usage page/usage, report lengths and whether user mode can open it |
| `descriptor [--hex-only]` | Dumps each node's report descriptor as hex plus a decoded item tree and a per-report size table |
| `di-info` | DirectInput view: capabilities, properties (FFB gain, autocenter), every object, every supported effect type |
| `monitor [--seconds n] [--threshold n] [--log file] [--changes] [--raw]` | Live view of all axes, POV and buttons; `--raw` also logs MI_00 raw input reports. Esc or Ctrl+C quits and prints a session summary |
| `notes [--path file]` | Regenerates the diagnostic block at the bottom of `docs/device-notes.md` |
| `ffb-status` | Shows FFB gain, autocenter and the device FFB state |
| `gain <0-100>` | Sets DIPROP_FFGAIN (no measurable effect on this wheel) |
| `autocenter on\|off [--hold s]` | Sets DIPROP_AUTOCENTER, optionally keeping the device held (no effect on this wheel) |
| `test <constant\|spring\|damper\|sine> [--strength 1-20] [--seconds 1-5] [--gain n] [--autocenter on\|off] [--left]` | Plays one test effect and reports the measured steering travel |

The FFB commands need the wheel's DC power adapter; on USB power alone the motor does nothing.
`scripts/ffb-feel-test.ps1` walks through the Phase 2 checks step by step (`-Language tr` for
Turkish prompts, `-Suite gain` for the measured gain check):

```bash
powershell -ExecutionPolicy Bypass -File scripts/ffb-feel-test.ps1 -Suite gain
```

Bluetooth LE commands (need a BLE adapter; keep the wheel's USB cable plugged in, since it
doesn't advertise without it, and close the mobile app, which otherwise holds the connection):

| Command | What it does |
| --- | --- |
| `ble-scan [--seconds n] [--active] [--all]` | Lists BLE devices that look like the wheel (passive by default) |
| `ble-gatt [--address a]` | Lists GATT services and characteristics, discovery only |
| `ble-info --address a` | Reads Device Information and characteristic descriptions |
| `ble-read-state --address a [--send]` | Reads mode, firmware version, pedal/shifter state |
| `ble-backup --address a [--send]` | Saves all six profile pages to `backups/` |
| `wheel-get --address a` | Shows angle, FFB strength, spring, damper, friction, inertia |
| `wheel-set --address a [--angle 90-1080] [--ffb 0-100] [--spring 0-100] [--send]` | Changes settings with backup, readback and an undo command |
| `ble-send --address a --request "hex" [--send]` | Sends raw read/edit requests; profile writes and reset are refused |

Every command that writes is a dry run unless `--send` is given.

```bash
dotnet run --project src/PaceWheel.Cli -- wheel-set --address DC:32:62:5A:92:91 --spring 60 --send
```

GUI:

```bash
dotnet run --project src/PaceWheel.App
```

Examples:

```bash
dotnet run --project src/PaceWheel.Cli -- descriptor
```

```bash
dotnet run --project src/PaceWheel.Cli -- monitor --log monitor.log
```

`monitor` uses a non-exclusive, background cooperative level, so it runs alongside games and
never sends anything to the wheel.

## Safety

Force feedback wheels can move suddenly and with real force. The FFB commands:
- cap test effects at 20% strength and 5 s, so every effect ends on its own;
- print a "hold the wheel" warning and a 3 s countdown before playing anything;
- stop all effects and release the device on Esc, Ctrl+C, normal exit and exceptions.

Force scaling on this wheel is very non-linear: 10% constant force for 2 s already swings the
wheel by about a third of its range. Start low and keep tests short.

The BLE commands only send messages confirmed on the real wheel, and print every byte first
(dry run unless `--send`). Profile writes (`52`) and the factory reset (`53 08`) are refused,
and the app's firmware update path is never used. Back up with `ble-backup` before changing
settings.

This is an unofficial project, not affiliated with Rampage or Lite Star. Use it at your own risk.

## Project layout

```
src/PaceWheel.Core   library: HID enumeration + descriptor parser, DirectInput access
src/PaceWheel.Cli    console tool (pacewheel)
src/PaceWheel.App    WPF GUI (PaceWheelApp)
docs/                device findings and protocol research notes
```

## License

MIT, see [LICENSE](LICENSE).
