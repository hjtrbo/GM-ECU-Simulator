using Common.Protocol;
using Core.Bus;
using Core.Scheduler;
using Core.Services;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Core.Ecu.Personas;

// Ford UDS persona: a write-everything-down-and-reject-everything-politely
// dispatcher used to elicit PCMTec's request stream when no real Ford ECU is
// available. Every inbound USDT message is appended to a per-session log file
// under %LOCALAPPDATA%\GmEcuSimulator\logs\ford-uds\ with full byte hex,
// a best-effort service-name annotation, and the addressing mode (physical /
// functional). Replies are always NRC $11 ServiceNotSupported on physical
// requests; functional broadcasts stay silent (spec).
//
// Why singleton: matches Gmw3110Persona / UdsKernelPersona. The per-session
// log file is owned by static state behind a lock - one process loads the
// persona once, gets one file per host-connect cycle. The HostConnected /
// HostDisconnected events on VirtualBus drive the file rotate so a Reset
// session lands as its own file.
//
// Usage: load the Ford UDS preset config (see
// `ecu_simulator_config_ford_uds.mode.json` at the repo root) which sets
// `PersonaId = "ford-uds"` on the single ECU at $7E0/$7E8. Register the
// 32-bit PassThruShim (PCMTec is 32-bit), start GmEcuSimulator, connect from
// PCMTec. Every request lands in the log file as a new line.
//
// Iterate by hand-extending the (sid, prefix) -> response table below as we
// observe PCMTec's request stream in the log; every other request still hits
// the default NRC path so we keep observing.
public sealed class FordUdsPersona : IDiagnosticPersona
{
    public static readonly FordUdsPersona Instance = new();
    private FordUdsPersona() { }

    public string Id => "ford-uds";
    public string DisplayName => "Ford UDS (PCMTec)";

    // ---- Iteration knobs: Mode 09 identity served from the loaded bin ----
    //
    // Phase 2 (observed 2026-05-23): PCMTec's first action after PassThruOpen
    // + ISO-15765 PassThruConnect is to send Service $09 (Request Vehicle
    // Information). It retries $09 $02 (VIN) for ~40 seconds, then sends one
    // $09 $04 (CalID), then walks to the next ECU. Without positive replies
    // it gives up before any strategy-specific traffic appears, so we answer
    // both - and PCMTec moves on to the next request stage, which we log.
    //
    // VIN and CalID are read straight out of the loaded flash bin (set via
    // LoadFlashBin / ecu.flashBinPath) rather than hard-coded, so they always
    // agree with what PCMTec independently cross-checks via $23
    // ReadMemoryByAddress - it reads the VIN at 0x000100C0 directly, so a
    // canned Mode 09 VIN could disagree with the $23 read of the same bytes.
    // Confirmed against HAEE4UY.bin (2026-06-06):
    //   VIN     17 ASCII bytes at 0x000100C0  ("6FPAAAJGCMAY96000")
    //   CalID   ASCII run at 0x00010046, '.'-terminated ("HAEE4UY.HEX" -> "HAEE4UY")
    // If the bin is unloaded or the window is unreadable we fall back to the
    // known-good FG strings so the capture flow still progresses.
    private const int VinBinOffset = 0x000100C0;
    private const int VinLength = 17;
    private const int CalIdBinOffset = 0x00010046;
    private const int CalIdMaxLength = 16;
    private const string VinFallback = "6FPAAAJGCMAY96000";
    private const string CalIdFallback = "HAEE4UY";

    // ---- Service $23 ReadMemoryByAddress: read from a loaded flash bin ----
    //
    // PCMTec's observed wire format (2026-05-23 capture, request bytes
    //   23 00 01 00 C0 00 04 - 7 bytes total):
    //   byte 0    = SID 0x23
    //   bytes 1-4 = 4-byte big-endian memory address  (0x000100C0)
    //   bytes 5-6 = 2-byte big-endian length          (0x0004)
    //
    // NO ALFI byte (the first iteration of this parser assumed the
    // ISO-14229 Address-and-Length Format Identifier byte was present;
    // PCMTec doesn't send one, and the 7-byte request fell through to
    // NRC $11 fallthrough on the first PCMTec re-run. Wire format is
    // strict per the capture - 7 bytes exactly).
    //
    // PCMTec uses this to cross-check the VIN at flash 0x000100C0 against
    // what it just got via Mode 09 PID 02 - and almost certainly to read
    // other identifier blocks (strategy name, build date, etc.) further into
    // the bin. Without a real flash image to back the read, we'd NRC every
    // one of these and PCMTec would never proceed to the strategy-specific
    // path. So we load the user's HAEE4UY bin into memory and serve reads
    // from it directly. Set the bytes via LoadFlashBin at startup; the
    // backing array lives behind a Volatile read so a config reload that
    // re-points it is picked up atomically.
    private static volatile byte[]? flashBin;

    /// <summary>Load (or replace) the flash backing for $23 reads. Pass null
    /// to clear and force $23 NRCs. The same bytes back every ECU using the
    /// ford-uds persona - the persona itself is a singleton.</summary>
    public static void LoadFlashBin(byte[]? bytes) => flashBin = bytes;

    /// <summary>Load the flash backing from a file path. Throws on missing /
    /// unreadable file so config-load failures are loud rather than silent.</summary>
    public static void LoadFlashBin(string path)
    {
        var bytes = File.ReadAllBytes(path);
        LoadFlashBin(bytes);
    }

