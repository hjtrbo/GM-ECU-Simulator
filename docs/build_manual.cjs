// GM ECU Simulator User Manual generator
//
// Regenerates docs/GM_ECU_Simulator_User_Manual.docx from the content arrays
// below. The .pdf is produced separately on Windows by opening the docx in
// Word and saving as PDF — easiest via this PowerShell one-liner from the
// repo root after running this script:
//
//   $w = New-Object -ComObject Word.Application
//   $d = $w.Documents.Open((Resolve-Path "docs\GM_ECU_Simulator_User_Manual.docx").Path, $false, $true)
//   $d.SaveAs([ref] (Join-Path (Resolve-Path "docs").Path "GM_ECU_Simulator_User_Manual.pdf"), [ref] 17)
//   $d.Close(); $w.Quit()
//
// Prerequisite: `npm install -g docx` (the docx-js library).
// Run with: `node docs/build_manual.cjs`

const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

// Resolve the global docx package — `npm root -g` works cross-platform.
const npmGlobalRoot = execSync('npm root -g').toString().trim();
const { Document, Packer, Paragraph, TextRun, Table, TableRow, TableCell, ImageRun,
        Header, Footer, AlignmentType, PageOrientation, LevelFormat, ExternalHyperlink,
        TabStopType, TabStopPosition, HeadingLevel, BorderStyle, WidthType, ShadingType,
        VerticalAlign, PageNumber, PageBreak, ImageType } = require(path.join(npmGlobalRoot, 'docx'));

// Paths derived from this script's location so the build works regardless of cwd.
const DOCS = __dirname;
const SHOTS = path.join(DOCS, 'screenshots');
const OUT_DOCX = path.join(DOCS, 'GM_ECU_Simulator_User_Manual.docx');

const BLUE = '2E75B6';
const NAVY = '1F4E79';
const HEADER_FILL = 'D5E8F0';
const NOTE_FILL = 'FFF3D6';
const CONTENT_WIDTH_DXA = 9360;

// ---------- helpers ----------
const P = (text, opts = {}) => new Paragraph({
    children: [new TextRun({ text, ...opts.run })],
    spacing: { after: 120, ...opts.spacing },
    ...opts.paragraph,
});

const Body = (text) => P(text, { spacing: { after: 160 } });

const H1 = (text) => new Paragraph({
    heading: HeadingLevel.HEADING_1,
    children: [new TextRun({ text, bold: true, color: BLUE, size: 36 })],
    spacing: { before: 360, after: 200 },
});

const H2 = (text) => new Paragraph({
    heading: HeadingLevel.HEADING_2,
    children: [new TextRun({ text, bold: true, color: BLUE, size: 26 })],
    spacing: { before: 260, after: 140 },
});

const H3 = (text) => new Paragraph({
    heading: HeadingLevel.HEADING_3,
    children: [new TextRun({ text, bold: true, color: NAVY, size: 22 })],
    spacing: { before: 200, after: 100 },
});

const Bullet = (text) => new Paragraph({
    numbering: { reference: 'bullets', level: 0 },
    children: [new TextRun({ text })],
    spacing: { after: 80 },
});

const Numbered = (text) => new Paragraph({
    numbering: { reference: 'numbered', level: 0 },
    children: [new TextRun({ text })],
    spacing: { after: 80 },
});

const Code = (text) => new Paragraph({
    children: [new TextRun({ text, font: 'Consolas', size: 20 })],
    shading: { fill: 'F4F4F4', type: ShadingType.CLEAR },
    spacing: { before: 80, after: 80 },
});

const Bold = (lead, rest) => new Paragraph({
    children: [
        new TextRun({ text: lead, bold: true }),
        new TextRun({ text: rest }),
    ],
    spacing: { after: 120 },
});

const Caption = (text) => new Paragraph({
    alignment: AlignmentType.CENTER,
    children: [new TextRun({ text, italics: true, size: 18, color: '666666' })],
    spacing: { before: 60, after: 200 },
});

function note(label, text) {
    return new Table({
        width: { size: CONTENT_WIDTH_DXA, type: WidthType.DXA },
        columnWidths: [CONTENT_WIDTH_DXA],
        rows: [new TableRow({
            children: [new TableCell({
                width: { size: CONTENT_WIDTH_DXA, type: WidthType.DXA },
                shading: { fill: NOTE_FILL, type: ShadingType.CLEAR },
                margins: { top: 140, bottom: 140, left: 200, right: 200 },
                borders: {
                    top:    { style: BorderStyle.SINGLE, size: 6, color: 'E0B040' },
                    bottom: { style: BorderStyle.SINGLE, size: 6, color: 'E0B040' },
                    left:   { style: BorderStyle.SINGLE, size: 18, color: 'E0B040' },
                    right:  { style: BorderStyle.SINGLE, size: 6, color: 'E0B040' },
                },
                children: [new Paragraph({
                    children: [
                        new TextRun({ text: label + ' ', bold: true }),
                        new TextRun({ text }),
                    ],
                })],
            })],
        })],
    });
}

