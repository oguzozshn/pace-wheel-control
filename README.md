# pace-wheel-control

A Windows tool for configuring and inspecting the **Rampage Pace V987** racing wheel. The wheel
has no official PC software; its settings normally go through a mobile app only.

Done so far:
- **Phase 1, discovery and diagnostics:** enumerate the wheel's HID interfaces, decode their
  report descriptors, show the DirectInput view and monitor input live.
- **Phase 2, force feedback via DirectInput:** gain, autocenter and low-strength test effects.
  Finding: on this wheel DIPROP_FFGAIN and DIPROP_AUTOCENTER have no measurable effect, so
  nothing persists. Persistent settings need the vendor protocol.

Findings are in [docs/device-notes.md](docs/device-notes.md).

Planned next:
- Phase 3 (optional): input processing via vJoy
- Phase 4: research into the vendor configuration protocol

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

Phase 4 (vendor protocol research) is read-only by default. Unknown output or feature reports
could misconfigure or brick the firmware, so the tool won't send any without the exact bytes
being confirmed first.

This is an unofficial project, not affiliated with Rampage or Lite Star. Use it at your own risk.

## Project layout

```
src/PaceWheel.Core   library: HID enumeration + descriptor parser, DirectInput access
src/PaceWheel.Cli    console tool (pacewheel)
docs/                device findings and protocol research notes
```

## License

MIT, see [LICENSE](LICENSE).
