namespace Common.Protocol;

// Per-frame friendly tag for the bus log. Stateless: each CAN frame is
// annotated in isolation - no ISO-TP reassembly, no session tracking, no
// memory of prior frames. CF and FC frames get a minimal label
// ("ISO-TP CF #1", "ISO-TP FC CTS BS=1 STmin=0"); SF and FF frames are
// decoded one level deeper into the USDT layer to surface the service name,
// the sub-function (for $10/$27/$A5/$AA), the NRC name (for $7F), or the
// declared download size ($34) and starting address ($36).
//
// Cheap: at most one switch + one allocation per recognised frame. The
// caller (VirtualBus.LogTx/LogRx/LogRxFiltered) is already on the hot
// path, so the annotator returns null when the frame isn't recognised
// rather than throwing; the caller just skips the suffix.
//
// Scope: generic UDS / ISO-14229 with GMW3110-2010 as a subset. Alongside
// the GM enhanced-diag SIDs it recognises plain UDS services ($11 ECUReset,
// $31 RoutineControl), SAE J1979 Mode 09, and Ford-PCM specifics ($23
// ReadMemoryByAddress, $B1 ReadBlock) - see Common.Protocol.Service.
public static class UdsAnnotator
{
    /// <summary>
    /// Lightweight classifier: true iff the frame is a TesterPresent
    /// request ($3E) or its positive response ($7E). Recognises both the
    /// ISO-TP Single-Frame form ($101 FE 01 3E / 7E0 01 3E) and the raw
    /// no-PCI functional broadcast some hosts emit ($101 FE 3E - the bare
    /// SID straight after the extended-addressing byte, no length nibble).
    /// FF/CF/FC and negative responses return false. Strips the GMLAN $101
    /// extended-addressing byte before inspecting. Used by VirtualBus to
    /// flag the frame so the UI log pane can drop it without affecting the
    /// file-log capture.
    /// </summary>
    public static bool IsTesterPresent(uint canId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return false;

        int offset = 0;
        bool functional = false;
        if (canId == GmlanCanId.AllNodesRequest)
        {
            byte ext = payload[0];
            if (ext != GmlanCanId.AllNodesExtAddr && ext != GmlanCanId.GatewayExtAddr)
                return false;
            offset = 1;
            functional = true;
        }

        if (payload.Length <= offset) return false;

        // Raw (no ISO-TP PCI) functional keepalive: e.g. $101 FE 3E, the
        // bare SID sitting directly after the extended-addressing byte with
        // no Single-Frame length nibble. $3E/$7E are not valid PCI types on
        // the functional channel (0x3n is Flow Control, never broadcast;
        // 0x7n is no PCI type at all), so a direct SID match is unambiguous.
        if (functional && IsTesterPresentSid(payload[offset]))
            return true;

        byte pci = payload[offset];
        if ((pci & 0xF0) != 0x00) return false;
        int len = pci & 0x0F;
        if (len < 1 || payload.Length < offset + 1 + len) return false;
        return IsTesterPresentSid(payload[offset + 1]);
    }

    private static bool IsTesterPresentSid(byte sid)
        => sid == Service.TesterPresent
        || sid == Service.TesterPresent + 0x40;

