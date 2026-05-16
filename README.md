# GM ECU Simulator

A standalone Windows app that emulates one or more GM (GMLAN / GMW3110-2010) ECUs and **registers itself as a real J2534 PassThru device**. Any J2534-aware host - Tech 2 Win, GDS, MDI, your own logger - loads it via the standard registry path and connects to it as if it were a Tactrix OpenPort or MongoosePro.

> **Disclaimer:** This codebase was written 100% by AI (Claude Code). It builds, runs, and registers correctly against real J2534 hosts, but every line of source - protocol handlers, IPC layer, native shim, UI - was produced by a model. Treat it accordingly: read the code before you trust it with anything important.

![1.00](docs/screenshots/main-editor.png)

## Supported protocols

`PassThruConnect` accepts two protocols:

* **`ProtocolID.CAN`** **(5)** - raw CAN frame forwarding. The host frames its own ISO-TP (PCI byte first, then payload); the simulator handles the ECU-side half via `IsoTpReassembler` and `IsoTpFragmenter`, honouring per-ECU FC.BS / FC.STmin from the Selected ECU inspector.
* **`ProtocolID.ISO15765`** **(6)** - the shim runs ISO 15765-2 itself in `Iso15765Channel`: segmentation, FlowControl handshake, reassembly. The host reads and writes complete USDT payloads. `PassThruIoctl SET_CONFIG` accepts the standard BS / STmin / WFT\_MAX parameters; `PassThruStartMsgFilter` accepts `FLOW_CONTROL_FILTER` for the addressing pair.

Other protocols (J1850, ISO9141, KWP2000) return `ERR_INVALID_PROTOCOL_ID`. The rejection is logged in the J2534 calls pane and surfaced as a status-bar message naming the rejected protocol.

Modern GM diagnostic stacks (Tech 2 Win, GDS, SPS) typically use ISO15765; legacy or hand-rolled testers that already implement their own ISO-TP can stay on raw CAN.

## What it does

* Implements the GMW3110-2010 services a real tester needs: `$10` InitiateDiagnosticOperation, `$1A` ReadDataByIdentifier (DID), `$20` ReturnToNormalMode, `$22` ReadDataByParameterIdentifier, `$27` SecurityAccess, `$28` DisableNormalCommunication, `$2C` DynamicallyDefineDataIdentifier, `$2D` DefinePidByAddress, `$34` RequestDownload, `$36` TransferData, `$3E` TesterPresent, `$A2` ReportProgrammedState, `$A5` ProgrammingMode, `$AA` ReadDataByPacketIdentifier (periodic UUDT push, Slow / Medium / Fast bands).
* **Per-ECU diagnostic personas.** Default is the GMW3110-2010 persona; after a successful `$36` sub `$80` DownloadAndExecute the active persona switches to a UDS (ISO 14229) persona presenting the services a real GM SPS kernel exposes (`$31` RoutineControl - EraseMemory, CheckMemoryByAddress, finalisation - plus `$3E` / `$20` / second-stage `$34`/`$36`). Reverts to GMW3110 on `$20` or P3C timeout.
* Real ISO-TP segmentation / reassembly, flow-control frames, P3C timeout handling (5000 ms nominal), idle-bus detection.
* N concurrent virtual ECUs on a virtual CAN bus, routed by destination CAN ID.
* PID values synthesised from waveforms (sine / triangle / square / sawtooth / constant) or replayed from `.bin` log files. Identity DIDs (`$90` VIN, `$92`/`$98` supplier HW, `$C1`/`$C2` part numbers, `$CC` ECU diagnostic address) are editable per ECU and can be populated automatically by tracing the `$1A` handler in a real flash image.
* Bootloader-capture toggle: with capture ON, `$36` payloads are written to disk on session end so a real SPS kernel (or any other downloaded code) can be extracted and inspected.
* Defaults to the OBD-II convention (`$7E0` request, `$7E8` USDT response, `$5E8` UUDT response). Per-ECU IDs are editable.

## Security (\$27)

`$27` is implemented behind a two-layer plug-in interface so different GM seed-key flavours can be slotted in per-ECU without touching the dispatcher:

