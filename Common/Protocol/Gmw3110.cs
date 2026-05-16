namespace Common.Protocol;

// GMW3110-2010 service identifiers. Request SID == response SID + 0x40 for
// positive responses, $7F for negative responses with the original SID echoed.
public static class Service
{
    // Request SIDs
    public const byte ClearDiagnosticInfo = 0x04;
    public const byte InitiateDiagnosticOperation = 0x10;     // $10 - sets P3C ACTIVE
    public const byte ReadFailureRecordData = 0x12;
    public const byte ReadDataByIdentifier = 0x1A;
    public const byte ReturnToNormalMode = 0x20;              // $20 - clears all enhanced state
    public const byte ReadDataByParameterIdentifier = 0x22;   // $22 - main read
    public const byte SecurityAccess = 0x27;                  // $27 - seed/key unlock (§8)
    public const byte DisableNormalCommunication = 0x28;      // $28 - silence non-diag traffic (§8.9)
    public const byte DynamicallyDefineMessage = 0x2C;        // $2C - DPID definition
    public const byte DefinePidByAddress = 0x2D;              // $2D - PID by memory address
    public const byte RequestDownload = 0x34;                 // $34 - prepare for module programming (§8.12)
    public const byte TransferData = 0x36;                    // $36 - download/execute block (§8.13)
    public const byte WriteDataByIdentifier = 0x3B;           // $3B - write static DID (§8.14, Appendix D Pt 2 $11/$12/$13)
    public const byte TesterPresent = 0x3E;                   // $3E - keepalive
    public const byte ReportProgrammedState = 0xA2;           // $A2 - SPS programmed-state query (§8.16)
    public const byte ProgrammingMode = 0xA5;                 // $A5 - enter programming session (§8.17)
    public const byte ReadDataByPacketIdentifier = 0xAA;      // $AA - periodic DPID

    // Positive response = request + 0x40
    public const byte NegativeResponse = 0x7F;

    public static byte Positive(byte requestSid) => (byte)(requestSid + 0x40);
}

// GMW3110-2010 §7.2 negative response codes (RC_).
public static class Nrc
{
    public const byte ServiceNotSupported = 0x11;             // SNS
    public const byte SubFunctionNotSupportedInvalidFormat = 0x12;  // SFNS-IF
    public const byte ConditionsNotCorrectOrSequenceError = 0x22;   // CNCRSE
    public const byte RequestOutOfRange = 0x31;               // ROOR
    public const byte SecurityAccessDenied = 0x33;            // SAD - $27 sub not allowed in current state
    public const byte InvalidKey = 0x35;                      // IK - $27 key mismatch
    public const byte ExceededNumberOfAttempts = 0x36;        // ENOA - $27 lockout triggered
    public const byte RequiredTimeDelayNotExpired = 0x37;     // RTDNE - $27 inside lockout window
    public const byte RequestCorrectlyReceivedResponsePending = 0x78;  // RCR-RP
    public const byte SchedulerFull = 0x81;                   // SCHDFULL
    public const byte VoltageOutOfRange = 0x83;               // VOLTRNG
    public const byte GeneralProgrammingFailure = 0x85;       // PROGFAIL - $36 erase/program/CRC error (§8.13.4)
}

// $AA sub-function (rate byte) values per GMW3110 §8.20 / Table 190.
public enum DpidRate : byte
{
    StopSending = 0x00,
    SendOneResponse = 0x01,
    Slow = 0x02,           // ~1000 ms
    Medium = 0x03,         // ~200 ms (we run 100 ms = 10 Hz to match DataLogger convention)
    Fast = 0x04,           // ~25 ms (we run 40 ms = 25 Hz to match DataLogger convention)
}

// ISO 15765-2 PCI nibble (upper 4 bits of byte 0).
public enum PciType : byte
{
    Single = 0x00,
    First = 0x10,
    Consecutive = 0x20,
    FlowControl = 0x30,
}

// GMW3110-2010 application timing parameters (§6.2).
public static class Timing
{
    public const int P2CT = 150;          // tester USDT response timeout (ms)
    public const int P2CT_Star = 5100;    // extended timeout after RC_$78 (ms)
    public const int P3Cnom = 5000;       // ECU TesterPresent timeout, nominal (ms)
    public const int P3Cmax = 5100;       // ECU TesterPresent timeout, max (ms)
}

// GMLAN diagnostic CAN ID conventions (§4.4). Tester→ECU = $7E0 + diag_addr;
// ECU→tester USDT = $7E8 + diag_addr; UUDT = $5xx form (configurable per ECU).
public static class GmlanCanId
{
    public const ushort AllNodesRequest = 0x101;       // functional broadcast request ID
    public const byte AllNodesExtAddr = 0xFE;          // functional extended address (in data[0])
    public const byte GatewayExtAddr = 0xFD;

    /// <summary>
    /// ISO 15765-4 OBD-II functional broadcast request ID. Modern GM ECUs accept
    /// BOTH this AND the GMLAN $101+$FE scheme - the GMW3110 form predates the
    /// OBD-II spec, and emissions compliance requires honouring $7DF for generic
    /// scan tools. Unlike $101+$FE, $7DF uses NORMAL addressing (data[0] is the
    /// PCI byte, no extended-address byte).
    /// </summary>
    public const ushort Obd2FunctionalRequest = 0x7DF;

    /// <summary>
    /// GMW3110 §8.16 SPS_PrimeReq base: SPS_TYPE_C ECUs (blank/unprogrammed) listen
    /// on $000 | diag_address once they've enabled diagnostic responses. e.g.
    /// diagnostic address $11 → SPS_PrimeReq $011.
    /// </summary>
    public const ushort SpsPrimeRequestBase = 0x000;

    /// <summary>
    /// GMW3110 §8.16 SPS_PrimeRsp base: SPS_TYPE_C ECUs respond on $300 | diag_address.
    /// e.g. diagnostic address $11 → SPS_PrimeRsp $311. GM SPS / DPS installs a
    /// PASS_FILTER mask=$700 pattern=$300 during $A2 enumeration specifically to
    /// catch these.
    /// </summary>
    public const ushort SpsPrimeResponseBase = 0x300;

    public static ushort SpsPrimeRequest(byte diagAddress)  => (ushort)(SpsPrimeRequestBase | diagAddress);
    public static ushort SpsPrimeResponse(byte diagAddress) => (ushort)(SpsPrimeResponseBase | diagAddress);
}

/// <summary>
/// GMW3110 §8.16 / §9.x classification of an ECU's programmability state at
/// boot. The simulator uses this to gate the SPS_PrimeReq/Rsp activation flow
/// that GM SPS / DPS drives during "Get Controller Info" / programming setup.
/// </summary>
///   <c>A</c>: fully programmed, permanent diagnostic CAN IDs in flash. Default;
///            responds normally on its USDT response ID at all times.
///   <c>B</c>: missing op-software or calibration, but permanent CAN IDs ARE
///            stored in flash. Same wire behaviour as A; the distinction is
///            informational (real ECUs report a different programmedState).
///   <c>C</c>: blank/unprogrammed. No permanent CAN IDs. Silent on every request
///            until receiving $A2 while $28 DisableNormalCommunication is active;
///            then enables diagnostic responses, replies on SPS_PrimeRsp
///            ($300 | diag_address), and subsequently honours SPS_PrimeReq
///            ($000 | diag_address) until $20 / P3C timeout reverts to silent.
public enum SpsType : byte
{
    A = 0,
    B = 1,
    C = 2,
}