    /// <summary>Current flash size in bytes, 0 if unloaded. Read by the WPF
    /// status bar / diagnostic UI.</summary>
    public static int FlashBinSize => flashBin?.Length ?? 0;

    // ---- $B1/$34/$36/$37 flash-write capture ----
    //
    // The Ford flash tool (Spanish Oak / PCMTec) programs the PCM with:
    //   B1 00 B2 AA            erase            -> F1
    //   34 00 01 00 00 00 0F.. RequestDownload  -> 74   (sent twice)
    //   36 <1024 raw bytes>    TransferData     -> 76   (no block counter / no address)
    //   37                     TransferExit     -> 77
    // writeFlash() streams sequential 0x400-byte chunks for flash i = 0x10000 ..
    // 0xFFC00, so the $36 payloads land contiguously starting at flash 0x10000 and
    // block 0 (0x0..0xFFFF) is never written. We accept the whole sequence (the
    // tool only checks the response SID byte), accumulate the $36 stream into the
    // node's DownloadBuffer, and on $37 flush a full image to a .bin: the loaded
    // flash bin's block 0 preserved, the captured region overlaid at 0x10000.
    private const int FordWritableRegionBase = 0x10000;   // writeFlash() start address
    private const int FordWriteBufferSize    = 0x100000;  // 1 MiB sink, covers the writable region

    private static readonly Lock FlashWriteLock = new();
    private static string? lastFlashWritePath;

    /// <summary>Absolute path of the most recent captured flash-write image, or
    /// null if no $34/$36/$37 download has completed this process.</summary>
    public static string? LastFlashWritePath { get { lock (FlashWriteLock) return lastFlashWritePath; } }

    // ---- Service $A1 DMR-setup mapping capture ----
    //
    // PCMTec's wire format (observed 2026-05-23 16:10):
    //   A1 <slot 0x01-0x09> 8C <4B BE RAM addr>      - 7 bytes
    // 0x8C is constant across every $A1 we've seen (probably a "read mode"
    // flag). The 4-byte trailing word is the RAM target. PCMTec uses this
    // to tell the ECU "when I later poll slot N, sample from RAM at X".
    //
    // The cookbook's whole project goal is to recover the firmware-side
    // wire_id -> RAM-address mapping. PCMTec sending $A1 *IS* that mapping
    // - in PCMTec's own choice of slot ids. Persist every $A1 to a CSV so
    // we accumulate the table across sessions; the user picks which MIDs
    // to log in PCMTec's UI and each new MID lands as a new row.

    /// <summary>slot id (1 byte) -> RAM address last bound by $A1.</summary>
    private static readonly ConcurrentDictionary<byte, uint> dmrSlots = new();

    /// <summary>Read-only snapshot of the currently-bound slot map.</summary>
    public static IReadOnlyDictionary<byte, uint> DmrSlotMap => dmrSlots;

    /// <summary>Tear down the captured map (test fixtures, session reset).</summary>
    public static void ResetDmrSlotMap() => dmrSlots.Clear();

    // Per-process append-only CSV. Lazily created on first $A1; survives the
    // lifetime of the GmEcuSimulator.exe process so cycles of connect /
    // disconnect / reconnect accumulate rows. Multiple PCMTec channel
    // sessions hitting the same slot just append duplicate rows tagged with
    // their bus-clock timestamp - useful when tracking which slots PCMTec
    // re-binds at run-start vs. mid-session.
    private const string CapturedCsvFileName = "firmware-dmr-table-captured.csv";
    private static readonly Lock CapturedCsvLock = new();
    private static bool csvHeaderWritten;
    private static string? capturedCsvPath;

    /// <summary>Absolute path of the captured-mappings CSV, null if not opened yet.</summary>
    public static string? CapturedCsvPath { get { lock (CapturedCsvLock) return capturedCsvPath; } }

    private static void AppendCapturedMapping(double nowMs, byte slot, byte modeByte, uint addr)
    {
        // One row per $A1: timestamp, slot, mode flag, RAM addr, plus a
        // pre-computed bin-region tag so a downstream grep can quickly
        // separate RAM-bank-1 (0x3F0000+) from RAM-bank-2 (0x400000+).
        string region = addr switch
        {
            >= 0x003F0000 and < 0x00400000 => "ram_3F",
            >= 0x00400000 and < 0x00410000 => "ram_40",
            _ => "other",
        };
        string row = string.Format(CultureInfo.InvariantCulture,
            "{0:F2},0x{1:X2},0x{2:X2},0x{3:X8},{4}\n",
            nowMs, slot, modeByte, addr, region);
        try
        {
            lock (CapturedCsvLock)
            {
                if (!csvHeaderWritten || capturedCsvPath == null)
                {
                    string dir = ResolveLogDirectory();
                    Directory.CreateDirectory(dir);
                    capturedCsvPath = Path.Combine(dir, CapturedCsvFileName);
                    // Truncate at process start so each GmEcuSimulator launch
                    // gets a clean file. Set FileMode.Append instead if you
                    // want the file to accumulate across launches; for now
                    // a fresh-per-launch file is easier to reason about.
                    if (!File.Exists(capturedCsvPath))
                    {
                        File.WriteAllText(capturedCsvPath,
                            "bus_ms,slot_hex,mode_hex,ram_addr_hex,region\n");
                    }
                    csvHeaderWritten = true;
                }
                File.AppendAllText(capturedCsvPath, row);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ford-uds] csv append error: {ex.Message}");
        }
    }