    /// <summary>
    /// Returns a short human-readable tag for the frame, or null if nothing
    /// useful can be said. Payload is the CAN data bytes only (no ID).
    /// </summary>
    public static string? Annotate(uint canId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return null;

        bool functional = canId == GmlanCanId.AllNodesRequest
                        || canId == GmlanCanId.Obd2FunctionalRequest;

        // GMLAN functional broadcast on $101 prepends an extended-addressing
        // byte ($FE all-nodes or $FD gateway) before the ISO-TP PCI. OBD-II
        // functional ($7DF) and all physical IDs use normal addressing.
        int offset = 0;
        if (canId == GmlanCanId.AllNodesRequest)
        {
            byte ext = payload[0];
            if (ext != GmlanCanId.AllNodesExtAddr && ext != GmlanCanId.GatewayExtAddr)
                return null;
            offset = 1;
        }

        if (payload.Length <= offset) return null;
        byte pci = payload[offset];
        byte pciType = (byte)(pci & 0xF0);
        string suffix = functional ? " - functional" : "";

        // Raw (no ISO-TP PCI) functional keepalive: $101 FE 3E - the bare SID
        // with no Single-Frame length nibble. Without this it would fall into
        // the 0x30 Flow-Control branch below and mislabel; $3E/$7E only ever
        // appear in this bare form as TesterPresent on the functional channel.
        if (functional && IsTesterPresentSid(pci))
        {
            var tag = AnnotateUsdt(payload.Slice(offset));
            return tag == null ? null : tag + suffix;
        }

        switch (pciType)
        {
            case 0x00:  // Single Frame
            {
                int len = pci & 0x0F;
                if (len == 0) return null;
                int start = offset + 1;
                if (payload.Length < start + len) return null;
                var usdt = payload.Slice(start, len);
                var tag = AnnotateUsdt(usdt);
                return tag == null ? null : tag + suffix;
            }
            case 0x10:  // First Frame
            {
                if (payload.Length < offset + 2) return null;
                int totalLen = ((pci & 0x0F) << 8) | payload[offset + 1];
                int start = offset + 2;
                if (payload.Length <= start) return $"ISO-TP FF ({totalLen}B)" + suffix;
                var usdt = payload.Slice(start);
                var tag = AnnotateUsdt(usdt);
                return tag == null
                    ? $"ISO-TP FF ({totalLen}B)" + suffix
                    : $"ISO-TP FF ({totalLen}B) - {tag}" + suffix;
            }
            case 0x20:  // Consecutive Frame
            {
                int seq = pci & 0x0F;
                return $"ISO-TP CF #{seq}";
            }
            case 0x30:  // Flow Control
            {
                int fs = pci & 0x0F;
                string fsName = fs switch
                {
                    0 => "CTS",
                    1 => "Wait",
                    2 => "Overflow",
                    _ => $"FS={fs}",
                };
                if (payload.Length < offset + 3) return $"ISO-TP FC {fsName}";
                byte bs = payload[offset + 1];
                byte stmin = payload[offset + 2];
                return $"ISO-TP FC {fsName} BS={bs} STmin={stmin}";
            }
            default:
                return null;
        }
    }

    private static string? AnnotateUsdt(ReadOnlySpan<byte> usdt)
    {
        if (usdt.Length == 0) return null;
        byte sid = usdt[0];

        if (sid == Service.NegativeResponse)
        {
            if (usdt.Length < 3) return "NegResp";
            string svc = ServiceName(usdt[1]) ?? $"${usdt[1]:X2}";
            return $"{svc}- {NrcName(usdt[2])}";
        }

        // Positive-response detection: sid - 0x40 must itself be a recognised
        // request SID. Naively trusting (sid >= 0x40) misfires for request SIDs
        // that already have the high bit set ($A2, $A5, $AA) - those would be
        // mistaken for responses to non-existent $62/$65/$6A services.
        byte requestSidIfPositive = (byte)(sid - 0x40);
        bool isPositive = sid >= 0x40 && ServiceName(requestSidIfPositive) != null;
        byte requestSid = isPositive ? requestSidIfPositive : sid;
        string? name = ServiceName(requestSid);
        if (name == null) return null;

        string head = isPositive ? name + "+" : name;

        // Service-specific detail. Negative-response path already returned above.
        switch (requestSid)
        {
            case Service.InitiateDiagnosticOperation:
                if (usdt.Length >= 2)
                {
                    string s = SessionSub(usdt[1]);
                    return $"{head} ({s})";
                }
                return head;
            case Service.SecurityAccess:
                if (usdt.Length >= 2) return $"{head} - {SecurityAccessSub(usdt[1], isPositive)}";
                return head;
            case Service.ProgrammingMode:
                if (usdt.Length >= 2 && !isPositive) return $"{head} - {ProgrammingModeSub(usdt[1])}";
                return head;
            case Service.ReadDataByPacketIdentifier:
                if (usdt.Length >= 2 && !isPositive) return $"{head} - {DpidRateSub(usdt[1])}";
                return head;
            case Service.ReportProgrammedState:
                if (isPositive && usdt.Length >= 2) return $"{head} {ProgrammedStateName(usdt[1])}";
                return head;
            case Service.RequestDownload:
                if (!isPositive && usdt.Length >= 4)
                {
                    uint size = 0;
                    for (int i = 2; i < usdt.Length; i++) size = (size << 8) | usdt[i];
                    return $"{head} size={size}";
                }
                return head;
            case Iso14229.Service.RoutineControl:
                // Request: [$31][sub][routineId hi][routineId lo][optionRecord...]
                // Positive (spec shape): [$71][sub][routineId hi][routineId lo][statusRecord...]
                // Positive (kernel CheckMemory shape): [$71][$04][crc_hi][crc_lo]
                //   - SPS programming kernels reply to $31 $01 $0401 with a
                //     fixed $04 opcode + 16-bit CRC, not the spec sub/id echo.
                //     Detect by the 4-byte length and the $04 second byte to
                //     avoid mistaking it for a spec response.
                if (isPositive && usdt.Length == 4 && usdt[1] == 0x04)
                {
                    ushort crc = (ushort)((usdt[2] << 8) | usdt[3]);
                    return $"{head} CheckMemoryByAddress (kernel) CRC=${crc:X4}";
                }
                if (usdt.Length >= 4)
                {
                    ushort routineId = (ushort)((usdt[2] << 8) | usdt[3]);
                    return $"{head} {RoutineControlSub(usdt[1])} {RoutineIdName(routineId)}";
                }
                return head;
            case Service.TransferData:
                if (!isPositive && usdt.Length >= 2)
                {
                    byte sf = usdt[1];
                    string sfName = sf switch
                    {
                        0x00 => "Download",
                        0x80 => "DownloadAndExecute",
                        _ => $"sub=${sf:X2}",
                    };
                    if (usdt.Length >= 6)
                    {
                        uint a = ((uint)usdt[2] << 24) | ((uint)usdt[3] << 16)
                               | ((uint)usdt[4] << 8) | usdt[5];
                        return $"{head} {sfName} @ {a:X8}";
                    }
                    return $"{head} {sfName}";
                }
                return head;
            case Service.RequestVehicleInformation:  // OBD-II Mode 09 RequestVehicleInformation (Ford PCM)
                // Request and the $49 positive response both carry the InfoType
                // in the same byte position (usdt[1]); the response adds a
                // NumberOfDataItems byte + the data, which the FF/SF hex already
                // shows, so surfacing just the InfoType keeps the tag scannable.
                if (usdt.Length >= 2) return $"{head} - {Mode09InfoType(usdt[1])}";
                return head;
            case Service.ReadMemoryByAddress:  // ReadMemoryByAddress (Ford UDS): 23 <4B BE addr> <2B BE len>
                if (!isPositive && usdt.Length == 7)
                {
                    uint addr = ((uint)usdt[1] << 24) | ((uint)usdt[2] << 16)
                              | ((uint)usdt[3] << 8) | usdt[4];
                    ushort len = (ushort)((usdt[5] << 8) | usdt[6]);
                    return $"{head} - addr=${addr:X8} len={len}";
                }
                // Positive $63: head + the raw bytes the read returned. These are
                // usually identifier ASCII (VIN / part-number fragments) so show
                // a quoted string when printable, else hex.
                if (isPositive && usdt.Length >= 2)
                    return $"{head} {RenderBytesAsText(usdt.Slice(1))}";
                return head;
            case Service.FordReadBlock:  // Ford ReadBlock / flash-erase command (e.g. B1 00 B2 AA)
                // Both the request and the $F1 positive response echo the same
                // command bytes; tag them with the trailing payload so an erase
                // (B1 00 B2 AA) is distinguishable from other $B1 sub-commands.
                if (usdt.Length >= 2) return $"{head} {HexBytes(usdt.Slice(1))}";
                return head;
            default:
                return head;
        }
    }