function image(file, widthPx, heightPx, captionText) {
    const fullPath = path.join(SHOTS, file);
    if (!fs.existsSync(fullPath)) return [P(`[figure: ${file} — file missing]`)];
    return [
        new Paragraph({
            alignment: AlignmentType.CENTER,
            children: [new ImageRun({
                type: 'png',
                data: fs.readFileSync(fullPath),
                transformation: { width: widthPx, height: heightPx },
                altText: { title: captionText, description: captionText, name: file },
            })],
            spacing: { before: 120, after: 60 },
        }),
        Caption(captionText),
    ];
}

function dataTable(headers, rows, colWidths) {
    const border = { style: BorderStyle.SINGLE, size: 4, color: 'BFBFBF' };
    const cellBorders = { top: border, bottom: border, left: border, right: border };

    const headerRow = new TableRow({
        tableHeader: true,
        children: headers.map((h, i) => new TableCell({
            width: { size: colWidths[i], type: WidthType.DXA },
            shading: { fill: BLUE, type: ShadingType.CLEAR },
            borders: cellBorders,
            margins: { top: 80, bottom: 80, left: 120, right: 120 },
            children: [new Paragraph({ children: [new TextRun({ text: h, bold: true, color: 'FFFFFF' })] })],
        })),
    });

    const dataRows = rows.map((row, idx) => new TableRow({
        children: row.map((cell, i) => new TableCell({
            width: { size: colWidths[i], type: WidthType.DXA },
            shading: idx % 2 === 0
                ? undefined
                : { fill: 'F4F8FC', type: ShadingType.CLEAR },
            borders: cellBorders,
            margins: { top: 70, bottom: 70, left: 120, right: 120 },
            children: [new Paragraph({ children: [new TextRun({ text: cell })] })],
        })),
    }));

    return new Table({
        width: { size: colWidths.reduce((a, b) => a + b, 0), type: WidthType.DXA },
        columnWidths: colWidths,
        rows: [headerRow, ...dataRows],
    });
}

// ---------- content ----------

const titleBlock = [
    new Paragraph({ spacing: { before: 2400 } }),
    new Paragraph({
        alignment: AlignmentType.CENTER,
        children: [new TextRun({ text: 'GM ECU Simulator', bold: true, color: BLUE, size: 64 })],
        spacing: { after: 120 },
    }),
    new Paragraph({
        alignment: AlignmentType.CENTER,
        children: [new TextRun({ text: 'User Manual', color: '888888', size: 36, italics: true })],
        spacing: { after: 600 },
    }),
    new Paragraph({
        alignment: AlignmentType.CENTER,
        children: [new TextRun({ text: 'J2534 v04.04 PassThru Device Emulator', size: 24, color: '444444' })],
        spacing: { after: 80 },
    }),
    new Paragraph({
        alignment: AlignmentType.CENTER,
        children: [new TextRun({ text: 'for GMLAN / GMW3110-2010', size: 24, color: '444444' })],
        spacing: { after: 600 },
    }),
    new Paragraph({
        alignment: AlignmentType.CENTER,
        children: [new TextRun({ text: 'Version 1.1 — May 2026', size: 22, color: '666666' })],
    }),
    new Paragraph({ children: [new PageBreak()] }),
];

const sec1 = [
    H1('1. Introduction'),
    Body('GM ECU Simulator is a Windows desktop application that emulates one or more GM (GMLAN / GMW3110) Engine Control Units and registers itself as a real SAE J2534-1 v04.04 PassThru device. Any J2534-aware diagnostic host — Tech 2 Win, GDS, or a custom scan tool — discovers it through the standard Windows registry path and connects to it exactly as it would connect to a physical hardware interface such as the Tactrix OpenPort or MongoosePro.'),
    Body('PID values are synthesised in real time from configurable waveforms (sine, triangle, square, sawtooth, constant, or file-stream replay). The waveform parameters are editable live, without restarting.'),
    H2('1.1 Supported Diagnostic Services'),
    dataTable(
        ['Service', 'Name', 'Description'],
        [
            ['$22', 'Read Data by Identifier',      'Reads one or more PIDs synthesised from waveforms'],
            ['$2C', 'Dyn. Define Data ID',         'Defines custom DPIDs for periodic UUDT streaming'],
            ['$2D', 'Write Memory by Address',     '32-bit memory-address writes mirrored to matching PID'],
            ['$AA', 'Periodic UUDT Push',          'Streams DPID values at Slow / Medium / Fast rates'],
            ['$27', 'Security Access',             'Modular seed/key unlock — see §8'],
            ['$3E', 'Tester Present',              'Keeps the session alive; resets P3C timeout'],
            ['$10', 'Start Diagnostic Session',    'Transitions ECU into programming or extended mode'],
            ['$20', 'Return to Normal',            'Graceful session teardown and DPID scheduler reset'],
        ],
        [1200, 2960, 5200]
    ),
];

