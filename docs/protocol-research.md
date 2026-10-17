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

### The app and the transport (2026-10-17)

- App: "Racing Wheel", package `com.star.v10`, version 1.0.25 (versionCode 26), from Google
  Play. Pulled from the user's own tablet with adb (`base.apk` + `split_config.arm64_v8a.apk`)
  and analyzed statically with REA 6.5.0 + jadx-headless-mcp 0.7.1. Nothing was executed.
- The app bundles PXN's code (`com.pxn.v12…`), so Lite Star wheels and PXN wheels share
  this protocol family.
- **Transport: Bluetooth LE, not USB.** The manifest requires `android.hardware.bluetooth_le`
  and asks for Bluetooth permissions only (no USB host). The app uses Nordic's BLE scanner
  library. This explains why the USB HID descriptors have no configuration channel.
- The app recognizes devices by their BLE name. **`FFB WHEEL PACE V987`** is in its list,
  together with many sister models (V980/V981/V983/V985/V987, GT987, Vantage, Vortex, …). For
  these models the app sets "bigger angle" mode (1080°).

### GATT layout

- Service `0000ffe0-0000-1000-8000-00805f9b34fb`.
- Characteristics in the app: `0000ffe1-…`, `0000ffe2-…`, `0000ffe4-…` (same base). Which one
  is written and which one notifies couldn't be decompiled (the GATT callback class failed in
  JADX). A read-only GATT discovery on the real wheel will show it from the characteristic
  properties.
- Notifications are enabled via the standard CCCD `00002902-…`.
- The app writes one command at a time from a queue. It waits up to 500 ms for the matching
  notification, retries up to 3 times (2 s apart), then disconnects. After a response it
  waits 100 ms before the next command.

### Messages

Byte 0 is the message type. The response has the same type with bit 7 set (`| 0x80`).
Multi-byte values are little-endian.

| Request | Response | Meaning |
| --- | --- | --- |
| `50 01` | `D0 …` | read state |
| `51 <mode> <page>` | `D1 …` | read profile page 0-5 (the app reads all six) |
| `52 <mode> <page> <data…>` (20 bytes total) | `D2 …` | write profile page |
| `53 <cmd> …` | `D3 <cmd> …` | edit one parameter (see below) |
| `54 …` | `D4 03 …` | testing mode / live test data |

`53` edit commands, as built by the app:

| Bytes | Effect | Notes |
| --- | --- | --- |
| `53 01 <mode>` | switch mode | modes are platform modes (PC, consoles, mobile); values not mapped yet |
| `53 03 <mode> <ffb> <reverse>` | FFB strength + reverse FFB | `reverse` 0/1 |
| `53 04 01 01 <angle lo> <angle hi>` | rotation angle | 16-bit degrees |
| `53 08 <mode>` | **reset profile to defaults** | destructive, never send without approval |
| `53 09 01 <spring> <damper> <friction> <inertia>` | firmware spring/damper/friction/inertia strength | likely controls the strong idle centering found in Phase 2 |

State response `D0`: byte 2 mode, bytes 3-4 firmware version, byte 5 pedal state, byte 6
shifter state, byte 7 angle mode.

