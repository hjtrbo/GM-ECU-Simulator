#!/usr/bin/env python3
"""
GM ECU Simulator -- User Manual PDF builder.
Requires: reportlab  (pip install reportlab)
"""

import os, sys

try:
    from reportlab.lib.pagesizes import A4
    from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
    from reportlab.lib.units import cm
    from reportlab.lib.colors import HexColor, black, white
    from reportlab.lib.enums import TA_LEFT, TA_CENTER, TA_JUSTIFY
    from reportlab.platypus import (BaseDocTemplate, Frame, PageTemplate,
                                    Paragraph, Spacer, Image, PageBreak,
                                    KeepTogether, Table, TableStyle)
    from reportlab.lib import colors as rcolors
except ImportError:
    print("Installing reportlab...")
    import subprocess
    subprocess.check_call([sys.executable, "-m", "pip", "install", "reportlab"])
    from reportlab.lib.pagesizes import A4
    from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
    from reportlab.lib.units import cm
    from reportlab.lib.colors import HexColor, black, white
    from reportlab.lib.enums import TA_LEFT, TA_CENTER, TA_JUSTIFY
    from reportlab.platypus import (BaseDocTemplate, Frame, PageTemplate,
                                    Paragraph, Spacer, Image, PageBreak,
                                    KeepTogether, Table, TableStyle)
    from reportlab.lib import colors as rcolors

PAGE_W, PAGE_H = A4
MARGIN = 2.0 * cm
CONTENT_W = PAGE_W - 2 * MARGIN

BLUE       = HexColor('#1a5276')
MED_BLUE   = HexColor('#2e86c1')
GRAY       = HexColor('#7f8c8d')
LIGHT_GRAY = HexColor('#f0f3f4')

DOCS_DIR  = os.path.dirname(os.path.abspath(__file__))
SHOTS_DIR = os.path.join(DOCS_DIR, 'manual_screenshots')
OUT_PATH  = os.path.join(DOCS_DIR, 'GM_ECU_Simulator_User_Manual.pdf')


# ---------------------------------------------------------------------------
# Document template with header / footer and PDF outline bookmarks
# ---------------------------------------------------------------------------
class ManualDoc(BaseDocTemplate):
    def __init__(self, path, **kw):
        BaseDocTemplate.__init__(self, path, **kw)
        body = Frame(MARGIN, MARGIN + 0.6*cm,
                     CONTENT_W, PAGE_H - 2*MARGIN - 1.8*cm, id='body')
        self.addPageTemplates([
            PageTemplate(id='main', frames=[body], onPage=self._page_decor)
        ])

    def _page_decor(self, canv, doc):
        canv.saveState()
        # Blue top bar
        canv.setFillColor(BLUE)
        canv.rect(0, PAGE_H - 1.3*cm, PAGE_W, 1.3*cm, fill=1, stroke=0)
        canv.setFillColor(white)
        canv.setFont('Helvetica', 8)
        canv.drawString(MARGIN, PAGE_H - 0.85*cm, 'GM ECU Simulator  --  User Manual')
        if doc.page > 1:
            canv.drawRightString(PAGE_W - MARGIN, PAGE_H - 0.85*cm, 'Page %d' % doc.page)
        # Footer rule
        canv.setStrokeColor(MED_BLUE)
        canv.setLineWidth(0.5)
        canv.line(MARGIN, MARGIN + 0.3*cm, PAGE_W - MARGIN, MARGIN + 0.3*cm)
        canv.setFillColor(GRAY)
        canv.setFont('Helvetica', 7)
        canv.drawCentredString(PAGE_W / 2, MARGIN + 0.05*cm,
                               'GM ECU Simulator  |  J2534 v04.04 PassThru Emulator')
        canv.restoreState()

    def afterFlowable(self, flowable):
        if not hasattr(flowable, 'style'):
            return
        sname = flowable.style.name
        if sname not in ('h1', 'h2'):
            return
        text = flowable.getPlainText()
        key  = text.replace(' ', '_').replace('/', '_')
        level = 0 if sname == 'h1' else 1
        self.canv.bookmarkPage(key)
        self.canv.addOutlineEntry(text, key, level=level, closed=(level > 0))