const sec2 = [
    H1('2. Architecture'),
    Body('The simulator is split into two components that communicate over a Windows named pipe (\\\\.\\pipe\\GmEcuSim.PassThru).'),
    H2('2.1 PassThruShim (native DLL)'),
    Body('A thin C++ DLL built for both x64 and x86. Registered in the Windows registry as a J2534 v04.04 device under HKLM\\SOFTWARE\\PassThruSupport.04.04\\GmEcuSim and the WOW6432Node mirror. When a diagnostic host loads it with LoadLibrary, it opens the named pipe and forwards all 14 PassThru API calls as length-prefixed binary frames. The shim exports exactly the 14 functions mandated by SAE J2534-1 v04.04 — no v05.00 extensions, no Drew Tech proprietary exports.'),
    H2('2.2 GmEcuSimulator.exe (WPF application)'),
    Body('A WPF .NET 9 application. On startup it opens the named pipe server and begins accepting connections. The RequestDispatcher maps each incoming IPC message type to VirtualBus operations. The VirtualBus routes incoming CAN frames by destination ID to one of N EcuNode instances. Each EcuNode runs ISO-TP reassembly on inbound frames and dispatches the assembled USDT payload to the matching service handler. Responses are enqueued back onto the channel RX queue for the shim to drain via PassThruReadMsgs.'),
    H2('2.3 Both bitnesses are required'),
    Body('A Windows process can only load a DLL matching its own bitness. 64-bit hosts (e.g. the DataLogger application) load PassThruShim64.dll; 32-bit hosts (Tech 2 Win, GDS, Bosch MDI) load PassThruShim32.dll. The OS automatically redirects 32-bit registry reads to WOW6432Node. Register.ps1 writes both registry views pointing at the matching DLL.'),
];

const sec3 = [
    H1('3. Getting Started'),
    H2('3.1 Prerequisites'),
    Bullet('Windows 10 or Windows 11 (64-bit)'),
    Bullet('Administrator rights (required for registry registration)'),
    Bullet('.NET 9 Runtime (included in build output)'),
    Bullet('Microsoft Visual C++ Redistributable 2022 (required by the native shim)'),
    H2('3.2 First Run'),
    Numbered('Build the solution with `dotnet build "GM ECU Simulator.sln"` and the native shim with MSBuild, or use pre-built binaries.'),
    Numbered('Launch GmEcuSimulator.exe. The named pipe server starts automatically and the WPF editor opens.'),
    Numbered('Click J2534 > Register as J2534 device.... A UAC elevation prompt appears; approve it to write the registry entries.'),
    Numbered('The status bar updates to Registered (32-bit + 64-bit).'),
    Numbered('Your J2534 diagnostic host now lists "GM ECU Simulator" in its device dropdown.'),
    Body('To undo registration, use J2534 > Unregister. To verify what is registered, use J2534 > Show registered devices....'),
];