Profile pages (`D1` response / `52` write), 20 bytes each, from byte 3 on:
- page 0 and 1: button map (one byte per key, in the app's fixed key order);
- page 2: more button map, then `params[0]` (u16, probably the rotation angle) and
  `params[1..5]` (one byte each; likely FFB strength, spring, damper, friction, inertia,
  which still has to be confirmed on the device), then FFB reverse (0/1);
- pages 3, 4, 5: throttle, brake and clutch response curves as (u16, u16) point pairs.

### On the real wheel: scan and GATT discovery (2026-10-17, read-only)

- `pacewheel ble-scan --active`: the wheel advertises as **`FFB WHEEL PACE V987`**, address
  `DC:32:62:5A:92:91`, connectable, service `0xFFE0` in the advertisement. The name only
  arrives in the scan response, so a passive scan doesn't show it. While the mobile app is
  connected, the wheel doesn't advertise at all; close the app first.
- `pacewheel ble-gatt` (discovery only, no reads/writes/subscriptions):

  | Service | Characteristic | Properties | Descriptors |
  | --- | --- | --- | --- |
  | 0x1800 GAP | 0x2A00, 0x2A01, 0x2A04, 0x2AA6 | Read | |
  | 0x1801 GATT | 0x2A05 | Indicate | 0x2902 |
  | 0x180A Device Information | 0x2A23-0x2A2A, 0x2A50 | Read | |
  | **0xFFE0** | **0xFFE2** | **Write, Notify** | 0x2902 (CCCD), 0x2901 (user description) |
  | 0xFFE0 | 0xFFE1 | Read, Write, Notify | 0x2901 |

- `0xFFE4`, which the app also references, does not exist on this wheel.
- **Commands go to `0xFFE1`; responses come as notifications on `0xFFE2`.** JADX couldn't
  decompile the app's GATT callback, but its smali (disassembled with the baksmali library
  bundled in the jadx-headless-mcp JAR) shows `onServicesDiscovered` storing `0xFFE1` as the
  write characteristic and calling `enableNotification` (CCCD `01 00`) on `0xFFE2`.
  An earlier guess (commands and responses both on `0xFFE2`) was wrong; see the exchange log
  below.

### Device Information and descriptions (read-only, `pacewheel ble-info`)

| Attribute | Value |
| --- | --- |
| Device Name (0x2A00) | `FFB WHEEL PACE V987` |
| Preferred connection params (0x2A04) | interval 100-200 ms, latency 0, timeout 10 s |
| Model / Serial / Firmware / Hardware / Software / Manufacturer (0x2A24-0x2A29) | literally `Model Number`, `Serial Number`, `Firmware Revision`, … |
| Regulatory (0x2A2A) | `FE 00` + `experimental` |
| System ID (0x2A23) | all zero |
| PnP ID (0x2A50) | `01 D7 07 00 00 10 01` |
| 0xFFE2 user description | `Characteristic 4` |
| 0xFFE1 user description | `Characteristic 1` |

- The Device Information strings are unchanged defaults of Texas Instruments' BLE sample
  firmware (SimpleBLEPeripheral / SimpleGATTProfile), so they carry no real version info. The
  firmware version has to come from the state response (`D0`, bytes 3-4).
- In TI's sample profile, "Characteristic 4" is the notifying characteristic, which matches
  `0xFFE2` carrying the responses. "Characteristic 1" (`0xFFE1`) is the command input.

### Exchange log

Every write to the wheel's configuration channel, in order, each approved byte for byte:

| # | Date | Writes | Result |
| --- | --- | --- | --- |
| 1 | 2026-10-17 | `0xFFE2` CCCD `01 00`; `0xFFE2` ← `50 01`; `0xFFE2` CCCD `00 00` | writes accepted, **no notification within 2 s**. `50 01` went to the response characteristic by mistake (wrong guess); no visible change on the wheel |
| 2 | 2026-10-17 | `0xFFE2` CCCD `01 00`; `0xFFE1` ← `50 01`; `0xFFE2` CCCD `00 00` | writes accepted, **no notification within 2 s**. Wheel connected over USB at the same time; mobile app closed |
| 3 | 2026-10-17 | none: with USB unplugged (power adapter only) the wheel was unreachable and a 15 s active scan didn't see it advertising | the BLE module seems to need USB power or to be off without a USB host |
| 4 | 2026-10-17 | `0xFFE2` CCCD `01 00`; `0xFFE1` ← `50 01`; `0xFFE2` CCCD `00 00` (handler also on `0xFFE1`, nothing written there), 5 s wait, USB connected | **answer on `0xFFE2`:** `D0 01 01 46 00 02 02 01` + 12 zero bytes. Nothing on `0xFFE1` |

