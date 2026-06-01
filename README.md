# GM ECU Simulator

A standalone Windows app that emulates one or more GM (GMLAN / GMW3110-2010) ECUs and **registers itself as a real J2534 PassThru device**. Any J2534-aware host - Tech 2 Win, GDS, MDI, your own logger - loads it via the standard registry path and connects to it as if it were a Tactrix OpenPort or MongoosePro.

It offers **two ways to connect**: the J2534 path above (the default, for real diagnostic hosts), or an **alternative raw-CAN TCP connection** - a localhost socket that lets another program (for example a separately developed gauge or data-logger simulator) join the same virtual bus directly, without going through J2534. You pick the connection from the mode dropdown; see [Supported protocols](#supported-protocols) below.

> **Disclaimer:** This codebase was written 100% by AI (Claude Code). It builds, runs, and registers correctly against real J2534 hosts, but every line of source - protocol handlers, IPC layer, native shim, UI - was produced by a model. Treat it accordingly: read the code before you trust it with anything important.

![1.00](docs/screenshots/main-editor.png)

## Supported protocols

`PassThruConnect` accepts two protocols:

* **`ProtocolID.CAN`** **(5)** - raw CAN frame forwarding. The host frames its own ISO-TP (PCI byte first, then payload); the simulator handles the ECU-side half via `IsoTpReassembler` and `IsoTpFragmenter`, honouring per-ECU FC.BS / FC.STmin from the Selected ECU inspector.
* **`ProtocolID.ISO15765`** **(6)** - the shim runs ISO 15765-2 itself in `Iso15765Channel`: segmentation, FlowControl handshake, reassembly. The host reads and writes complete USDT payloads. `PassThruIoctl SET_CONFIG` accepts the standard BS / STmin / WFT\_MAX parameters; `PassThruStartMsgFilter` accepts `FLOW_CONTROL_FILTER` for the addressing pair.

Other protocols (J1850, ISO9141, KWP2000) return `ERR_INVALID_PROTOCOL_ID`. The rejection is logged in the J2534 calls pane and surfaced as a status-bar message naming the rejected protocol.

Modern GM diagnostic stacks (Tech 2 Win, GDS, SPS) typically use ISO15765; legacy or hand-rolled testers that already implement their own ISO-TP can stay on raw CAN.

**Two transports.** Orthogonal to the protocol above is *how* a peer reaches the bus, chosen in the mode dropdown:

* **J2534** - the native `PassThruShim` DLL forwards each PassThru call over the named pipe `\\.\pipe\GmEcuSim.PassThru` (the registry-discovered path a real J2534 host uses).
* **TCP** - a localhost TCP listener (`RawCanTcpServer`) carries raw CAN frames, so a separately developed gauge / data-logger simulator can join the same virtual bus as if it were another node on the wire. The wire carries single CAN frames only (never reassembled USDT); ISO-TP runs on both ends.

## What it does

* Implements the GMW3110-2010 services a real tester needs: `$10` InitiateDiagnosticOperation, `$1A` ReadDataByIdentifier (DID), `$20` ReturnToNormalMode, `$22` ReadDataByParameterIdentifier, `$27` SecurityAccess, `$28` DisableNormalCommunication, `$2C` DynamicallyDefineDataIdentifier, `$2D` DefinePidByAddress, `$34` RequestDownload, `$36` TransferData, `$3B` WriteDataByIdentifier, `$3E` TesterPresent, `$A2` ReportProgrammedState, `$A5` ProgrammingMode, `$AA` ReadDataByPacketIdentifier (periodic UUDT push, Slow / Medium / Fast bands), `$AE` RequestDeviceControl.
* OBD-II / J1979 `$01` ShowCurrentData. On real GM silicon `$01` lives on the separate UDS-stack dispatcher, so it answers on the OBD CAN IDs (`$7DF` / `$7E0`) and a GMW3110-only request gets NRC `$11`. Values are the legislated J1979 projection over the signal layer (below) - the support-list PIDs (`$00` / `$20` / ...) are computed from each ECU's advertised subset, never stored.
* **Per-ECU diagnostic personas** (`Core/Ecu/Personas/`). Default is the GMW3110-2010 persona (`Gmw3110Persona`); after a successful `$36` sub `$80` DownloadAndExecute the active persona switches to a UDS (ISO 14229) persona (`UdsKernelPersona`) presenting the services a real GM SPS kernel exposes (`$31` RoutineControl - EraseMemory, CheckMemoryByAddress, finalisation - plus `$3E` / `$20` / second-stage `$34`/`$36`). Reverts to GMW3110 on `$20` or P3C timeout. A `FordCapturePersona` is also available for replaying captured Ford-stack traffic.
* Real ISO-TP segmentation / reassembly, flow-control frames, P3C timeout handling (5000 ms nominal), idle-bus detection.
* N concurrent virtual ECUs on a virtual CAN bus, routed by destination CAN ID.
* **Signal-centric PID values.** Each ECU has a live `EngineModel` that turns a selected scenario (Key-On Engine-Off / Idle / Cruise / Accel-Decel Sweep) into a continuously-readable, time-driven value for any signal - primaries (RPM / speed / throttle / load) ease toward their scenario targets on per-signal time constants, and derived signals (MAP / MAF / spark / fuel trims / O2) are recomputed from the primaries on every read so they stay mutually consistent. The model-specific half is a pluggable **engine character** (`IEngineCharacter` via `EngineCharacterRegistry`): "Naturally Aspirated V8" (`na-gas-v8`, the default) and "Boosted V8" (`boosted-gas-v8`) read differently for MAP / airflow / fuelling. Swap the character in the editor to swap the engine. Non-analog status (`MIL`, stored-DTC count, fuel-system status) comes from a per-ECU `DiscreteState`.
* Per PID row, a **value source** (`PidValueSource`) selects where the wire value comes from: `Signal` (driven by a named `EngineModel` signal, encoded with the row's scalar / offset / data type), `Waveform` (its own sine / triangle / square / sawtooth / constant generator, or a `.bin` log replay), or `None` (flat 0, unless the row carries a static payload).
* **Multi-mode PID stores.** A single PID row is routed by a `PidMode` selector to one of three services: `$22` ReadDataByParameterIdentifier (2-byte wire id), `$1A` ReadDataByIdentifier (1-byte DID, static payload), or `$2D` DefinePidByAddress (32-bit address mirrored to a `$F000`-range wire id at boot).
* Identity DIDs (`$90` VIN, `$92`/`$98` supplier HW, `$C1`/`$C2` part numbers, `$CC` ECU diagnostic address) are editable per ECU and can be populated automatically by tracing the `$1A` handler in a real flash image.
* **Two app modes.** "ECU Simulator" (multiple ECUs, full editor, state persists) and "DPS Simulator" (single-ECU programming-session workflow - prime from a DPS archive and drive a target ECU through a flash). The mode dropdown also selects the transport (the two transports under Supported protocols above): "ECU Simulator - J2534" or "ECU Simulator - TCP".
* Bootloader-capture toggle: with capture ON, `$36` payloads are written to disk on session end so a real SPS kernel (or any other downloaded code) can be extracted and inspected.
* Defaults to the OBD-II convention (`$7E0` request, `$7E8` USDT response, `$5E8` UUDT response). Per-ECU IDs are editable.

## Security (\$27)

`$27` is implemented behind a two-layer plug-in interface so different GM seed-key flavours can be slotted in per-ECU without touching the dispatcher:

* **`ISecurityAccessModule`** - owns a whole `$27` exchange step. The bundled `Gmw3110_2010_Generic` module covers the GMW3110-2010 protocol envelope (length validation, subfunction parity, pending-seed tracking, 3-strike lockout with 10s deadline-timestamp recovery, NRC `$12` / `$22` / `$35` / `$36` / `$37` paths). The module's `SecurityModuleBehaviour` (`Strict` or `BypassAll`) is set at construction time and is orthogonal to the cipher - the same cipher class can be wired into either.
* **`ISeedKeyAlgorithm`** - the small strategy you usually write. \~30 lines. `Gmw3110_2010_Generic` wraps one and handles everything else.

Ships with six modules registered out of the box, on a `gm-{ecmFamily}-{width}` / `gm-bypass-{width}` axis. Strict entries are named for the ECM family they target - the community "Algo 92" / "Algo 89" attribution is too unsourced to be load-bearing in the ID:

| Module ID | Seed/Key | Cipher | Behaviour |
|---|---|---|---|
| `gm-e38-2byte` | 2/2 | E38 ECM via non-DPS testers (HPT, EFILive, jakka351). `k = ~(bswap(s)+0x7D58)+0x8001`. Community-tagged "GMLAN 0x92" but the algorithm-number attribution is unsourced. | Strict |
| `gm-e92-5byte` | 5/5 | E92-family ECMs via DPS. Reverse-engineered on 2026-05-17 via a logging proxy (`tools/sa015bcr_hook/`) and verified against 7 known seed/key pairs. The "92" is grounded in the actual `algoId` byte DPS utility files for this family carry. Defaults to the E92 password captured from a 2026-05 DPS 4.52 run; override with `password` / `algoId` / `familyByte` / `fixedSeed` in `SecurityModuleConfig`. | Strict |
| `gm-e67-2byte` | 2/2 | E67 ECM. Extracted from PowerPCM_Flasher's `KeyAlgoGm_$89` (RVA 0x6670); brute-force-distinct from `gm-e38-2byte` over all 65536 seeds despite both being community-tagged "GMLAN". | Strict |
| `gm-t43-2byte` | 2/2 | T43 TCM (6T70 family) `gett43key`, ported from 6Speed.T43 FOSS source. GM algorithm number not yet documented. | Strict |
| `gm-bypass-2byte` | 2/2 | `RandomSeedCipher(2)`. Emits a non-zero random seed (or `fixedSeed` config) and accepts any key. | BypassAll |
| `gm-bypass-5byte` | 5/5 | `RandomSeedCipher(5)`. Same, 5-byte width for DPS Enhanced 5-byte utility files whose algorithm we haven't captured yet. | BypassAll |

For stub-security ECUs (T43 boot block, "let any tester through" test scenarios) select one of the `gm-bypass-*` modules. They short-circuit `$27` unconditionally - no session-state gating - so requestSeed returns seed `00 00` (DPS / CCRT "already unlocked" convention) and any sendKey is accepted.

Each ECU's chosen module ID + module config blob persist to the per-mode config file (`ecu_simulator.mode.json` in ECU Simulator mode). Legacy IDs from every prior naming pass (the original family names `gm-e38` / `gm-e67` / `gm-t43` / `gm-e92`, the brief algo-axis names `gm-algo92-2byte` / `gm-algo89-2byte` / `gm-algo92-5byte` / `gm-algo-92`, and the behaviour-named bypass entries `gm-programming-bypass` / `gm-permissive-5byte` / `gmw3110-2010-not-implemented`) are remapped to their current equivalents at load time via `SecurityModuleRegistry.NormaliseLegacyId`. Schema is currently at **version 17**, with **minimum supported version 16** - v16 was the clean-break baseline for the signal-centric redesign, so configs written before it are **rejected with a version error rather than silently migrated**. Within the v16+ range, missing fields fall back to documented defaults (FC.BS / FC.STmin = 0, `$36` address byte count = 4, BootloaderCapture disabled, no security module -> `$27` returns NRC `$11`; v16 files load with an empty live-tile dashboard).

See [`docs/Adding a Security Access ($27) Module.md`](<docs/Adding a Security Access (\$27) Module.md>) for a step-by-step walkthrough of writing a new algorithm, using the E38 algorithm as the worked example.

## Architecture (one paragraph)

A J2534 host expects a native DLL with the 14 C exports defined by SAE J2534-1 v04.04. C# can't be loaded that way, so [`PassThruShim/`](PassThruShim/) is a thin native C++ DLL (built **32-bit and 64-bit**) whose only job is to forward each PassThru call as a length-prefixed binary frame over a Windows named pipe (`\\.\pipe\GmEcuSim.PassThru`). The C# WPF app in [`GmEcuSimulator/`](GmEcuSimulator/) hosts the pipe server, dispatches frames through a `RequestDispatcher` to a `VirtualBus`, which routes by destination CAN ID to one of N `EcuNode` instances. Each ECU runs ISO-TP reassembly on inbound frames and hands the assembled USDT payload to its active diagnostic persona (`Gmw3110Persona` by default, `UdsKernelPersona` after kernel handover). Responses are enqueued back onto the channel's RX queue. [`Core/`](Core/) is the simulator engine (bus, ECU model, service handlers, personas, ISO-TP, DPID scheduler); [`Common/`](Common/) is pure types, protocol constants, and the signal layer (`EngineModel` + the pluggable `IEngineCharacter` set under `Common/Signals/`); [`Shim/`](Shim/) hosts the per-channel `Iso15765Channel` that runs ISO 15765-2 when the host opens the channel as `ProtocolID.ISO15765`, plus the alternative `RawCanTcpServer` transport. The named pipe is the default transport; selecting TCP in the mode dropdown swaps it for the localhost CAN-frame listener instead.

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

**Both bitnesses are required.** A Windows process can only `LoadLibrary` a DLL of its own bitness - 64-bit hosts load `PassThruShim64.dll`, 32-bit hosts load `PassThruShim32.dll`, never mixed. `Register.ps1` writes both registry views, each pointing at the matching shim. The titlebar pill in the app reflects the current state ("Shim Registered" when both bitnesses are present / "32-bit Shim Fault" or "64-bit Shim Fault" when only one bitness is registered / "Shim Not Registered" when neither is) after every Register / Unregister click.

**Diagnostic dialog:** **J2534 -> Show registered devices...** runs `Installer\List.ps1` (read-only, no elevation) and shows every J2534 device on the machine across both registry views, with DLL existence checks. Useful for verifying what changed and for triaging "device doesn't show in host" reports.

![1.00](docs/screenshots/registered-devices.png)

## Use

1. Run `GmEcuSimulator.exe`. The named-pipe server starts listening; the titlebar pill shows the J2534 registration state.
2. The main window is a live PID tile dashboard for the selected ECU, plus the menu bar, the mode/connection dropdown, and the two titlebar pills (selected-ECU `$27` security state, J2534 registration state). There is no ECU list on the main window - all ECU and PID editing lives in the ECU Editor (next).
3. Open the modeless **ECU Editor** via **ECU -> Open ECU settings\...** (or **Ctrl+Shift+P**). It has its own ECU list (Add / Remove / Save / Load ECU), the per-ECU settings (Name, CAN IDs, FC.BS / FC.STmin, `$27` security module, engine character, scenario), and the PID table. Each PID row has a **Mode** (`$22` / `$1A` / `$2D`), a **Signal** column that picks the value source (a named `EngineModel` signal, the row's own waveform, or none), a scalar / offset, a unit string, and a size (Byte / Word / DWord); the **Live** column shows the current synthesised value. Set the engine character (Naturally Aspirated V8 / Boosted V8) and scenario (Idle / Cruise / Accel-Decel Sweep / Key-On Engine-Off) to drive every signal-backed PID at once.
4. Launch your J2534 host. "GM ECU Simulator" appears in its device dropdown. The shim is `LoadLibrary`'d into the host process and forwards every PassThru call to the simulator.
5. Workspace tabs: **Bus log** shows live CAN frames (Tx/Rx) on the left and J2534 control-plane calls (Open / Connect / Filter / ReadMsgs / ...) on the right; **Bin Replay** (ECU Simulator) replays a `.bin` capture through the ECU's PIDs; **Glitch settings** (ECU Simulator) is the fault-injection UI (config only, not yet wired into the bus); **Captures** (DPS Simulator) browses the `$36` download captures written during programming sessions.
6. The **Log** menu enables a streaming file-log sink that writes to `%LOCALAPPDATA%\GmEcuSimulator\logs\bus logs\bus_*.csv` on a background thread. Use this for long captures - the in-window textbox doesn't virtualise and can freeze the GUI at high message rates.

![1.00](docs/screenshots/bus-log.png)

## Bin Replay

Load a `.bin` data-logger capture file (or the built-in 4-channel synthetic demo) and the simulator will replay the recorded values through your defined PIDs. ECUs and channels are auto-mapped by node type and PID address. Loop mode (HoldLast / Loop / Stop) is configurable, and the loaded path can be auto-restored on next launch.

![1.00](docs/screenshots/bin-replay.png)

## Documentation

A full user manual lives at [`docs/User Manual.pdf`](<docs/User Manual.pdf>) (source: [`docs/User Manual.docx`](<docs/User Manual.docx>)).

## License

This project is **dual-licensed**. Pick the option that matches your use:

### Free for hobbyists and enthusiasts (AGPL-3.0)

If you're an individual working on your own vehicles, a student, a researcher, or anyone else using this for **personal, non-commercial purposes**, you can use the simulator free of charge under the [GNU Affero General Public License v3.0](LICENSE). That's the whole community this was built for - have at it, modify it, share it, build on it. The only ask under AGPL is that if you publish or network-host a modified version, you publish the modified source too.

Examples of use that's free under AGPL:

* Tuning, diagnosing, or logging your own car or your friends' cars
* Learning how J2534, GMLAN, or GMW3110 works
* Academic research, coursework, or teaching
* Hobby projects, blog write-ups, YouTube videos, conference talks
* Non-profit open-source forks (kept under AGPL)

### Commercial use requires a separate license

A **paid commercial license** is required for any **business or for-profit** use. That includes (without limitation):

* Use by or within a for-profit business or entity, including internal use by its employees, contractors, or affiliates
* Bundling the simulator (or any derivative) into a commercial product or paid service
* Using the simulator to provide paid diagnostics, tuning, calibration, repair, or programming services to customers
* Hosting it as part of a SaaS or networked offering
* Any use where AGPL-3.0's source-disclosure requirements are incompatible with how you want to ship

Commercial licenses are offered on reasonable terms. To request a quote, please email **ndpryor@hotmail.com** with a short description of your intended use.

If you're not sure which side of the line your use falls on, just ask - hobbyist by default, business if money's involved.

## Status / scope

* v04.04 conformance only - by design.
* Both `ProtocolID.CAN` and `ProtocolID.ISO15765` are validated end-to-end against the bundled `$27` flow and the full `$34`/`$36` download path.
* Glitch injection (per-service NRC / drop / corrupt-byte / random) has UI and config plumbing but is not yet consulted by the dispatcher at runtime.
* Screenshots in this README are being recaptured to match the current UI; the linked PNGs may temporarily render as broken images while that work is in flight.
* This was built primarily as a development aid for a sibling data-logger project. It's not a certified tool and not affiliated with GM.