const sec4 = [
    H1('4. Main Editor'),
    Body('The main editor is divided into three areas: the ECU list on the left, the PID table in the centre, and the waveform configuration panel on the right. A tab strip at the bottom provides access to Bus Log, Bin Replay, Security access ($27), and Glitch Settings.'),
    ...image('main-editor.png', 600, 330, 'Figure 1 — Main editor with ECM and TCM in the ECU list, Engine RPM and MAP sensor PIDs, and the sine-wave waveform panel on the right.'),
    H2('4.1 ECU List'),
    Body('Lists all virtual ECU nodes loaded in the simulator. Each entry shows the ECU name, request CAN ID, and response CAN ID. Click an ECU to load its PIDs into the centre table. Use the toolbar buttons or Edit menu to add, remove, or clone ECUs.'),
    H2('4.2 PID Table'),
    Body('Lists all Data Identifiers (DIDs) configured for the selected ECU. Each row shows the two-byte PID identifier, a human-readable name, scaling formula, and the current synthesised value updating in real time. Click a row to load its waveform parameters into the right-hand panel.'),
    H2('4.3 Waveform Panel'),
    Body('Controls how the selected PID value evolves over time. The available shapes are:'),
    Bullet('Sine — Smooth sinusoidal oscillation between (Offset - Amplitude) and (Offset + Amplitude).'),
    Bullet('Triangle — Linear ramp up then ramp down.'),
    Bullet('Square — Alternates instantly between two fixed levels.'),
    Bullet('Sawtooth — Linear ramp up with instant reset.'),
    Bullet('Constant — Fixed value equal to Offset; Amplitude and Frequency are ignored.'),
    Bullet('File Stream — Replays values read sequentially from a CSV or binary log file.'),
    Body('All waveform parameter changes take effect immediately without restarting. The Frequency (Hz) field controls oscillation rate for time-varying shapes.'),
];

const sec5 = [
    H1('5. Bus Log'),
    Body('The Bus Log tab captures all J2534 API calls made by connected hosts and the resulting CAN frames on the virtual bus. Enable it by checking Log traffic in the tab header.'),
    ...image('bus-log.png', 600, 340, 'Figure 2 — Bus Log tab showing Rx $22 requests from the host at 0x7E0 and Tx $62 responses from ECM at 0x7E8, alongside the J2534 API call trace.'),
    Body('The left column shows raw ISO-TP / CAN frames with direction (Rx/Tx), CAN arbitration ID, and hex payload. The right column shows the corresponding J2534 API trace (PassThruOpen, PassThruConnect, PassThruWriteMsgs, PassThruReadMsgs, etc.) with return codes, confirming the host/simulator handshake.'),
    Body('Click Clear to wipe the log. The log is in-memory only and is not persisted to disk.'),
    note('Warning — performance limitation:',
        'Enable Log traffic only for short diagnostic bursts (under 10 seconds on high-speed runs). The text box does not virtualise its content and becomes quickly overwhelmed at high message rates, causing the GUI to freeze. This is a known optimisation issue slated for a future build.'),
];

const sec6 = [
    H1('6. Bin Replay'),
    Body('The Bin Replay tab lets you load a logged binary data file (typically recorded by the companion GM DataLogger application) and feed its channel values back through the simulator as if they were live ECU responses. This enables repeatable testing of diagnostic software against a known data set.'),
    ...image('bin-replay.png', 600, 340, 'Figure 3 — Bin Replay tab with a four-channel demo file loaded. State: Armed. Channels: RPM, TPS, Coolant Temp (ECM) and Trans Temp (TCM).'),
    Bullet('Load — Opens a file picker to select the .bin replay file.'),
    Bullet('State — Shows the current playback state: Idle, Armed, or Playing.'),
    Bullet('Channels — Lists data channels in the file with source ECU, PID ID, and unit.'),
    Bullet('Arm / Disarm — Prepares the replay engine; playback starts on the next J2534 host connection.'),
    Body('While replay is active, waveform-synthesised values for matching PIDs are replaced by the file data. PIDs not present in the file continue to use their configured waveforms.'),
];

const sec7 = [
    H1('7. Glitch Settings'),
    Body('The Glitch Settings tab injects controlled faults into simulator responses. For each diagnostic service on each ECU you can set a fault probability (0-100%) and the action to take when the fault fires. Useful for testing how host software handles unexpected ECU behaviour.'),
    Body('Available glitch actions:'),
    Bullet('EmitNrc — Respond with a Negative Response Code drawn from the NRC pool instead of the normal positive response.'),
    Bullet('Drop — Silently discard the request; the host times out waiting.'),
    Bullet('CorruptByte — Send the normal response with one byte flipped at random.'),
    Bullet('Random — Choose one of the above actions randomly each time the fault fires.'),
    Body('The NRC Pool checkboxes select which NRC codes the EmitNrc action draws from: $11 (Service Not Supported), $12 (Sub-Function Not Supported), $22 (Conditions Not Correct), $31 (Request Out of Range), $33 (Security Access Denied), $78 (Busy Response Pending).'),
    note('Note — not yet active:',
        'The glitch injection UI is fully built and all settings are saved to the config file, but the feature currently has no effect at runtime. The bus logic does not yet consult the glitch configuration. Full implementation is slated for a future release.'),
];