    // ---- Service $A0 UUDT broadcast loop ----
    //
    // PCMTec installs a PASS_FILTER for CAN ID 0x6A0/0x6A1 on a different
    // J2534 channel from the diag channel right after sending the first $A0,
    // then sits and waits for data. Without an actual stream on that ID,
    // PCMTec ~3s later sends $3E TesterPresent, then ~3s later still it
    // tears down the channel and reconnects from scratch.
    //
    // Phase 6 spawns a single 100ms-period TimerOnDelay (AutoRestart) on
    // first $A0. Each tick round-robins through the currently-bound slots
    // in dmrSlots, builds a raw CAN frame of the form
    //   <CAN ID 0x6A0 (4B)> <slot (1B)> <4B value> <3B padding>
    // and shoves it at every channel via bus.Broadcaster. Each channel's
    // own filter table decides delivery, so the diag channel ignores it
    // (its FlowControl filter is on 0x7E8) while PCMTec's UUDT listener
    // channel (PASS_FILTER on 0x6A0) takes it.
    //
    // The data payload is currently four zero bytes - no real ECU here to
    // sample RAM from. If PCMTec accepts that, we progress; if it rejects
    // ("Sequence counter mismatch" or similar), the response shape needs
    // refinement and we know what to look for.
    // PCMTec filters BOTH 0x6A0 AND 0x6A4 (mask FFFFFFFE on each so 0x6A1/
    // 0x6A5 also pass). The dual filter strongly suggests Ford's "stream A /
    // stream B" convention - parallel UUDT streams the broadcast tester
    // reads as a paired feed. Each tick we emit on one ID then the other so
    // both PCMTec filters see traffic.
    private static readonly ushort[] UudtBroadcastCanIds = new ushort[] { 0x6A0, 0x6A4 };
    private static int broadcastCanIdIdx;
    private static readonly Lock BroadcastLock = new();
    private static Core.Utilities.TimerOnDelay? broadcastTimer;
    private static VirtualBus? broadcastBus;
    private static int broadcastSlotIdx;
    private static byte[]? broadcastSlotOrder;

    private static void EnsureBroadcastStarted(VirtualBus? bus)
    {
        if (bus == null) return;
        lock (BroadcastLock)
        {
            // Re-subscribe HostDisconnected on every $A0 - cheap, and survives
            // a previous session's StopBroadcast wiping the subscription. The
            // -= before += is the idempotency belt-and-braces (delegate equality
            // makes a duplicate += harmless but tidy).
            bus.HostDisconnected -= StopBroadcast;
            bus.HostDisconnected += StopBroadcast;

            broadcastBus = bus;
            // Snapshot the slot order at every $A0 - PCMTec may rebind
            // slots between sessions and we want the broadcast loop to
            // track the current map.
            broadcastSlotOrder = dmrSlots.Keys.OrderBy(k => k).ToArray();
            broadcastSlotIdx = 0;
            if (broadcastTimer != null) return;

            broadcastTimer = new Core.Utilities.TimerOnDelay
            {
                Preset = 100,
                AutoRestart = true,
            };
            broadcastTimer.OnTimingDone += OnBroadcastTick;
            broadcastTimer.Start();
        }
    }

    /// <summary>Tear down the broadcast loop. Called on host-disconnect via
    /// VirtualBus.HostDisconnected (wired in the WPF composition root) and
    /// from unit-test fixtures cleaning up between cases.</summary>
    public static void StopBroadcast()
    {
        lock (BroadcastLock)
        {
            if (broadcastTimer == null) return;
            broadcastTimer.Stop();
            broadcastTimer.OnTimingDone -= OnBroadcastTick;
            broadcastTimer = null;
            broadcastBus = null;
            broadcastSlotOrder = null;
            broadcastSlotIdx = 0;
            broadcastCanIdIdx = 0;
        }
    }

    private static void OnBroadcastTick(object? _, Core.Utilities.TimerDoneEventArgs __)
    {
        IFrameBroadcaster? broadcaster;
        byte[]? order;
        int slotIdx;
        ushort canId;
        lock (BroadcastLock)
        {
            if (broadcastBus == null) return;
            broadcaster = broadcastBus.Broadcaster;
            order = broadcastSlotOrder;
            if (order == null || order.Length == 0) return;
            slotIdx = broadcastSlotIdx;
            broadcastSlotIdx = (slotIdx + 1) % order.Length;
            canId = UudtBroadcastCanIds[broadcastCanIdIdx];
            broadcastCanIdIdx = (broadcastCanIdIdx + 1) % UudtBroadcastCanIds.Length;
        }
        if (broadcaster == null) return;

        byte slot = order[slotIdx];
        // 4-byte CAN ID prefix + 8 data bytes = 12-byte frame, matching the
        // existing UUDT format DpidScheduler uses for GM. Data: slot + 4
        // zeros + 3-byte pad. The 4 "value" bytes are zero pending PCMTec
        // feedback on the expected payload shape.
        var frame = new byte[12];
        frame[2] = (byte)((canId >> 8) & 0xFF);
        frame[3] = (byte)(canId & 0xFF);
        frame[4] = slot;
        // frame[5..11] already zero
        broadcaster.BroadcastFrame(frame);

        // Phantom Ford engine-bus broadcast: PCMTec installs a filter for
        // 0x97 (`PCM_Pmes_PCM` / engine-bus heartbeat carrying RPM and
        // friends) on a real vehicle. MID27506 (ENGINE_SPEED) is marked
        // "Broadcast" by PCMTec - it expects RPM here, NOT via $A1. Without
        // any 0x97 traffic the logger may decide the engine is dead and
        // exit. Emit a synthetic frame each tick alongside the DMR stream.
        //
        // Bytes: bytes 0-1 = 16-bit BE RPM*4 = 800 RPM idle = 3200 = 0x0C80.
        // (Common Ford encoding. If the exact format is wrong PCMTec
        // probably still accepts ANY 0x97 traffic as "bus alive" since the
        // engine-running flag is set elsewhere.)
        var rpmFrame = new byte[12];
        rpmFrame[2] = 0x00; // CAN ID high
        rpmFrame[3] = 0x97; // CAN ID low
        rpmFrame[4] = 0x0C; // RPM high (800 rpm * 4 = 3200 = 0x0C80)
        rpmFrame[5] = 0x80; // RPM low
        // rpmFrame[6..11] zero - other engine-bus fields (load, gear, etc.)
        broadcaster.BroadcastFrame(rpmFrame);
    }

