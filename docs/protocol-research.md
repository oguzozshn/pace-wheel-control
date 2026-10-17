# Vendor protocol research (Phase 4)

Goal: find out how the "Racing Wheel" mobile app configures the Rampage Pace V987, so this
tool can change persistent settings (centering strength, rotation range, gain, …) that
DirectInput can't reach (see [device-notes.md](device-notes.md), Phase 2).

## Rules

- **Read-only by default.** Reading descriptors, input reports and captures is fine.
- **Nothing gets sent to the wheel's vendor channel without the user confirming the exact
  bytes first.** This covers output and feature reports on MI_01, report IDs that aren't in
  the descriptor, and any command found in the app. Unknown commands could misconfigure or
  brick the firmware.
- Decompiled vendor code stays out of this repository. Only protocol notes (report IDs, byte
  layouts, value ranges, observed effects) are written here.
- Analysis is for interoperability with hardware the user owns.

## What we know going in

- The HID descriptors declare **no vendor channel**. MI_00 is a standard joystick + HID PID
  force feedback interface; MI_01 is keyboard + mouse emulation with no feature reports.
- Windows' HID stack only passes report IDs that are declared in the descriptor. So if the
  app configures the wheel over USB, it either uses declared PID reports in a non-standard
  way, or something Windows would block. The app could also use a different transport
  (Bluetooth / BLE).

## Route 1: static analysis of the Android app (primary)

The app holds the protocol: which transport it uses, which bytes it sends for each setting,
and whether the firmware stores them.

Tools:
- [REA](https://github.com/morluto/rea) as an MCP server for the coding agent. It analyzes
  APKs through JADX.
- Temurin JDK 21, JADX, Android Platform-Tools (adb), installed with winget.

Getting the APK from the user's own phone:

1. Phone: enable Developer options and USB debugging, connect over USB, allow the PC.
2. Find the package and its APK paths (split APKs have several):

   ```bash
   adb shell pm list packages | findstr /i wheel
   ```

   ```bash
   adb shell pm path <package.name>
   ```

3. Pull each path into a fresh, empty folder outside the repository:

   ```bash
   adb pull <path-from-pm-path> <empty-folder>
   ```

What to look for in the decompiled app:
- The transport: Android USB host APIs (`UsbManager`, `UsbDeviceConnection.controlTransfer`,
  `bulkTransfer`) vs Bluetooth (`BluetoothGatt`, service/characteristic UUIDs).
- The command builder: byte arrays sent per setting (report ID / command byte, payload layout,
  checksum).
- Read-back commands, which are the safest first thing to try on real hardware.
- Any "save to flash" or "apply" command, and any firmware update path. Never call those
  without explicit approval.

## Route 2: USB capture with USBPcap + Wireshark

Use this to confirm what actually goes over the wire. It also works if the app talks USB
(phone → wheel over OTG can't be captured on the PC, but the PC side can be: PID traffic from
`pacewheel`, or a PC configuration tool if one turns up).

Setup:
1. Install Wireshark and select the USBPcap component during setup. Reboot if asked.
2. Find the wheel's root hub: Wireshark lists `USBPcap1`, `USBPcap2`, …. Start a short
   capture on each while moving the wheel, and keep the one that shows traffic.

Capture workflow:
1. Start the capture on the right USBPcap interface.
2. Do **one** action, for example `pacewheel gain 30` or `pacewheel test constant --strength 10`.
3. Stop the capture and save it as `.pcapng`, named after the action, outside the repository.
4. Filter on the wheel's device address (`usb.device_address == N`), plus:
   - `usb.transfer_type == 0x01` (interrupt) for input/output reports;
   - `usb.transfer_type == 0x02` (control) for SET_REPORT / GET_REPORT (feature reports, and
     output reports sent over the control pipe).
5. Write down each report: direction, report ID, length, payload bytes, and what the action
   was. Put the summary in this file; the captures stay local.

Questions a capture can answer right away:
- Does `pacewheel gain 30` produce a PID Device Gain report (output ID 0x0D)? If it does, the
  firmware ignores it. If it doesn't, pid.dll never sends it.
- What does pid.dll send on acquire and on release (Device Control reset, actuator on/off)?
  That may explain why the wheel's own centering pauses after a test.
- The true report descriptor bytes (GET_DESCRIPTOR during enumeration: replug the wheel while
  capturing), instead of HidSharp's rebuilt version.

## Findings

_None yet._