// NEW SECTION — Security Access ($27)
const sec8 = [
    H1('8. Security Access ($27)'),
    Body('The Security access ($27) tab configures GMW3110 Service $27 SecurityAccess per ECU. The simulator implements $27 behind a two-layer plug-in interface so different GM seed-key flavours (the algorithms have evolved across model years) can be slotted in without recompiling the dispatcher.'),
    H2('8.1 Architecture in 30 seconds'),
    Bullet('ISecurityAccessModule — owns one full $27 exchange step. The bundled Gmw3110_2010_Generic implementation handles all the protocol envelope concerns: length validation, subfunction parity (odd = requestSeed, even = sendKey), pending-seed tracking, the 3-strike attempt counter, the 10-second lockout window, and every NRC path ($12, $22, $35, $36, $37).'),
    Bullet('ISeedKeyAlgorithm — the small strategy interface most users actually write. It supplies SeedLength, KeyLength, SupportedLevels, GenerateSeed and ComputeExpectedKey. Gmw3110_2010_Generic wraps an instance and handles everything else.'),
    Bullet('SecurityModuleRegistry — maps a string ID (persisted in ecu_config.json) to a factory. Each ECU gets its own module instance, so per-ECU bookkeeping is isolated.'),
    H2('8.2 The wire exchange'),
    Body('A full successful unlock looks like this (ISO-TP Single Frames on the ECU\'s physical request and USDT response CAN IDs):'),
    Code('Tester → ECU   02 27 01                  // requestSeed level 1'),
    Code('ECU   → Tester 04 67 01 SS SS            // seed bytes'),
    Code('Tester → ECU   04 27 02 KK KK            // sendKey'),
    Code('ECU   → Tester 02 67 02                  // positive — unlocked'),
    Body('Failed unlock paths:'),
    Bullet('Invalid key — NRC $7F 27 35. Attempt counter increments.'),
    Bullet('Third failure — NRC $7F 27 36 and the ECU arms a 10-second lockout deadline.'),
    Bullet('Any $27 request inside the lockout window — NRC $7F 27 37. Deadline self-heals; the next request after expiry is processed normally with the counter reset.'),
    Bullet('sendKey without a prior requestSeed for the same level — NRC $7F 27 22.'),
    Bullet('Malformed payload, reserved subfunction ($00 / $7F), or unsupported level — NRC $7F 27 12.'),
    H2('8.3 Configuring per ECU'),
    Body('In the editor, select the ECU you want to configure, then open the Security access ($27) tab. The Module dropdown lists every algorithm registered in SecurityModuleRegistry, prefixed with a synthetic "(none)" entry. Picking (none) leaves $27 unsupported on that ECU; the simulator returns NRC $11 ServiceNotSupported to any incoming request.'),
    Body('Beneath the dropdown is a key/value editor. Each row becomes one top-level property in the JSON object passed to the algorithm\'s LoadConfig. Values persist as JSON strings; the algorithm is responsible for parsing them as needed (hex bytes, integers, etc.). The grid round-trips to the SecurityModuleConfig entry under that ECU in ecu_config.json.'),
    Bold('Example — ', 'with Module set to gm-e38-test, one row:'),
    Code('Key: fixedSeed       Value: 1234'),
    Body('…fixes the seed to 0x1234 for every $27 $01 on that ECU, which is useful for deterministic unit tests and for stepping through a tester\'s handler code without chasing a moving target. The corresponding key the tester must send back is E38(0x1234) = 0x96CE.'),
    Bold('Multiple rows — ', 'each becomes one property in a flat JSON object. Algorithms that take more than one tunable read each by key name; algorithms that take fewer simply ignore the extras. Example with hypothetical extra metadata:'),
    Code('Key: fixedSeed       Value: 1234'),
    Code('Key: notes           Value: matches VIN ABC123'),
    Body('would serialise to { "fixedSeed": "1234", "notes": "matches VIN ABC123" }. The gm-e38-test algorithm reads fixedSeed and silently ignores notes — the row round-trips to disk but has no effect on the exchange.'),
    note('Common point of confusion:',
        'fixedSeed is optional. With the K/V grid completely empty, gm-e38-test still works — it just generates a random seed per request and validates whatever key the tester computes from it client-side. Both sides only need to agree on the algorithm; the config tunes behaviour, it does not gate it.'),
    Bullet('Empty key, value-only — that row is skipped on serialise.'),
    Bullet('Duplicate key on two rows — the row evaluated last wins (Dictionary semantics).'),
    Bullet('Key the module does not recognise — silently ignored. No error surface for typos. Verify the algorithm consumed your config by triggering an exchange and watching the wire.'),
    H2('8.4 Live state and Reset'),
    Body('The strip above the K/V grid displays the selected ECU\'s live security state, refreshed at 10 Hz from the main UI tick. It reflects runtime state, not config:'),
    Bullet('Status — Locked / Unlocked (level N) / Locked out — X.X s remaining. The countdown ticks down in 0.1 s steps; once the deadline elapses the next $27 request resets the counter automatically.'),
    Bullet('Failed attempts — N / 3. Increments on each NRC $35; rolls back to 0 on a successful unlock or after the lockout deadline passes.'),
    Bullet('Pending seed — the level and bytes of the last requestSeed response, or "(none)". Cleared on a successful unlock or when the lockout arms.'),
    Body('Per GMW3110 spec, the unlocked level persists across $20 ReturnToNormalMode, P3C TesterPresent timeouts, and host disconnects (PassThruDisconnect). Only a power-cycle clears it — and the simulator treats its own process restart as the equivalent of a power-cycle. So opening the simulator and seeing "Unlocked" with an empty K/V grid is normal if a prior tester session already unlocked it.'),
    Body('The Reset state button next to the Module dropdown re-locks the ECU and clears the attempt counter, pending seed, and lockout deadline in one click — without restarting the simulator. Equivalent to a power-cycle for the $27 subsystem of that one ECU. Useful for iterative testing: verify lockout-then-recovery without waiting 10 s, retry a failed unlock cleanly, etc.'),
    H2('8.5 Bundled algorithms'),
    Body('The simulator ships with two algorithms registered out of the box:'),
    H3('gmw3110-2010-not-implemented'),
    Body('A placeholder stub. Returns the deterministic seed [0x12, 0x34] and refuses every key, regardless of what was sent. The intent is to exercise every NRC path end-to-end (requestSeed → 67 01 12 34, sendKey → 7F 27 35, three failures → 7F 27 36, lockout window → 7F 27 37, recovery) without committing any real algorithm math to the source tree.'),
    H3('gm-e38-test'),
    Body('The GM E38 ECM algorithm (also known as GMLAN algorithm 0x92; the same algorithm is used by the E67 ECM). 2-byte seed, 2-byte key, level 1 only. Random seed per request by default; supply a fixedSeed config entry for deterministic exchanges. The algorithm is documented openly in the GM tuning community; the simulator implementation is verified against five test vectors in Tests.Unit/Security/E38AlgorithmTests.cs.'),
    note('On responsible use:',
        'These algorithms are included as development aids for testing J2534 hosts against the simulator. They are not intended as tools for bypassing security on a vehicle you do not own or have rights to.'),
    H2('8.6 Writing a custom module'),
    Body('Adding a new algorithm is approximately 30 lines of C# plus a one-line registry update. The typical shape:'),
    Numbered('Implement ISeedKeyAlgorithm in Core/Security/Algorithms/. Declare SeedLength, KeyLength, SupportedLevels, then provide GenerateSeed (writes seed bytes into a buffer) and ComputeExpectedKey (writes the key the tester is expected to send back). Optionally accept configuration via LoadConfig(JsonElement?).'),
    Numbered('Register in SecurityModuleRegistry\'s static constructor: Register("my-algo", () => new Gmw3110_2010_Generic(new MyAlgorithm(), id: "my-algo")).'),
    Numbered('Add unit tests under Tests.Unit/Security/. Document at least one (seed, expected key) test vector in source. If you have a captured pair from real hardware, prefer that to a self-derived vector.'),
    Body('For algorithms whose protocol flow does not fit the standard requestSeed / sendKey envelope (multi-message exchanges, MAC-based schemes), implement ISecurityAccessModule directly and bypass Gmw3110_2010_Generic. See docs/security-access-modules.md for the full walkthrough using the E38 algorithm as a worked example.'),
];