Exchange 4 decoded with the app's layout: mode 1, firmware version `0x0046` (the same value
as the USB `bcdDevice`, which confirms the parsing), pedal state 2, shifter state 2, angle
mode 1 (probably 1080°). The response is 20 bytes. Byte 1 echoes the request's sub-command.
Exchange 2 most likely failed on timing (2 s wait right after connecting).

| 5 | 2026-10-17 | `0xFFE2` CCCD `01 00`; `0xFFE1` ← `51 01 00` … `51 01 05` (one at a time, waiting for each answer); `0xFFE2` CCCD `00 00` | **all six `D1` pages received**, backup saved locally in `backups/` (git-ignored) |

Profile of mode 1 as read in exchange 5 (factory/current settings):

| Page | Raw (after `D1 01 <page>`) | Reading |
| --- | --- | --- |
| 0 | `00 01 02 03 04 05 06 07 08 09 0A 0B 46 0D 11 10 00` | button map, mostly identity order |
| 1 | `0E 0F 00 14 15 16 17 18 19 1A 1B 1C 1D 1E 1F 20 21` | button map continued |
| 2 | `22 23` `84 03` `64` `58` `50` `50` `50` `00` | 2 more map bytes; params[0] = 0x0384 = **900** (rotation angle?); params[1..5] = **100, 88, 80, 80, 80**; FFB reverse off |
| 3, 4, 5 | `00 00 00 00` `55 05 55 55` `AA 0A AA AA` `FF 0F FF FF` | throttle/brake/clutch curves: points (0,0) (1365,21845) (2730,43690) (4095,65535), i.e. linear, 12-bit in → 16-bit out |

The meaning of params[1..5] is still a guess from the app's `writeForce(spring, damper,
friction, inertia)` and `writeForceFeedback(ffb, reverse)`: likely FFB 100 and spring 88 /
damper 80 / friction 80 / inertia 80. Changing one value and reading the page back will tell.

| 6 | 2026-10-17 | `0xFFE2` CCCD `01 00`; `0xFFE1` ← `53 09 01 28 50 50 50`, then `51 01 02`; `0xFFE2` CCCD `00 00` | `D3 09 01 28 50 50 50` (command echoed = accepted); page 2 now `22 23 84 03 64 28 50 50 50 00`: **only params[2] changed, 88 → 40** |

Exchange 6 confirms the `53 09 01` layout: `<spring> <damper> <friction> <inertia>` map to
params[2..5], and the change is stored in the profile. Undo: `53 09 01 58 50 50 50`.

User feedback after exchange 6: **the idle centering became clearly weaker.** So params[2]
is the firmware centering spring that DirectInput couldn't reach in Phase 2.

| 7 | 2026-10-17 | `0xFFE2` CCCD `01 00`; `0xFFE1` ← `53 09 01 58 50 50 50`, then `51 01 02`; `0xFFE2` CCCD `00 00` | page 2 back to `22 23 84 03 64 58 50 50 50 00` (original). The `D3` echo didn't arrive this time; the readback confirms the value |

### Not touched

- The bundled PXN code has a firmware update (OTA) screen. Firmware update paths are out of
  scope and must never be triggered.
- `53 08` (reset) and `52` (write profile) change stored settings; they need explicit
  approval of the exact bytes, and a backup of all six profile pages read first.

### Next steps

1. ~~BLE scan from Windows~~ done: the wheel advertises as `FFB WHEEL PACE V987`.
2. ~~Read-only GATT discovery~~ done; the app's smali shows commands go to `0xFFE1` and
   responses come on `0xFFE2`.
3. ~~Read Device Information and `0x2901`~~ done: TI sample placeholders, no version; `0x2901`
   descriptions of `0xFFE1`/`0xFFE2`.
4. ~~First request `50 01`~~ done (exchange 4); ~~profile backup~~ done (exchange 5).
5. ~~Change the centering spring with `53 09 01`~~ done and undone (exchanges 6-7): it works
   and is felt.
6. Next ideas: map params[1] (100, probably FFB strength via `53 03`) and params[0] (900,
   rotation angle via `53 04`); then a `pacewheel spring <0-100>` style command.