    // Mode 09 PID 02 - VIN. SAE J1979 reply: 49 02 01 <17 VIN bytes>. Reads
    // the 17-byte VIN from the bin at 0x000100C0; falls back to VinFallback
    // when the bin is absent or the window isn't 17 printable-ASCII bytes.
    private static byte[] BuildMode09Pid02Reply()
    {
        string vin = ReadAsciiFromBin(VinBinOffset, VinLength, stopAtDot: false);
        if (vin.Length != VinLength) vin = VinFallback;
        // 49 (positive-response SID) + 02 (PID echo) + 01 (NODI) + 17 VIN bytes
        var reply = new byte[3 + 17];
        reply[0] = 0x49;
        reply[1] = 0x02;
        reply[2] = 0x01;
        System.Text.Encoding.ASCII.GetBytes(vin, 0, 17, reply, 3);
        return reply;
    }

    // Mode 09 PID 04 - Calibration ID. 49 04 01 <16 ASCII bytes, zero-padded>.
    // Reads the '.'-terminated strategy string from the bin at 0x00010046
    // ("HAEE4UY.HEX" -> "HAEE4UY"); falls back to CalIdFallback when absent.
    private static byte[] BuildMode09Pid04Reply()
    {
        string calId = ReadAsciiFromBin(CalIdBinOffset, CalIdMaxLength, stopAtDot: true);
        if (calId.Length == 0) calId = CalIdFallback;
        // 49 + 04 + 01 (NODI) + 16 ASCII bytes (zero-padded)
        var reply = new byte[3 + 16];
        reply[0] = 0x49;
        reply[1] = 0x04;
        reply[2] = 0x01;
        int n = Math.Min(calId.Length, 16);
        System.Text.Encoding.ASCII.GetBytes(calId, 0, n, reply, 3);
        // bytes 3+n .. 18 already zero
        return reply;
    }

    // Pull an ASCII string out of the loaded flash bin. Returns "" if no bin
    // is loaded or the window doesn't fit, so callers fall back to a literal.
    // stopAtDot truncates the run at the first '.', NUL, 0xFF (erased flash)
    // or non-printable byte (the bin stores the strategy as "HAEE4UY.HEX" but
    // PCMTec wants just "HAEE4UY"); otherwise the full fixed-length window is
    // returned only if every byte is printable ASCII, else "".
    private static string ReadAsciiFromBin(int offset, int maxLen, bool stopAtDot)
    {
        byte[]? bin = flashBin;
        if (bin == null || offset < 0 || offset + maxLen > bin.Length) return "";
        if (stopAtDot)
        {
            int len = 0;
            while (len < maxLen)
            {
                byte c = bin[offset + len];
                if (c == (byte)'.' || c == 0x00 || c == 0xFF || c < 0x20 || c > 0x7E) break;
                len++;
            }
            return System.Text.Encoding.ASCII.GetString(bin, offset, len);
        }
        // Fixed-length window: reject unless every byte is printable ASCII.
        for (int i = 0; i < maxLen; i++)
        {
            byte c = bin[offset + i];
            if (c < 0x20 || c > 0x7E) return "";
        }
        return System.Text.Encoding.ASCII.GetString(bin, offset, maxLen);
    }

    // One log file per host-connect cycle, lazily opened on first message.
    // Sync object covers the {writer, currentPath} pair against concurrent
    // dispatch from the IPC pipe thread + UI thread.
    private static readonly Lock LogLock = new();
    private static StreamWriter? logWriter;
    private static string? currentPath;