const sec9 = [
    H1('9. J2534 Menu'),
    Body('The J2534 menu manages the simulator\'s registration as a PassThru device. Register and Unregister require administrator rights; a UAC elevation prompt appears automatically.'),
    ...image('j2534-menu.png', 600, 360, 'Figure 4 — J2534 menu with registration management commands.'),
    Bold('Register as J2534 device... — ', 'Writes registry entries under HKLM\\SOFTWARE\\PassThruSupport.04.04\\GmEcuSim (64-bit) and WOW6432Node (32-bit), each pointing at the matching PassThruShim DLL. Re-running Register over an existing entry automatically cleans up legacy layouts.'),
    Bold('Unregister — ', 'Removes the registry entries. The simulator continues to accept pipe connections from direct clients; only J2534 host enumeration is affected.'),
    Bold('Show registered devices... — ', 'Opens a read-only modal listing every J2534 device found on this machine across both registry views, with DLL existence checks. Run this before and after Register / Unregister to verify what changed.'),
    ...image('registered-devices.png', 600, 360, 'Figure 5 — Show Registered Devices dialog listing all J2534 devices found in the Windows registry with DLL path and existence status.'),
];

const sec10 = [
    H1('10. File Menu'),
    Body('The File menu manages ECU configuration files. The default configuration is ecu_config.json in the application directory and is loaded automatically on startup. Configurations written by v1.1+ are schema version 3; v1/v2 files load with security module fields null and $27 returns NRC $11 as before.'),
    Bold('New (Ctrl+N) — ', 'Creates a blank configuration with a single default ECU.'),
    Bold('Open... (Ctrl+O) — ', 'Loads a configuration from a JSON file.'),
    Bold('Save (Ctrl+S) — ', 'Saves to the current file, or prompts for a path if unsaved.'),
    Bold('Save As... (Ctrl+Shift+S) — ', 'Saves to a new file path.'),
    Bold('Import... — ', 'Imports ECU definitions from an external format.'),
    Bold('Export... — ', 'Exports the configuration to a distributable format.'),
    Bold('Exit — ', 'Closes the application; the named pipe server shuts down gracefully.'),
];