# ---------------------------------------------------------------------------
# Styles
# ---------------------------------------------------------------------------
def make_styles():
    s = getSampleStyleSheet()
    return {
        'h1': ParagraphStyle('h1', parent=s['Normal'],
                             fontSize=17, fontName='Helvetica-Bold',
                             textColor=BLUE, spaceBefore=20, spaceAfter=8),
        'h2': ParagraphStyle('h2', parent=s['Normal'],
                             fontSize=12, fontName='Helvetica-Bold',
                             textColor=MED_BLUE, spaceBefore=14, spaceAfter=5),
        'body': ParagraphStyle('body', parent=s['Normal'],
                               fontSize=10, leading=15,
                               alignment=TA_JUSTIFY, spaceAfter=8),
        'bullet': ParagraphStyle('bullet', parent=s['Normal'],
                                  fontSize=10, leading=14,
                                  leftIndent=18, bulletIndent=6, spaceAfter=4),
        'caption': ParagraphStyle('caption', parent=s['Normal'],
                                   fontSize=8.5, textColor=GRAY,
                                   alignment=TA_CENTER, spaceAfter=14),
        'note': ParagraphStyle('note', parent=s['Normal'],
                               fontSize=9, leading=13, textColor=GRAY,
                               leftIndent=10, spaceAfter=8),
    }


ST = make_styles()


# ---------------------------------------------------------------------------
# Helper: embed a screenshot scaled to fit the content area
# ---------------------------------------------------------------------------
def shot(name, caption=None, width_frac=0.88):
    path = os.path.join(SHOTS_DIR, name)
    if not os.path.exists(path):
        items = [Paragraph('<i>[Screenshot: %s not found]</i>' % name, ST['note'])]
        if caption:
            items.append(Paragraph(caption, ST['caption']))
        return items
    avail = CONTENT_W * width_frac
    img = Image(path)
    ratio = img.drawWidth / float(img.drawHeight)
    img.drawWidth  = avail
    img.drawHeight = avail / ratio
    items = [Spacer(1, 0.25*cm), img]
    if caption:
        items.append(Paragraph(caption, ST['caption']))
    items.append(Spacer(1, 0.1*cm))
    return items


def bullet(text):
    return Paragraph(text, ST['bullet'])


def body(text):
    return Paragraph(text, ST['body'])


def h1(text):
    return Paragraph(text, ST['h1'])


def h2(text):
    return Paragraph(text, ST['h2'])