    /// <summary>
    /// Hook for VirtualBus lifecycle events: opens a fresh log file. Safe to
    /// call repeatedly; reopens the writer on each call so a Disconnect /
    /// Connect cycle produces a clean file per session. Returns the path that
    /// will receive log lines (so the caller can surface it in the UI).
    /// </summary>
    public static string BeginSession()
    {
        lock (LogLock)
        {
            CloseWriterUnlocked();
            string dir = ResolveLogDirectory();
            Directory.CreateDirectory(dir);
            string filename = $"pcmtec_capture_{Bitness}_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            currentPath = Path.Combine(dir, filename);
            logWriter = new StreamWriter(new FileStream(
                currentPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
            logWriter.WriteLine($"# Ford UDS log opened {DateTime.Now:O} (shim bitness {Bitness})");
            logWriter.WriteLine($"# Every line: <utcMs>  <dir>  <chan>  <addr>  <stack>  <sid_hex sid_name>  <payload_hex>  <annotation>");
            return currentPath;
        }
    }

    /// <summary>Closes the per-session log file. Safe to call repeatedly.</summary>
    public static void EndSession()
    {
        lock (LogLock) CloseWriterUnlocked();
    }

    private static void CloseWriterUnlocked()
    {
        if (logWriter == null) return;
        try
        {
            logWriter.WriteLine($"# Ford UDS log closed {DateTime.Now:O}");
            logWriter.Dispose();
        }
        catch { /* idempotent close */ }
        finally
        {
            logWriter = null;
            currentPath = null;
        }
    }

    /// <summary>Currently active log file path, or null if no session is open.</summary>
    public static string? CurrentLogPath { get { lock (LogLock) return currentPath; } }

    public bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                        bool isFunctional, byte sid, double nowMs, DpidScheduler scheduler,
                        DiagnosticStack stack)
    {
        // Log the request. Best-effort - never let logging failure crash the
        // bus thread; the IPC pipe and other ECUs depend on this returning.
        try
        {
            WriteRequestLine(node, usdt, ch, isFunctional, sid, nowMs, stack);
        }
        catch (Exception ex)
        {
            // Best we can do - the bus already swallows handler exceptions
            // upstream, but persona dispatch shouldn't throw on logging errors
            // either. Re-raise into a non-fatal channel by writing to the
            // process console if available.
            Console.Error.WriteLine($"[ford-uds] log error: {ex.Message}");
        }

        // Spec-correct: functional broadcasts get no reply (every ECU on the
        // bus would otherwise step on each other).
        if (isFunctional) return true;

        // Canned-response whitelist: short-circuit specific (SID, sub-id)
        // shapes with a hard-coded positive reply. Anything else falls
        // through to the default NRC path so we keep observing PCMTec's
        // probe stream.
        if (sid == 0x09 && usdt.Length >= 2)
        {
            byte pid = usdt[1];
            if (pid == 0x02)
            {
                node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, BuildMode09Pid02Reply());
                return true;
            }
            if (pid == 0x04)
            {
                node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, BuildMode09Pid04Reply());
                return true;
            }
        }