* **`ISecurityAccessModule`** - owns a whole `$27` exchange step. The bundled `Gmw3110_2010_Generic` module covers the GMW3110-2010 protocol envelope (length validation, subfunction parity, pending-seed tracking, 3-strike lockout with 10s deadline-timestamp recovery, NRC `$12` / `$22` / `$35` / `$36` / `$37` paths).
* **`ISeedKeyAlgorithm`** - the small strategy you usually write. \~30 lines. `Gmw3110_2010_Generic` wraps one and handles everything else.

Ships with three algorithms registered out of the box, selectable per-ECU in the **Security** tab:

* `gmw3110-2010-not-implemented` - deterministic seed `[0x12, 0x34]`, refuses every key. Exercises every NRC path against any J2534 host without committing real algorithm math.
* `gm-e38-test` - the GM E38 ECM algorithm (GMLAN algorithm `0x92`, also used by E67). 2-byte seed, 2-byte key. Optional `fixedSeed` JSON config for deterministic exchanges.
* `gm-t43-test` - the GM T43 (Aisin AF40) TCM algorithm. 2-byte seed, 2-byte key.

Each ECU also has a **Bypass security** checkbox that short-circuits `$27` entirely (seed `00 00`, any key accepted) for modelling stub-security levels seen on real hardware (e.g. T43 TCM at level 1).

Each ECU's chosen module ID + module config blob persist to `ecu_config.json`. Schema is currently at version 8; v1..v7 configs still load with missing fields falling back to documented defaults (BypassSecurity = false, FC.BS / FC.STmin = 0, `$36` address byte count = 4, BootloaderCapture disabled, no security module -> `$27` returns NRC `$11`).

See [`docs/Adding a Security Access ($27) Module.md`](<docs/Adding a Security Access (\$27) Module.md>) for a step-by-step walkthrough of writing a new algorithm, using the E38 algorithm as the worked example.

## Architecture (one paragraph)

A J2534 host expects a native DLL with the 14 C exports defined by SAE J2534-1 v04.04. C# can't be loaded that way, so [`PassThruShim/`](PassThruShim/) is a thin native C++ DLL (built **32-bit and 64-bit**) whose only job is to forward each PassThru call as a length-prefixed binary frame over a Windows named pipe (`\\.\pipe\GmEcuSim.PassThru`). The C# WPF app in [`GmEcuSimulator/`](GmEcuSimulator/) hosts the pipe server, dispatches frames through a `RequestDispatcher` to a `VirtualBus`, which routes by destination CAN ID to one of N `EcuNode` instances. Each ECU runs ISO-TP reassembly on inbound frames and hands the assembled USDT payload to its active diagnostic persona (`Gmw3110Persona` by default, `UdsKernelPersona` after kernel handover). Responses are enqueued back onto the channel's RX queue. [`Core/`](Core/) is the simulator engine (bus, ECU model, service handlers, personas, ISO-TP, DPID scheduler); [`Common/`](Common/) is pure types and protocol constants; [`Shim/`](Shim/) hosts the per-channel `Iso15765Channel` that runs ISO 15765-2 when the host opens the channel as `ProtocolID.ISO15765`.

## Conformance

**J2534-1 v04.04 only.** The shim exports exactly the 14 functions defined by v04.04 and reports `04.04` from `PassThruReadVersion`. The v05.00 additions (`ScanForDevices`, `GetNextDevice`, `LogicalConnect`, ...) and the Drew Tech proprietary `PassThruGetNextCarDAQ` are **deliberately not exported**. From a J2534-Sharp host, call `api.GetDevice("")` for the default device - `api.GetDeviceList()` returns empty because that path is v05.00-only.

## Build

```PowerShell
# .NET projects (Common, Core, Shim, GmEcuSimulator)
dotnet build "GM ECU Simulator.sln" -c Debug

# Native shim - both bitnesses (J2534 hosts can be either)
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "PassThruShim\PassThruShim.vcxproj" /p:Configuration=Debug /p:Platform=x64
& $msbuild "PassThruShim\PassThruShim.vcxproj" /p:Configuration=Debug /p:Platform=Win32

# Or do all three in one shot (requires elevation):
.\Installer\Register.ps1 -Build
```

