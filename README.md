# pace-wheel-control

A Windows tool for configuring and inspecting the **Rampage Pace V987** racing wheel. The wheel
has no official PC software; its settings normally go through a mobile app only.

The project is at **Phase 1: discovery and diagnostics**. The CLI can enumerate the wheel's HID
interfaces, decode its report descriptors, show the DirectInput view (axes, buttons, FFB effect
types) and monitor input live. Nothing is written to the device in this phase.
Findings are in [docs/device-notes.md](docs/device-notes.md).

Planned next:
- Phase 2: FFB gain, autocenter and test effects via DirectInput
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
| `monitor [--seconds n] [--threshold n] [--log file] [--changes]` | Live view of all axes, POV and buttons. Esc or Ctrl+C quits and prints a session summary |
| `notes [--path file]` | Regenerates the diagnostic block at the bottom of `docs/device-notes.md` |

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

Force feedback wheels can move suddenly and with real force. The FFB commands planned for
Phase 2 will:
- start test effects at no more than 20% magnitude and always with a finite duration;
- print a "hold the wheel" warning before playing anything;
- stop all effects and release the device on Esc, Ctrl+C, normal exit and exceptions.

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