        // Ford-proprietary $A1 SETUP_DMR. Wire format (verified against the
        // PCMTec Development Blog at https://pcmhacking.net/forums/viewtopic.php?t=4940 ):
        //   A1 <index 0x01..0x14> <magic_byte> <4B BE RAM addr>      - 7 bytes
        // Response: E1 <index>                                       - 2 bytes
        //
        // The blog notes (Phase 6 / fuzzing): up to 20 indices can be polled
        // simultaneously, the "magic byte" at offset 2 is a sub-function
        // that selects memory access mode (one of {0x89, 0x8A, 0x8B, 0x8C,
        // 0x91, 0x92, 0x93, 0x94, 0x99, 0x9A, 0x9B, 0xA1, 0xA2, 0xA9} -
        // each represents a different block / access width).
        //
        // We accept ANY magic byte (don't filter on 0x8C) - other strategies
        // may emit different modes. The response is JUST {E1, index};
        // sending the full 7-byte verbatim echo (our Phase 5 mistake) is
        // most likely what triggered PCMTec's NullReferenceException in
        // DataLoggerViewModel - PCMTec parsed the trailing bytes as a
        // structure it didn't expect.
        if (sid == 0xA1 && usdt.Length == 7)
        {
            byte index = usdt[1];
            byte modeByte = usdt[2];
            uint addr = (uint)((usdt[3] << 24) | (usdt[4] << 16) | (usdt[5] << 8) | usdt[6]);
            dmrSlots[index] = addr;
            AppendCapturedMapping(nowMs, index, modeByte, addr);
            node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                new byte[] { 0xE1, index });
            return true;
        }

        // Ford-proprietary $A0 DMR read. 2-byte request (`A0 <slot>`).
        // Phase 6 kicks off (or refreshes) the periodic UUDT broadcast on
        // 0x6A0 - PCMTec installs a PASS_FILTER for that ID on a separate
        // J2534 channel right after the first $A0 and waits for the stream.
        // The synchronous reply on the diag channel is still an echo so
        // PCMTec's USDT layer doesn't see a NRC/timeout.
        if (sid == 0xA0)
        {
            EnsureBroadcastStarted(ch.Bus);
            var reply = new byte[usdt.Length];
            reply[0] = 0xE0;
            for (int i = 1; i < usdt.Length; i++) reply[i] = usdt[i];
            node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, reply);
            return true;
        }

        // $3E TesterPresent ACK. PCMTec sends `3E 02` between $A0 polls -
        // we previously NRC'd which prompted PCMTec to disconnect/reconnect.
        // ISO-14229 convention: positive response is `7E <sub & 0x7F>` when
        // the high bit (suppress-positive) is clear; silent otherwise.
        if (sid == 0x3E)
        {
            byte sub = usdt.Length >= 2 ? usdt[1] : (byte)0;
            if ((sub & 0x80) == 0)
            {
                node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                    new byte[] { 0x7E, (byte)(sub & 0x7F) });
            }
            return true;
        }

        // $27 SecurityAccess. The ford-uds persona NRC-$11'd this until now,
        // which is exactly where a real Ford flash tool (PCMTec) stalls before
        // programming - see the bus log that motivated Ford-Flash-Support. Route
        // it to the shared Service27Handler so node.SecurityModule drives the
        // seed/key handshake. When the config left SecurityModuleId null, install
        // the Ford accept-any-key module on the node so the flash path unlocks
        // out of the box; an explicit config choice (e.g. a strict cipher) is
        // honoured untouched.
        if (sid == 0x27)
        {
            node.SecurityModule ??= Core.Security.SecurityModuleRegistry.Create("ford-uds-accept-any");
            Service27Handler.Handle(node, usdt, ch, (long)nowMs);
            return true;
        }

        // $11 ECUReset. The Ford flash tool sends `11 01` (hardReset) between its
        // level-2 and level-1 security unlocks - the "ignition cycle" step shown
        // in the bus log. We previously NRC-$11'd it; reply spec-correctly with
        // `51 <sub>` and clear programming + security state so the ECU behaves
        // like a freshly-reset PCM, then let the tool re-unlock. The high bit of
        // the subfunction is suppress-positive-response (ISO-14229), same as $3E.
        if (sid == 0x11)
        {
            byte sub = usdt.Length >= 2 ? usdt[1] : (byte)0;
            node.State.ClearProgrammingState();
            node.State.SecurityUnlockedLevel = 0;
            node.State.SecurityPendingSeedLevel = 0;
            node.State.SecurityLastIssuedSeed = null;
            node.State.SecurityFailedAttempts = 0;
            node.State.SecurityLockoutUntilMs = 0;
            if ((sub & 0x80) == 0)
            {
                node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                    new byte[] { 0x51, (byte)(sub & 0x7F) });
            }
            return true;
        }

        // $B1 Ford diagnosticCommand. The flash tool's erase is `B1 00 B2 AA`
        // (also `B1 80 12 00` reset-adaptations); the tool treats response SID
        // $F1 ($B1 + $40) as success. Echo the command bytes after $F1: a real
        // Ford PCM's $B1 response carries data, and a bare 1-byte $F1 is what
        // PCMTec's J2534 stack rejected with ERR_INVALID_MSG (every response that
        // worked through the unlock phase was >= 2 bytes). The tool only checks
        // the SID byte, so the echoed payload is cosmetic but keeps the message
        // a valid length. (A real erase first answers $7F B1 78 responsePending
        // for ~5 s then $F1; replying $F1 immediately is accepted.)
        if (sid == 0xB1)
        {
            var reply = new byte[usdt.Length];
            reply[0] = 0xF1;
            usdt.Slice(1).CopyTo(reply.AsSpan(1));
            // Deferred by the ECU's FlashEraseDelayMs (0 = immediate) to model a
            // real multi-second erase. No $7F B1 78 ResponsePending - PCMTec
            // aborts on a pending response to $B1; a real PCM just answers $F1
            // when the erase finishes.
            FlashTiming.EnqueueEraseResponse(node, ch, reply);
            return true;
        }

        // $34 RequestDownload. Wire form `34 00 01 00 00 00 0F 00 00` (addr
        // 0x010000, size 0x0F0000); the tool sends it twice and only checks for
        // response SID $74. We don't parse the non-standard address/size field -
        // the write region is known to start at 0x10000 (writeFlash loop) - we
        // just (re)start a capture buffer. The double $34 resets it twice, which
        // is harmless because no $36 has arrived yet.
        if (sid == 0x34)
        {
            node.State.DownloadBuffer = new byte[FordWriteBufferSize];
            node.State.DownloadBytesReceived = 0;
            node.State.DownloadDeclaredSize = FordWriteBufferSize;
            node.State.DownloadActive = true;
            // Standard UDS RequestDownload positive response: lengthFormatIdentifier
            // $20 (high nibble 2 = maxNumberOfBlockLength is 2 bytes) + $0400, the
            // 0x400-byte chunk size the tool uses. The tool only checks the $74 SID;
            // the trailing bytes keep the message a spec-valid length (see $B1 note).
            node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                new byte[] { 0x74, 0x20, 0x04, 0x00 });
            return true;
        }

        // $36 TransferData. Ford form is `36 <data>` with NO block-sequence
        // counter and NO address - each frame's 0x400 payload is the next
        // contiguous chunk from flash 0x10000 upward. Append to the capture
        // buffer at the running offset; reply $76.
        if (sid == 0x36)
        {
            var buf = node.State.DownloadBuffer;
            if (!node.State.DownloadActive || buf is null)
            {
                ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.ConditionsNotCorrectOrSequenceError);
                return true;
            }
            int dataLen = usdt.Length - 1;
            uint pos = node.State.DownloadBytesReceived;
            if (dataLen <= 0 || pos + (uint)dataLen > (uint)buf.Length)
            {
                ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.RequestOutOfRange);
                return true;
            }
            usdt.Slice(1, dataLen).CopyTo(buf.AsSpan((int)pos));
            node.State.DownloadBytesReceived = pos + (uint)dataLen;
            // $76 + a status byte. Ford $36 carries no block-sequence counter, so
            // there is none to echo; the second byte just keeps the response >= 2
            // bytes (the tool checks only the $76 SID). Paced by the ECU's
            // FlashTransferDelayMs (0 = immediate) via the shared FlashTiming.
            FlashTiming.EnqueueTransferResponse(node, ch, new byte[] { 0x76, 0x00 });
            return true;
        }

        // $37 RequestTransferExit. Reply $77 and flush the accumulated $36 stream
        // to a .bin under the ford-uds log dir (full image: loaded bin's
        // block 0 preserved, captured region overlaid at 0x10000).
        if (sid == 0x37)
        {
            if (node.State.DownloadActive && node.State.DownloadBuffer is not null)
            {
                string path = WriteFlashCapture(node.State.DownloadBuffer,
                                                (int)node.State.DownloadBytesReceived);
                ch.Bus?.LogSim?.Invoke(
                    $"[ford-flash] captured {node.State.DownloadBytesReceived} bytes from $36 stream -> {path}");
            }
            node.State.DownloadActive = false;
            node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, new byte[] { 0x77, 0x00 });
            return true;
        }

        if (sid == 0x23 && usdt.Length == 7)
        {
            // Ford ReadMemoryByAddress: 23 <4B BE addr> <2B BE len> - 7 bytes
            // total. See header comment for the wire-format observation.
            uint addr = (uint)((usdt[1] << 24) | (usdt[2] << 16) | (usdt[3] << 8) | usdt[4]);
            ushort len = (ushort)((usdt[5] << 8) | usdt[6]);
            byte[]? bin = flashBin;
            if (bin == null)
            {
                ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.ConditionsNotCorrectOrSequenceError);
                return true;
            }
            if (len == 0 || addr + (uint)len > (uint)bin.Length)
            {
                ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.RequestOutOfRange);
                return true;
            }
            // Positive response: 0x63 (= 0x23 | 0x40) followed by the
            // requested bytes. No echo of address/length - the host knows
            // what it asked for, and PCMTec's observed handler treats the
            // payload as just the raw bytes.
            var reply = new byte[1 + len];
            reply[0] = 0x63;
            Array.Copy(bin, (int)addr, reply, 1, len);
            node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, reply);
            return true;
        }

        ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.ServiceNotSupported);
        return true;
    }

    // ---- internal: flash-write capture ----

    /// <summary>
    /// Flush the accumulated $36 stream to a .bin under the ford-uds log dir
    /// and return its path. When a flash bin is loaded, the output is a full image
    /// (a clone of the loaded bin with the captured <paramref name="count"/> bytes
    /// overlaid at <see cref="FordWritableRegionBase"/>, so block 0 is preserved);
    /// otherwise just the captured region is written.
    /// </summary>
    private static string WriteFlashCapture(byte[] captured, int count)
    {
        if (count < 0) count = 0;
        if (count > captured.Length) count = captured.Length;

        byte[] image;
        byte[]? bin = flashBin;
        if (bin != null)
        {
            int end = FordWritableRegionBase + count;
            image = new byte[Math.Max(bin.Length, end)];
            Array.Copy(bin, image, bin.Length);
            Array.Copy(captured, 0, image, FordWritableRegionBase, count);
        }
        else
        {
            image = new byte[count];
            Array.Copy(captured, 0, image, 0, count);
        }

        string dir = ResolveLogDirectory();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"ford_flash_write_{DateTime.Now:yyyyMMdd_HHmmss}.bin");
        File.WriteAllBytes(path, image);
        lock (FlashWriteLock) lastFlashWritePath = path;
        return path;
    }

    // ---- internal: log formatting ----

    private static void WriteRequestLine(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                                         bool isFunctional, byte sid, double nowMs, DiagnosticStack stack)
    {
        // Pre-format on the calling thread so the lock around the writer is
        // as short as possible. nowMs is bus-clock ms since process start;
        // the wall clock is on the file header so a consumer can reconstruct
        // absolute times if it needs to.
        var sb = new StringBuilder(64 + usdt.Length * 3);
        sb.Append(nowMs.ToString("F2", CultureInfo.InvariantCulture));
        sb.Append("  Rx  chan");
        sb.Append(ch.Id);
        sb.Append("  ");
        sb.Append(isFunctional ? "FUNC" : "PHYS");
        sb.Append("  ");
        sb.Append(stack);
        sb.Append("  ");
        sb.Append("0x");
        sb.Append(sid.ToString("X2"));
        sb.Append(' ');
        sb.Append(ServiceName(sid).PadRight(28));
        sb.Append("  ");
        // Hex dump of the USDT payload (including the SID byte for self-contained
        // lines). Cap long payloads so the ~1 KiB $36 TransferData frames during a
        // flash write don't bloat the log or stall the bus thread formatting them.
        const int maxHexBytes = 24;
        int dumpLen = Math.Min(usdt.Length, maxHexBytes);
        for (int i = 0; i < dumpLen; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(usdt[i].ToString("X2"));
        }
        if (usdt.Length > maxHexBytes)
            sb.Append(CultureInfo.InvariantCulture, $" ... (+{usdt.Length - maxHexBytes} more, {usdt.Length} bytes total)");
        // Trailing annotation if we recognise the request shape.
        string? note = Annotate(sid, usdt);
        if (note != null) { sb.Append("  ; "); sb.Append(note); }

        lock (LogLock)
        {
            // Lazy-open if no session was started by a caller. This keeps the
            // persona usable in tests that drive Dispatch directly without
            // running through VirtualBus's HostConnected event.
            if (logWriter == null) BeginSessionUnlocked();
            logWriter!.WriteLine(sb.ToString());
        }
    }

    private static void BeginSessionUnlocked()
    {
        string dir = ResolveLogDirectory();
        Directory.CreateDirectory(dir);
        string filename = $"pcmtec_capture_{Bitness}_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        currentPath = Path.Combine(dir, filename);
        logWriter = new StreamWriter(new FileStream(
            currentPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        logWriter.WriteLine($"# Ford UDS log opened {DateTime.Now:O} (shim bitness {Bitness}, lazy-init)");
    }

    private static string ResolveLogDirectory()
    {
        // Sit next to the existing shim/bus log dirs under LOCALAPPDATA. The
        // shim's OpenLogFileIfNeeded uses the same parent; cohabiting keeps
        // a captured session as one tidy folder.
        string appdata = Environment.GetEnvironmentVariable("LOCALAPPDATA")
                         ?? Path.GetTempPath();
        return Path.Combine(appdata, "GmEcuSimulator", "logs", "ford-uds");
    }

    private static string Bitness => IntPtr.Size == 8 ? "64" : "32";

    // Mostly Ford-SCP / ISO-14229 service codes, plus the GMW3110 ones that
    // overlap. Names lifted from spec - the goal is human readability in the
    // log, not protocol negotiation. Unknown SIDs get "Unknown".
    private static string ServiceName(byte sid) => sid switch
    {
        0x01 => "Mode01_OBD2CurrentData",
        0x02 => "Mode02_OBD2FreezeFrame",
        0x03 => "Mode03_OBD2ReadDtc",
        0x04 => "Mode04_OBD2ClearDtc",
        0x05 => "Mode05_OBD2O2Sensor",
        0x06 => "Mode06_OBD2OnBoardMon",
        0x07 => "Mode07_OBD2PendingDtc",
        0x08 => "Mode08_OBD2Control",
        0x09 => "Mode09_OBD2VehicleInfo",
        0x0A => "Mode0A_OBD2PermDtc",
        0x10 => "DiagSessionControl",
        0x11 => "EcuReset",
        0x14 => "ClearDiagInfo",
        0x18 => "ReadDtcByStatus",
        0x19 => "ReadDtcInformation",
        0x1A => "ReadEcuId(GM/Ford)",
        0x20 => "ReturnToNormalMode",
        0x21 => "ReadDataByLocalId(Ford)",
        0x22 => "ReadDataByIdentifier",
        0x23 => "ReadMemoryByAddress",
        0x27 => "SecurityAccess",
        0x28 => "CommunicationControl",
        0x29 => "Authentication",
        0x2A => "ReadDataByPeriodicId",
        0x2C => "DynDefDataIdentifier",
        0x2D => "DefinePidByAddress",
        0x2E => "WriteDataByIdentifier",
        0x2F => "InputOutputControl",
        0x31 => "RoutineControl",
        0x34 => "RequestDownload",
        0x35 => "RequestUpload",
        0x36 => "TransferData",
        0x37 => "RequestTransferExit",
        0x38 => "RequestFileTransfer",
        0x3B => "WriteDataByLocalId",
        0x3D => "WriteMemoryByAddress",
        0x3E => "TesterPresent",
        0x85 => "ControlDTCSetting",
        0x86 => "ResponseOnEvent",
        0x87 => "LinkControl",
        0xA0 => "Ford_DmrRead?(A0)",
        0xA1 => "Ford_DmrSetup?(A1)",
        0xA2 => "ReportProgrammedState(GM)",
        0xA5 => "ProgrammingMode(GM)",
        0xA9 => "ReadDmrByDpid(Ford)",
        0xAA => "ReadDpidPeriodic(GM)",
        0xAB => "DynDmrDefinition(Ford?)",
        0xAE => "RequestDeviceControl",
        0xB1 => "ReadBlock(Ford)",
        0xB2 => "WriteBlock(Ford)",
        _    => $"Unknown",
    };

    // Surface a one-line interpretation for the most common requests so the
    // log is scannable without a hex-to-spec lookup each time. Conservative -
    // only annotate when the request shape is unambiguous.
    private static string? Annotate(byte sid, ReadOnlySpan<byte> usdt)
    {
        if (sid == 0x10 && usdt.Length >= 2)
            return $"session sub=0x{usdt[1]:X2}";
        if (sid == 0x3E && usdt.Length >= 2)
            return $"TesterPresent sub=0x{usdt[1]:X2} (suppressPosRsp={(usdt[1] & 0x80) != 0})";
        if (sid == 0x22 && usdt.Length >= 3)
            return $"DID=0x{usdt[1]:X2}{usdt[2]:X2}";
        if (sid == 0x1A && usdt.Length >= 2)
            return $"DID=0x{usdt[1]:X2}";
        if (sid == 0x27 && usdt.Length >= 2)
            return $"sub=0x{usdt[1]:X2}";
        if (sid == 0x21 && usdt.Length >= 2)
            return $"LID=0x{usdt[1]:X2}";
        if (sid == 0x23 && usdt.Length == 7)
        {
            uint addr = (uint)((usdt[1] << 24) | (usdt[2] << 16) | (usdt[3] << 8) | usdt[4]);
            ushort len = (ushort)((usdt[5] << 8) | usdt[6]);
            return $"addr=0x{addr:X8} len={len}";
        }
        if (sid == 0xA1 && usdt.Length == 7)
        {
            // Best-guess parse: A1 + 1B + 1B + 4B addr. Surfaces the RAM
            // target so the log is scannable.
            uint addr = (uint)((usdt[3] << 24) | (usdt[4] << 16) | (usdt[5] << 8) | usdt[6]);
            return $"sub=0x{usdt[1]:X2} id?=0x{usdt[2]:X2} addr=0x{addr:X8}";
        }
        if (sid == 0xA0 && usdt.Length == 2)
            return $"id?=0x{usdt[1]:X2}";
        return null;
    }
}