    // OBD-II Mode 09 (SAE J1979) InfoType names for the bus-log tag. Unknown
    // InfoTypes fall back to the raw hex so the line is still self-describing.
    private static string Mode09InfoType(byte b) => b switch
    {
        0x02 => "InfoType $02 (VIN)",
        0x04 => "InfoType $04 (CalID)",
        0x06 => "InfoType $06 (CVN)",
        0x0A => "InfoType $0A (ECUName)",
        _    => $"InfoType ${b:X2}",
    };

    // Render a byte span as a quoted ASCII string when every byte is printable,
    // otherwise as space-separated hex. Used for $23 positive responses, which
    // return raw flash bytes that are usually identifier ASCII but sometimes
    // binary (or erased $FF).
    private static string RenderBytesAsText(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return "(empty)";
        foreach (byte b in data)
            if (b < 0x20 || b > 0x7E) return HexBytes(data);
        Span<char> chars = stackalloc char[data.Length];
        for (int i = 0; i < data.Length; i++) chars[i] = (char)data[i];
        return $"\"{new string(chars)}\"";
    }

    private static string HexBytes(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    private static string? ServiceName(byte sid) => sid switch
    {
        Service.ClearDiagnosticInfo               => "ClearDiagnosticInfo",
        Service.InitiateDiagnosticOperation       => "StartDiagnosticSession",
        Service.ReadFailureRecordData             => "ReadFailureRecordData",
        Service.ReadDataByIdentifier              => "ReadDataByIdentifier",
        Service.ReturnToNormalMode                => "ReturnToNormalMode",
        Service.ReadDataByParameterIdentifier     => "ReadDataByPID",
        Service.SecurityAccess                    => "SecurityAccess",
        Service.DisableNormalCommunication        => "DisableNormalCommunication",
        Service.DynamicallyDefineMessage          => "DynamicallyDefineMessage",
        Service.DefinePidByAddress                => "DefinePIDByAddress",
        Service.RequestDownload                   => "RequestDownload",
        Service.TransferData                      => "TransferData",
        Service.WriteDataByIdentifier             => "WriteDataByIdentifier",
        Service.TesterPresent                     => "TesterPresent",
        Service.ReportProgrammedState             => "ReportProgrammedState",
        Service.ProgrammingMode                   => "ProgrammingMode",
        Service.ReadDataByPacketIdentifier        => "ReadDataByPacketID",
        // Foreign SIDs we still want to recognise in the bus log even though
        // GMW3110-2010 does not define them. Testers sometimes probe with UDS
        // services after a $36 DownloadAndExecute when the loaded kernel may
        // speak a different protocol; tagging them helps explain the NRC $11
        // the dispatcher sends back. The Ford-PCM block below is exercised by
        // the ford-uds persona (PCMTec): Mode 09 vehicle-info, $23
        // ReadMemoryByAddress (Ford's 23 <4B addr> <2B len> form, no ALFI),
        // and the $B1 ReadBlock/flash-erase command. See FordUdsPersona.
        Service.EcuReset                          => "ECUReset (UDS)",
        Iso14229.Service.RoutineControl           => "RoutineControl (UDS)",
        Service.RequestVehicleInformation         => "VehicleInfo (Mode09)",
        Service.ReadMemoryByAddress               => "ReadMemoryByAddress",
        Service.FordReadBlock                     => "ReadBlock (Ford)",
        _ => null,
    };

    private static string RoutineControlSub(byte b) => b switch
    {
        0x01 => "Start",
        0x02 => "Stop",
        0x03 => "Results",
        _    => $"sub=${b:X2}",
    };

    private static string RoutineIdName(ushort id) => id switch
    {
        0xFF00 => "EraseMemoryByAddress",
        0x0401 => "CheckMemoryByAddress",
        _      => $"id=${id:X4}",
    };

    private static string SessionSub(byte b) => b switch
    {
        0x01 => "Default",
        0x02 => "Programming",
        0x03 => "Extended",
        _    => $"sub=${b:X2}",
    };

    private static string SecurityAccessSub(byte b, bool isPositive) => b switch
    {
        0x01 => isPositive ? "Seed" : "RequestSeed",
        0x02 => isPositive ? "KeyAccepted" : "SendKey",
        _    => $"level=${b:X2}",
    };

    private static string ProgrammingModeSub(byte b) => b switch
    {
        0x01 => "Request",
        0x02 => "RequestHighSpeed",
        0x03 => "Enable",
        _    => $"sub=${b:X2}",
    };

    private static string DpidRateSub(byte b) => b switch
    {
        0x00 => "Stop",
        0x01 => "OneShot",
        0x02 => "Slow",
        0x03 => "Medium",
        0x04 => "Fast",
        _    => $"rate=${b:X2}",
    };

    private static string ProgrammedStateName(byte b) => b switch
    {
        0x00 => "FullyProgrammed",
        0x01 => "NoOpSwOrCal",
        0x02 => "OpSwPresentCalMissing",
        0x03 => "SwPresentDefaultCal",
        0x50 => "GeneralMemoryFault",
        0x51 => "RamFault",
        0x52 => "NvramFault",
        0x53 => "BootMemoryFault",
        0x54 => "FlashMemoryFault",
        0x55 => "EepromFault",
        _    => $"state=${b:X2}",
    };

    private static string NrcName(byte b) => b switch
    {
        Nrc.ServiceNotSupported                       => "ServiceNotSupported",
        Nrc.SubFunctionNotSupportedInvalidFormat      => "SubFunctionNotSupported/InvalidFormat",
        Nrc.ConditionsNotCorrectOrSequenceError       => "ConditionsNotCorrect/SequenceError",
        Nrc.RequestOutOfRange                         => "RequestOutOfRange",
        Nrc.SecurityAccessDenied                      => "SecurityAccessDenied",
        Nrc.InvalidKey                                => "InvalidKey",
        Nrc.ExceededNumberOfAttempts                  => "ExceededAttempts",
        Nrc.RequiredTimeDelayNotExpired               => "RequiredTimeDelayNotExpired",
        Nrc.RequestCorrectlyReceivedResponsePending   => "RCR-RP",
        Nrc.SchedulerFull                             => "SchedulerFull",
        Nrc.VoltageOutOfRange                         => "VoltageOutOfRange",
        Nrc.GeneralProgrammingFailure                 => "GeneralProgrammingFailure",
        _ => $"NRC ${b:X2}",
    };
}