Outputs:

* `PassThruShim\x64\Debug\PassThruShim64.dll`
* `PassThruShim\Debug\PassThruShim32.dll`
* `GmEcuSimulator\bin\Debug\net9.0-windows\GmEcuSimulator.exe`

## Register as a J2534 device

Either click **J2534 -> Register as J2534 device...** in the app's menu bar (UAC prompts; the underlying script runs elevated and exits) or run `.\Installer\Register.ps1` from an elevated PowerShell directly. Both write the same standard v04.04 registry entries (`HKLM\SOFTWARE\PassThruSupport.04.04\GmEcuSim` and the `WOW6432Node` mirror, flat layout - all values directly on the `GmEcuSim` subkey).

![1.00](docs/screenshots/j2534-menu.png)

**Both bitnesses are required.** A Windows process can only `LoadLibrary` a DLL of its own bitness - 64-bit hosts load `PassThruShim64.dll`, 32-bit hosts load `PassThruShim32.dll`, never mixed. `Register.ps1` writes both registry views, each pointing at the matching shim. The titlebar pill in the app reflects the current state ("Registered (32-bit + 64-bit)" / "Not registered") after every Register / Unregister click.

**Diagnostic dialog:** **J2534 -> Show registered devices...** runs `Installer\List.ps1` (read-only, no elevation) and shows every J2534 device on the machine across both registry views, with DLL existence checks. Useful for verifying what changed and for triaging "device doesn't show in host" reports.

![1.00](docs/screenshots/registered-devices.png)

## Use

1. Run `GmEcuSimulator.exe`. The named-pipe server starts listening; the titlebar pill shows the J2534 registration state.
2. The main window's left pane lists virtual ECUs; the right pane is the Selected ECU inspector (CAN IDs, FC.BS / FC.STmin, `$36` address byte count, identity DIDs).
3. Press **Ctrl+Shift+P** (or **View -> Setup window\...**) to open the modeless **Setup window** for editing PIDs and waveforms. Each PID gets a waveform (or pulls from a `.bin` replay), a scalar / offset, a unit string, and a size (Byte / Word / DWord). The **Live** column shows the current synthesised value.
4. Launch your J2534 host. "GM ECU Simulator" appears in its device dropdown. The shim is `LoadLibrary`'d into the host process and forwards every PassThru call to the simulator.
5. The **Bus log** tab shows live CAN frames (Tx/Rx) on the left and J2534 control-plane calls (Open / Connect / Filter / ReadMsgs / ...) on the right. The **Download** tab shows the programming-flow traffic-light tree for the selected ECU. The **Bootloader** tab toggles `$36`-payload capture to disk.
6. The **Log** menu enables a streaming file-log sink that writes to `%LOCALAPPDATA%\GmEcuSimulator\logs\bus_*.csv` on a background thread. Use this for long captures - the in-window textbox doesn't virtualise and can freeze the GUI at high message rates.

![1.00](docs/screenshots/bus-log.png)

## Bin Replay

Load a `.bin` data-logger capture file (or the built-in 4-channel synthetic demo) and the simulator will replay the recorded values through your defined PIDs. ECUs and channels are auto-mapped by node type and PID address. Loop mode (HoldLast / Loop / Stop) is configurable, and the loaded path can be auto-restored on next launch.

![1.00](docs/screenshots/bin-replay.png)

## Documentation

A full user manual lives at [`docs/User Manual.pdf`](<docs/User Manual.pdf>) (source: [`docs/User Manual.docx`](<docs/User Manual.docx>)).

## Status / scope

* v04.04 conformance only - by design.
* Both `ProtocolID.CAN` and `ProtocolID.ISO15765` are validated end-to-end against the bundled `$27` flow and the full `$34`/`$36` download path.
* Glitch injection (per-service NRC / drop / corrupt-byte / random) has UI and config plumbing but is not yet consulted by the dispatcher at runtime.
* Screenshots in this README are being recaptured to match the current UI; the linked PNGs may temporarily render as broken images while that work is in flight.
* This was built primarily as a development aid for a sibling data-logger project. It's not a certified tool and not affiliated with GM.