const sec11 = [
    H1('11. Technical Reference'),
    H2('11.1 CAN ID Conventions'),
    Body('The simulator uses the OBD-II GM GMLAN CAN ID scheme matching real hardware. The GMW3110 specification uses pedagogical IDs ($241/$641) in its worked examples; those IDs are used only in unit-test fixtures and do not appear in the default configuration.'),
    dataTable(
        ['ECU', 'Request ID', 'Response ID', 'Functional ID'],
        [
            ['ECM', '0x7E0', '0x7E8', '0x5E8'],
            ['TCM', '0x7E1', '0x7E9', '0x5E9'],
        ],
        [2200, 2360, 2400, 2400]
    ),
    H2('11.2 J2534 Conformance'),
    Body('PassThruShim exports exactly the 14 functions defined by SAE J2534-1 v04.04: PassThruOpen, PassThruClose, PassThruConnect, PassThruDisconnect, PassThruReadMsgs, PassThruWriteMsgs, PassThruStartPeriodicMsg, PassThruStopPeriodicMsg, PassThruStartMsgFilter, PassThruStopMsgFilter, PassThruSetProgrammingVoltage, PassThruReadVersion, PassThruGetLastError, and PassThruIoctl. PassThruReadVersion reports firmware version 04.04. The v05.00 functions (ScanForDevices, GetNextDevice, LogicalConnect, etc.) and the Drew Tech proprietary PassThruGetNextCarDAQ are deliberately not exported.'),
    H2('11.3 Supported Protocol — CAN only'),
    Body('PassThruConnect accepts ProtocolID.CAN (5) only. Hosts that attempt to connect with ProtocolID.ISO15765 (6) or any other protocol receive ERR_INVALID_PROTOCOL_ID from PassThruConnect. The simulator also surfaces the rejection in two places so a third-party user can see why their host failed to connect: a diagnostic entry in the J2534 calls pane ("[connect] rejected: protocol ISO15765 not supported — sim is CAN-only") and a status-bar message at the bottom of the main window.'),
    Body('CAN-only is a deliberate scope decision, not a bug. Hosts must therefore do their own ISO-TP framing in the PassThruWriteMsgs payload — prepending the PCI byte for Single Frames, splitting larger USDT messages into First Frame + Consecutive Frames, and handling Flow Control. On a real GM ECU, ProtocolID.ISO15765 mode (where the J2534 driver handles ISO-TP for the host) is the more common choice; against the simulator, point your host at ProtocolID.CAN and supply the PCI yourself.'),
    H2('11.4 Named Pipe Protocol'),
    Body('The shim communicates with the simulator over the named pipe \\\\.\\pipe\\GmEcuSim.PassThru using length-prefixed binary frames. Each frame starts with a 4-byte little-endian length followed by a message-type byte and the marshalled parameters. The RequestDispatcher maps each message type to the corresponding VirtualBus operation and enqueues the response.'),
    H2('11.5 ISO-TP Transport'),
    Body('Inbound CAN frames are processed by the ISO-TP reassembler, which handles Single Frame (SF), First Frame + Consecutive Frame (FF/CF), and Flow Control (FC) types per ISO 15765-2. Assembled USDT payloads are dispatched to the appropriate GMW3110 service handler by service ID (first payload byte).'),
    H2('11.6 Tester Present and Session Timeout'),
    Body('Each ECU tracks its own P3C (Tester Present) timer. If no $3E request arrives within 5 seconds (P3Cnom), the ECU runs EcuExitLogic: the DPID scheduler is cleared, $2D-defined dynamic PIDs are pruned, and an unsolicited $60 response is emitted. The same exit logic fires on graceful $20 Return-to-Normal and on host-vanish detection (IdleBusSupervisor monitors inactivity on the VirtualBus). $27 SecurityAccess state (unlocked level, failed-attempt counter, lockout deadline) is retained across $20 per the GMW3110 specification — only a power-cycle (simulator restart) resets it.'),
    H2('11.7 NodeState'),
    Body('All per-ECU runtime state lives in a unified NodeState container exposed as node.State on EcuNode. This includes the ISO-TP reassembler, the dynamic DPID map, the $2D dynamic-PID set, the P3C TesterPresent state, the last enhanced channel, and the security-access fields (unlocked level, pending seed, failed attempts, lockout deadline, opaque module-state slot). Future services consult node.State for preconditions like "ECU must be unlocked at level N" by reading the same object that $27 writes.'),
];