# ---------------------------------------------------------------------------
# Content
# ---------------------------------------------------------------------------
def build_story():
    story = []

    # ---- Cover ----
    story.append(Spacer(1, 3.5*cm))
    cover_title_st = ParagraphStyle('ct', fontSize=34, fontName='Helvetica-Bold',
                                    textColor=BLUE, alignment=TA_CENTER, spaceAfter=10)
    cover_sub_st   = ParagraphStyle('cs', fontSize=15, fontName='Helvetica',
                                    textColor=MED_BLUE, alignment=TA_CENTER, spaceAfter=6)
    cover_desc_st  = ParagraphStyle('cd', fontSize=11, textColor=GRAY,
                                    alignment=TA_CENTER, spaceAfter=4)
    story.append(Paragraph('GM ECU Simulator', cover_title_st))
    story.append(Paragraph('User Manual', cover_sub_st))
    story.append(Spacer(1, 0.6*cm))
    story.append(Paragraph('J2534 v04.04 PassThru Device Emulator', cover_desc_st))
    story.append(Paragraph('for GMLAN / GMW3110-2010', cover_desc_st))
    story.append(Spacer(1, 1.5*cm))
    story.append(Paragraph('Version 1.0  --  May 2026', cover_desc_st))
    story.append(PageBreak())

    # ---- 1. Introduction ----
    story.append(h1('1. Introduction'))
    story.append(body(
        'GM ECU Simulator is a Windows desktop application that emulates one or more '
        'GM (GMLAN / GMW3110) Engine Control Units and registers itself as a real '
        'SAE J2534-1 v04.04 PassThru device. Any J2534-aware diagnostic host -- '
        'Tech 2 Win, GDS, or a custom scan tool -- discovers it through the standard '
        'Windows registry path and connects to it exactly as it would connect to a '
        'physical hardware interface such as the Tactrix OpenPort or MongoosePro.'
    ))
    story.append(body(
        'PID values are synthesised in real time from configurable waveforms '
        '(sine, triangle, square, sawtooth, constant, or file-stream replay). '
        'The waveform parameters are editable live, without restarting.'
    ))
    story.append(h2('1.1 Supported Diagnostic Services'))
    svc_rows = [
        ['Service', 'Name', 'Description'],
        ['$22', 'Read Data by Identifier', 'Reads one or more PIDs synthesised from waveforms'],
        ['$2C', 'Dyn. Define Data ID', 'Defines custom DPIDs for periodic UUDT streaming'],
        ['$2D', 'Write Memory by Address', '32-bit memory-address writes mirrored to matching PID'],
        ['$AA', 'Periodic UUDT Push', 'Streams DPID values at Slow / Medium / Fast rates'],
        ['$3E', 'Tester Present', 'Keeps the session alive; resets P3C timeout'],
        ['$10', 'Start Diagnostic Session', 'Transitions ECU into programming or extended mode'],
        ['$20', 'Return to Normal', 'Graceful session teardown and DPID scheduler reset'],
    ]
    svc_table = Table(svc_rows,
                      colWidths=[1.5*cm, 4.5*cm, CONTENT_W - 6.0*cm])
    svc_table.setStyle(TableStyle([
        ('BACKGROUND',   (0, 0), (-1,  0), BLUE),
        ('TEXTCOLOR',    (0, 0), (-1,  0), white),
        ('FONTNAME',     (0, 0), (-1,  0), 'Helvetica-Bold'),
        ('FONTSIZE',     (0, 0), (-1, -1), 9),
        ('ROWBACKGROUNDS', (0, 1), (-1, -1), [white, LIGHT_GRAY]),
        ('GRID',         (0, 0), (-1, -1), 0.4, GRAY),
        ('VALIGN',       (0, 0), (-1, -1), 'MIDDLE'),
        ('TOPPADDING',   (0, 0), (-1, -1), 5),
        ('BOTTOMPADDING',(0, 0), (-1, -1), 5),
    ]))
    story.append(svc_table)
    story.append(PageBreak())

    # ---- 2. Architecture ----
    story.append(h1('2. Architecture'))
    story.append(body(
        'The simulator is split into two components that communicate over a Windows '
        'named pipe (<b>\\\\\\\\.\\\\ pipe\\\\GmEcuSim.PassThru</b>).'
    ))
    story.append(h2('2.1 PassThruShim (native DLL)'))
    story.append(body(
        'A thin C++ DLL built for both x64 and x86. Registered in the Windows registry '
        'as a J2534 v04.04 device under '
        '<b>HKLM\\SOFTWARE\\PassThruSupport.04.04\\GmEcuSim</b> and the WOW6432Node mirror. '
        'When a diagnostic host loads it with LoadLibrary, it opens the named pipe and '
        'forwards all 14 PassThru API calls as length-prefixed binary frames. The shim '
        'exports exactly the 14 functions mandated by SAE J2534-1 v04.04 -- no v05.00 '
        'extensions, no Drew Tech proprietary exports.'
    ))
    story.append(h2('2.2 GmEcuSimulator.exe (WPF application)'))
    story.append(body(
        'A WPF .NET 9 application. On startup it opens the named pipe server and begins '
        'accepting connections. The RequestDispatcher maps each incoming IPC message type '
        'to VirtualBus operations. The VirtualBus routes incoming CAN frames by destination '
        'ID to one of N EcuNode instances. Each EcuNode runs ISO-TP reassembly on inbound '
        'frames and dispatches the assembled USDT payload to the matching service handler. '
        'Responses are enqueued back onto the channel RX queue for the shim to drain via '
        'PassThruReadMsgs.'
    ))
    story.append(h2('2.3 Both bitnesses are required'))
    story.append(body(
        'A Windows process can only load a DLL matching its own bitness. '
        '64-bit hosts (e.g. the DataLogger application) load PassThruShim64.dll; '
        '32-bit hosts (Tech 2 Win, GDS, Bosch MDI) load PassThruShim32.dll. '
        'The OS automatically redirects 32-bit registry reads to WOW6432Node. '
        'Register.ps1 writes both registry views pointing at the matching DLL.'
    ))
    story.append(PageBreak())

    # ---- 3. Getting Started ----
    story.append(h1('3. Getting Started'))
    story.append(h2('3.1 Prerequisites'))
    for p in [
        'Windows 10 or Windows 11 (64-bit)',
        'Administrator rights (required for registry registration)',
        '.NET 9 Runtime (included in build output)',
        'Microsoft Visual C++ Redistributable 2022 (required by the native shim)',
    ]:
        story.append(bullet(p))
    story.append(Spacer(1, 0.3*cm))
    story.append(h2('3.2 First Run'))
    for i, s in enumerate([
        'Build the solution with <b>dotnet build EcuSimulator.sln</b> and the native shim '
        'with MSBuild, or use pre-built binaries.',
        'Launch <b>GmEcuSimulator.exe</b>. The named pipe server starts automatically '
        'and the WPF editor opens.',
        'Click <b>J2534 &gt; Register as J2534 device...</b>. A UAC elevation prompt '
        'appears; approve it to write the registry entries.',
        'The status bar updates to <i>Registered (32-bit + 64-bit)</i>.',
        'Your J2534 diagnostic host now lists <b>GM ECU Simulator</b> in its device dropdown.',
    ], 1):
        story.append(bullet('%d.  %s' % (i, s)))
    story.append(Spacer(1, 0.3*cm))
    story.append(body(
        'To undo registration, use <b>J2534 &gt; Unregister</b>. '
        'To verify what is registered, use <b>J2534 &gt; Show registered devices...</b>.'
    ))
    story.append(PageBreak())

    # ---- 4. Main Editor ----
    story.append(h1('4. Main Editor'))
    story.append(body(
        'The main editor is divided into three areas: the ECU list on the left, '
        'the PID table in the centre, and the waveform configuration panel on the right. '
        'A tab strip at the bottom provides access to Bus Log, Bin Replay, and Glitch Settings.'
    ))
    for fl in shot('01_main_editor.png',
                   'Figure 1 -- Main editor with ECM and TCM in the ECU list, Engine RPM '
                   'and MAP sensor PIDs, and the sine-wave waveform panel on the right.'):
        story.append(fl)

    story.append(h2('4.1 ECU List'))
    story.append(body(
        'Lists all virtual ECU nodes loaded in the simulator. Each entry shows the ECU '
        'name, request CAN ID, and response CAN ID. Click an ECU to load its PIDs into '
        'the centre table. Use the toolbar buttons or Edit menu to add, remove, or clone ECUs.'
    ))

    story.append(h2('4.2 PID Table'))
    story.append(body(
        'Lists all Data Identifiers (DIDs) configured for the selected ECU. Each row shows '
        'the two-byte PID identifier, a human-readable name, scaling formula, and the '
        'current synthesised value updating in real time. Click a row to load its waveform '
        'parameters into the right-hand panel.'
    ))

    story.append(h2('4.3 Waveform Panel'))
    story.append(body(
        'Controls how the selected PID value evolves over time. The available shapes are:'
    ))
    for name, desc in [
        ('Sine',      'Smooth sinusoidal oscillation between (Offset - Amplitude) and (Offset + Amplitude).'),
        ('Triangle',  'Linear ramp up then ramp down.'),
        ('Square',    'Alternates instantly between two fixed levels.'),
        ('Sawtooth',  'Linear ramp up with instant reset.'),
        ('Constant',  'Fixed value equal to Offset; Amplitude and Frequency are ignored.'),
        ('File Stream','Replays values read sequentially from a CSV or binary log file.'),
    ]:
        story.append(bullet('<b>%s</b> -- %s' % (name, desc)))
    story.append(body(
        'All waveform parameter changes take effect immediately without restarting. '
        'The <b>Frequency (Hz)</b> field controls oscillation rate for time-varying shapes.'
    ))
    story.append(PageBreak())

    # ---- 5. Bus Log ----
    story.append(h1('5. Bus Log'))
    story.append(body(
        'The Bus Log tab captures all J2534 API calls made by connected hosts and the '
        'resulting CAN frames on the virtual bus. Enable it by checking <b>Log traffic</b> '
        'in the tab header.'
    ))
    for fl in shot('02_bus_log.png',
                   'Figure 2 -- Bus Log tab showing Rx $22 requests from the host at 0x7E0 '
                   'and Tx $62 responses from ECM at 0x7E8, alongside the J2534 API call trace.'):
        story.append(fl)
    story.append(body(
        'The left column shows raw ISO-TP / CAN frames with direction (Rx/Tx), '
        'CAN arbitration ID, and hex payload. The right column shows the corresponding '
        'J2534 API trace (PassThruOpen, PassThruConnect, PassThruWriteMsgs, '
        'PassThruReadMsgs, etc.) with return codes, confirming the host/simulator handshake.'
    ))
    story.append(body(
        'Click <b>Clear</b> to wipe the log. The log is in-memory only and is not persisted '
        'to disk.'
    ))

    warn_st = ParagraphStyle('warn', parent=ST['body'],
                             fontSize=10, leading=14,
                             backColor=HexColor('#fef9e7'),
                             borderColor=HexColor('#f39c12'),
                             borderWidth=1, borderPadding=8,
                             leftIndent=0, spaceAfter=10)
    story.append(Paragraph(
        '<b>Warning — performance limitation:</b> Enable Log traffic only for short '
        'diagnostic bursts (under 10 seconds on high-speed runs). The text box does '
        'not virtualise its content and becomes quickly overwhelmed at high message '
        'rates, causing the GUI to freeze. This is a known optimisation issue slated '
        'for a future build.',
        warn_st))
    story.append(PageBreak())

    # ---- 6. Bin Replay ----
    story.append(h1('6. Bin Replay'))
    story.append(body(
        'The Bin Replay tab lets you load a logged binary data file (typically recorded by '
        'the companion GM DataLogger application) and feed its channel values back through '
        'the simulator as if they were live ECU responses. This enables repeatable testing '
        'of diagnostic software against a known data set.'
    ))
    for fl in shot('03_bin_replay.png',
                   'Figure 3 -- Bin Replay tab with a four-channel demo file loaded. '
                   'State: Armed. Channels: RPM, TPS, Coolant Temp (ECM) and Trans Temp (TCM).'):
        story.append(fl)
    for item in [
        '<b>Load</b> -- Opens a file picker to select the .bin replay file.',
        '<b>State</b> -- Shows the current playback state: Idle, Armed, or Playing.',
        '<b>Channels</b> -- Lists data channels in the file with source ECU, PID ID, and unit.',
        '<b>Arm / Disarm</b> -- Prepares the replay engine; playback starts on the next J2534 host connection.',
    ]:
        story.append(bullet(item))
    story.append(body(
        'While replay is active, waveform-synthesised values for matching PIDs are replaced '
        'by the file data. PIDs not present in the file continue to use their configured waveforms.'
    ))
    story.append(PageBreak())

    # ---- 7. Glitch Settings ----
    story.append(h1('7. Glitch Settings'))
    story.append(body(
        'The Glitch Settings tab injects controlled faults into simulator responses. '
        'For each diagnostic service on each ECU you can set a fault probability (0-100%) '
        'and the action to take when the fault fires. Useful for testing how host software '
        'handles unexpected ECU behaviour.'
    ))
    for fl in shot('04_glitch_settings.png',
                   'Figure 4 -- Glitch Settings for ECM. All services at 0% probability '
                   '(no faults active). NRC pool has $11, $12, $22, $31 selected.'):
        story.append(fl)
    story.append(body('Available glitch actions:'))
    for item in [
        '<b>EmitNrc</b> -- Respond with a Negative Response Code drawn from the NRC pool '
        'instead of the normal positive response.',
        '<b>Drop</b> -- Silently discard the request; the host times out waiting.',
        '<b>CorruptByte</b> -- Send the normal response with one byte flipped at random.',
        '<b>Random</b> -- Choose one of the above actions randomly each time the fault fires.',
    ]:
        story.append(bullet(item))
    story.append(body(
        'The <b>NRC Pool</b> checkboxes select which NRC codes the EmitNrc action draws from: '
        '$11 (Service Not Supported), $12 (Sub-Function Not Supported), '
        '$22 (Conditions Not Correct), $31 (Request Out of Range).'
    ))
    info_st = ParagraphStyle('info', parent=ST['body'],
                             fontSize=10, leading=14,
                             backColor=HexColor('#eaf4fb'),
                             borderColor=HexColor('#2e86c1'),
                             borderWidth=1, borderPadding=8,
                             spaceAfter=10)
    story.append(Paragraph(
        '<b>Note — not yet active:</b> The glitch injection UI is fully built and all '
        'settings are saved to the config file, but the feature currently has no effect '
        'at runtime. The bus logic does not yet consult the glitch configuration. '
        'Full implementation is slated for a future release.',
        info_st))
    story.append(PageBreak())

    # ---- 8. J2534 Menu ----
    story.append(h1('8. J2534 Menu'))
    story.append(body(
        'The J2534 menu manages the simulator\'s registration as a PassThru device. '
        'Register and Unregister require administrator rights; a UAC elevation prompt '
        'appears automatically.'
    ))
    for fl in shot('06_j2534_menu.png',
                   'Figure 5 -- J2534 menu with registration management commands.'):
        story.append(fl)
    for item in [
        '<b>Register as J2534 device...</b> -- Writes registry entries under '
        'HKLM\\SOFTWARE\\PassThruSupport.04.04\\GmEcuSim (64-bit) and WOW6432Node (32-bit), '
        'each pointing at the matching PassThruShim DLL. '
        'Re-running Register over an existing entry automatically cleans up legacy layouts.',
        '<b>Unregister</b> -- Removes the registry entries. The simulator continues to '
        'accept pipe connections from direct clients; only J2534 host enumeration is affected.',
        '<b>Show registered devices...</b> -- Opens a read-only modal listing every J2534 '
        'device found on this machine across both registry views, with DLL existence checks. '
        'Run this before and after Register / Unregister to verify what changed.',
    ]:
        story.append(bullet(item))
    for fl in shot('07_registered_devices_dialog.png',
                   'Figure 6 -- Show Registered Devices dialog listing all J2534 devices '
                   'found in the Windows registry with DLL path and existence status.'):
        story.append(fl)
    story.append(PageBreak())

    # ---- 9. File Menu ----
    story.append(h1('9. File Menu'))
    story.append(body(
        'The File menu manages ECU configuration files. The default configuration is '
        'ecu_config.json in the application directory and is loaded automatically on startup.'
    ))
    for fl in shot('05_file_menu.png',
                   'Figure 7 -- File menu showing New, Open, Save, Save As, Import, Export, and Exit.'):
        story.append(fl)
    for item in [
        '<b>New</b>  (Ctrl+N) -- Creates a blank configuration with a single default ECU.',
        '<b>Open...</b>  (Ctrl+O) -- Loads a configuration from a JSON file.',
        '<b>Save</b>  (Ctrl+S) -- Saves to the current file, or prompts for a path if unsaved.',
        '<b>Save As...</b>  (Ctrl+Shift+S) -- Saves to a new file path.',
        '<b>Import...</b> -- Imports ECU definitions from an external format.',
        '<b>Export...</b> -- Exports the configuration to a distributable format.',
        '<b>Exit</b> -- Closes the application; the named pipe server shuts down gracefully.',
    ]:
        story.append(bullet(item))
    story.append(PageBreak())

    # ---- 10. Technical Reference ----
    story.append(h1('10. Technical Reference'))

    story.append(h2('10.1 CAN ID Conventions'))
    story.append(body(
        'The simulator uses the OBD-II GM GMLAN CAN ID scheme matching real hardware. '
        'The GMW3110 specification uses pedagogical IDs ($241/$641) in its worked '
        'examples; those IDs are used only in unit-test fixtures and do not appear in '
        'the default configuration.'
    ))
    can_rows = [
        ['ECU',  'Request ID', 'Response ID', 'Functional ID'],
        ['ECM',  '0x7E0',      '0x7E8',       '0x5E8'],
        ['TCM',  '0x7E1',      '0x7E9',       '0x5E9'],
    ]
    can_t = Table(can_rows, colWidths=[3*cm, 3.5*cm, 3.5*cm, 3.5*cm])
    can_t.setStyle(TableStyle([
        ('BACKGROUND',    (0, 0), (-1,  0), BLUE),
        ('TEXTCOLOR',     (0, 0), (-1,  0), white),
        ('FONTNAME',      (0, 0), (-1,  0), 'Helvetica-Bold'),
        ('FONTSIZE',      (0, 0), (-1, -1), 10),
        ('ROWBACKGROUNDS',(0, 1), (-1, -1), [white, LIGHT_GRAY]),
        ('GRID',          (0, 0), (-1, -1), 0.4, GRAY),
        ('ALIGN',         (1, 0), (-1, -1), 'CENTER'),
        ('VALIGN',        (0, 0), (-1, -1), 'MIDDLE'),
        ('TOPPADDING',    (0, 0), (-1, -1), 6),
        ('BOTTOMPADDING', (0, 0), (-1, -1), 6),
    ]))
    story.append(can_t)
    story.append(Spacer(1, 0.4*cm))

    story.append(h2('10.2 J2534 Conformance'))
    story.append(body(
        'PassThruShim exports exactly the 14 functions defined by SAE J2534-1 v04.04: '
        'PassThruOpen, PassThruClose, PassThruConnect, PassThruDisconnect, '
        'PassThruReadMsgs, PassThruWriteMsgs, PassThruStartPeriodicMsg, '
        'PassThruStopPeriodicMsg, PassThruStartMsgFilter, PassThruStopMsgFilter, '
        'PassThruSetProgrammingVoltage, PassThruReadVersion, PassThruGetLastError, '
        'and PassThruIoctl. PassThruReadVersion reports firmware version 04.04. '
        'The v05.00 functions (ScanForDevices, GetNextDevice, LogicalConnect, etc.) '
        'and the Drew Tech proprietary PassThruGetNextCarDAQ are deliberately not exported.'
    ))

    story.append(h2('10.3 Named Pipe Protocol'))
    story.append(body(
        'The shim communicates with the simulator over the named pipe '
        '\\\\.\\pipe\\GmEcuSim.PassThru using length-prefixed binary frames. '
        'Each frame starts with a 4-byte little-endian length followed by a message-type '
        'byte and the marshalled parameters. The RequestDispatcher maps each message type '
        'to the corresponding VirtualBus operation and enqueues the response.'
    ))

    story.append(h2('10.4 ISO-TP Transport'))
    story.append(body(
        'Inbound CAN frames are processed by the ISO-TP reassembler, which handles '
        'Single Frame (SF), First Frame + Consecutive Frame (FF/CF), and Flow Control (FC) '
        'types per ISO 15765-2. Assembled USDT payloads are dispatched to the appropriate '
        'GMW3110 service handler by service ID (first payload byte).'
    ))

    story.append(h2('10.5 Tester Present and Session Timeout'))
    story.append(body(
        'Each ECU tracks its own P3C (Tester Present) timer. If no $3E request arrives '
        'within 5 seconds (P3Cnom), the ECU runs EcuExitLogic: the DPID scheduler is '
        'cleared, $2D-defined dynamic PIDs are pruned, and an '
        'unsolicited $60 response is emitted. The same exit logic fires on graceful '
        '$20 Return-to-Normal and on host-vanish detection (IdleBusSupervisor monitors '
        'inactivity on the VirtualBus).'
    ))

    return story


def main():
    doc = ManualDoc(
        OUT_PATH,
        pagesize=A4,
        leftMargin=MARGIN, rightMargin=MARGIN,
        topMargin=MARGIN + 1.8*cm, bottomMargin=MARGIN + 0.8*cm,
        title='GM ECU Simulator User Manual',
        author='GM ECU Simulator',
        subject='J2534 v04.04 PassThru Device Emulator for GMLAN / GMW3110',
    )
    story = build_story()
    doc.build(story)
    print('PDF written to: ' + OUT_PATH)


if __name__ == '__main__':
    main()