// ---------- assemble ----------

const allContent = [
    ...titleBlock,
    ...sec1, ...sec2, ...sec3, ...sec4, ...sec5, ...sec6, ...sec7, ...sec8, ...sec9, ...sec10, ...sec11,
];

const doc = new Document({
    creator: 'GM ECU Simulator',
    title: 'GM ECU Simulator User Manual',
    description: 'J2534 v04.04 PassThru Device Emulator for GMLAN / GMW3110-2010',
    styles: {
        default: { document: { run: { font: 'Arial', size: 22 } } },
        paragraphStyles: [
            { id: 'Heading1', name: 'Heading 1', basedOn: 'Normal', next: 'Normal', quickFormat: true,
              run: { size: 36, bold: true, color: BLUE, font: 'Arial' },
              paragraph: { spacing: { before: 360, after: 200 }, outlineLevel: 0 } },
            { id: 'Heading2', name: 'Heading 2', basedOn: 'Normal', next: 'Normal', quickFormat: true,
              run: { size: 26, bold: true, color: BLUE, font: 'Arial' },
              paragraph: { spacing: { before: 260, after: 140 }, outlineLevel: 1 } },
            { id: 'Heading3', name: 'Heading 3', basedOn: 'Normal', next: 'Normal', quickFormat: true,
              run: { size: 22, bold: true, color: NAVY, font: 'Arial' },
              paragraph: { spacing: { before: 200, after: 100 }, outlineLevel: 2 } },
        ],
    },
    numbering: {
        config: [
            { reference: 'bullets',
              levels: [{ level: 0, format: LevelFormat.BULLET, text: '•', alignment: AlignmentType.LEFT,
                style: { paragraph: { indent: { left: 720, hanging: 360 } } } }] },
            { reference: 'numbered',
              levels: [{ level: 0, format: LevelFormat.DECIMAL, text: '%1.', alignment: AlignmentType.LEFT,
                style: { paragraph: { indent: { left: 720, hanging: 360 } } } }] },
        ],
    },
    sections: [{
        properties: {
            page: {
                size: { width: 12240, height: 15840 },
                margin: { top: 1440, right: 1440, bottom: 1440, left: 1440 },
            },
        },
        headers: {
            default: new Header({
                children: [
                    new Paragraph({
                        border: { bottom: { style: BorderStyle.SINGLE, size: 8, color: BLUE, space: 4 } },
                        tabStops: [{ type: TabStopType.RIGHT, position: TabStopPosition.MAX }],
                        children: [
                            new TextRun({ text: 'GM ECU Simulator — User Manual', size: 18, color: '666666' }),
                            new TextRun({ text: '\tPage ' }),
                            new TextRun({ children: [PageNumber.CURRENT], size: 18, color: '666666' }),
                        ],
                    }),
                ],
            }),
        },
        footers: {
            default: new Footer({
                children: [
                    new Paragraph({
                        border: { top: { style: BorderStyle.SINGLE, size: 8, color: BLUE, space: 4 } },
                        alignment: AlignmentType.CENTER,
                        children: [new TextRun({ text: 'GM ECU Simulator  |  J2534 v04.04 PassThru Emulator', size: 18, color: '666666' })],
                    }),
                ],
            }),
        },
        children: allContent,
    }],
});

Packer.toBuffer(doc).then(buf => {
    fs.writeFileSync(OUT_DOCX, buf);
    console.log('Wrote ' + OUT_DOCX + ' (' + buf.length + ' bytes)');
});
