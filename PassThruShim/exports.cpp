// PassThru shim — SAE J2534-1 **v04.04 only**.
//
// This DLL deliberately implements the v04.04 PassThru API surface and
// nothing more. We do NOT support v05.00, and that is the design intent —
// not a missing feature.
//
//   * The 14 v04.04 functions (Open/Close/Connect/Disconnect/ReadMsgs/
//     WriteMsgs/StartPeriodicMsg/StopPeriodicMsg/StartMsgFilter/
//     StopMsgFilter/SetProgrammingVoltage/ReadVersion/GetLastError/Ioctl)
//     are all exported. Hosts detect us as V404_SIGNATURE = 0x03FFF.
//   * v05.00 functions (ScanForDevices, GetNextDevice, LogicalConnect,
//     LogicalDisconnect, Select, QueueMsgs) are intentionally NOT exported.
//   * PassThruGetNextCarDAQ (the Drew Tech proprietary extension some
//     libraries use as a fallback enumeration path) is also NOT exported —
//     J2534-Sharp's HeapIntPtr is 32-bit-only and AVs in 64-bit hosts
//     when that path is taken.
//
// PassThruReadVersion reports "04.04" as the API version so hosts cannot
// mistake us for a v05.00 driver.
//
// Consequence for hosts: J2534-Sharp's api.GetDeviceList() returns an empty
// list (it only knows the v05.00 path or the Drew Tech path, both omitted).
// Hosts that gate connection on a non-empty list must call api.GetDevice("")
// for the default device — that's the J2534-1 v04.04 spec-defined open
// path. The end-to-end test at Tests/test_j2534sharp_open.ps1 is the
// reference implementation.
//
// All exports except SetProgrammingVoltage and GetLastError route through
// the IPC layer to the C# simulator. SetProgrammingVoltage is a stub (no
// hardware programming pin in a software simulator). GetLastError is
// resolved locally from a TLS string set by the most recent call.

#include "j2534.h"
#include "error_buffer.h"
#include "ipc_client.h"
#include <cstdio>
#include <vector>
#include <cstring>

// ---------------- helpers ----------------

static void DebugLog(const char* fmt, ...)
{
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    OutputDebugStringA("[PassThruShim] ");
    OutputDebugStringA(buf);
    OutputDebugStringA("\n");
}

// Format up to `cap` bytes as space-separated uppercase hex into `dst`.
// Appends "..." when the source is longer than cap so the log shows truncation.
static void FormatHex(char* dst, size_t dstCap, const uint8_t* src, size_t len, size_t cap)
{
    if (dstCap == 0) return;
    dst[0] = '\0';
    size_t n = len < cap ? len : cap;
    size_t off = 0;
    for (size_t i = 0; i < n && off + 4 < dstCap; i++)
    {
        off += snprintf(dst + off, dstCap - off, i == 0 ? "%02X" : " %02X", src[i]);
    }
    if (len > cap && off + 4 < dstCap)
    {
        snprintf(dst + off, dstCap - off, " ...");
    }
}

static const char* FilterTypeName(unsigned long t)
{
    switch (t)
    {
    case 1: return "PASS_FILTER";
    case 2: return "BLOCK_FILTER";
    case 3: return "FLOW_CONTROL_FILTER";
    default: return "?";
    }
}

// Little-endian readers/writers — wire format on the C# side (Common.Wire).
static void PutU32(std::vector<uint8_t>& out, uint32_t v)
{
    out.push_back((uint8_t)(v & 0xFF));
    out.push_back((uint8_t)((v >> 8) & 0xFF));
    out.push_back((uint8_t)((v >> 16) & 0xFF));
    out.push_back((uint8_t)((v >> 24) & 0xFF));
}

static uint32_t GetU32(const std::vector<uint8_t>& buf, size_t off)
{
    return buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16) | (buf[off + 3] << 24);
}

static bool ReadStringU16(const std::vector<uint8_t>& buf, size_t& off, char* dest, size_t destCap)
{
    if (off + 2 > buf.size()) return false;
    uint16_t len = (uint16_t)(buf[off] | (buf[off + 1] << 8));
    off += 2;
    if (off + len > buf.size()) return false;
    size_t copy = (len < destCap - 1) ? len : destCap - 1;
    memcpy(dest, buf.data() + off, copy);
    dest[copy] = '\0';
    off += len;
    return true;
}

// PASSTHRU_MSG marshalling — only ProtocolID, RxStatus, TxFlags, Timestamp,
// ExtraDataIndex (5 × u32) plus DataSize-prefixed Data. Never sends the
// full 4128-byte buffer.
static void PutPassThruMsg(std::vector<uint8_t>& out, const PASSTHRU_MSG& m)
{
    PutU32(out, m.ProtocolID);
    PutU32(out, m.RxStatus);
    PutU32(out, m.TxFlags);
    PutU32(out, m.Timestamp);
    PutU32(out, m.ExtraDataIndex);
    PutU32(out, m.DataSize);
    for (uint32_t i = 0; i < m.DataSize && i < sizeof(m.Data); i++)
        out.push_back(m.Data[i]);
}

// Wire order on both sides: ProtocolID, RxStatus, TxFlags, Timestamp,
// ExtraDataIndex, DataSize, Data[DataSize].
static bool GetPassThruMsg(const std::vector<uint8_t>& buf, size_t& off, PASSTHRU_MSG& m)
{
    if (off + 24 > buf.size()) return false;
    m.ProtocolID     = GetU32(buf, off); off += 4;
    m.RxStatus       = GetU32(buf, off); off += 4;
    m.TxFlags        = GetU32(buf, off); off += 4;
    m.Timestamp      = GetU32(buf, off); off += 4;
    m.ExtraDataIndex = GetU32(buf, off); off += 4;
    m.DataSize       = GetU32(buf, off); off += 4;
    if (off + m.DataSize > buf.size()) return false;
    if (m.DataSize > sizeof(m.Data)) return false;
    memcpy(m.Data, buf.data() + off, m.DataSize);
    off += m.DataSize;
    return true;
}

// Common pattern: send request, get response, return result code from first u32.
static long ExchangeAndExtractRc(uint8_t requestType, const std::vector<uint8_t>& req,
                                  std::vector<uint8_t>& resp, uint8_t expectedRespType,
                                  long onTransportFailure)
{
    if (!IpcClient::EnsureConnected()) return ERR_DEVICE_NOT_CONNECTED;
    uint8_t respType = IpcClient::Exchange(requestType, req, resp);
    if (respType == 0) return onTransportFailure;
    if (respType != expectedRespType) { SetLastErrorString("Unexpected response type"); return ERR_FAILED; }
    if (resp.size() < 4) { SetLastErrorString("Short response"); return ERR_FAILED; }
    return (long)GetU32(resp, 0);
}

// ---------------- exports ----------------

extern "C" {

long __stdcall PassThruOpen(void* pName, unsigned long* pDeviceID)
{
    DebugLog("PassThruOpen");
    if (pDeviceID == nullptr) { SetLastErrorString("PassThruOpen: pDeviceID null"); return ERR_NULL_PARAMETER; }

    std::vector<uint8_t> req, resp;
    long rc = ExchangeAndExtractRc(MSG_OPEN_REQ, req, resp, /*expected=*/0x81, ERR_DEVICE_NOT_CONNECTED);
    if (rc != STATUS_NOERROR) return rc;
    *pDeviceID = GetU32(resp, 4);
    SetLastErrorString("No error.");
    return STATUS_NOERROR;
}

long __stdcall PassThruClose(unsigned long DeviceID)
{
    DebugLog("PassThruClose %lu", DeviceID);
    std::vector<uint8_t> req(4); req[0] = (uint8_t)DeviceID;
    req[1] = (uint8_t)(DeviceID >> 8); req[2] = (uint8_t)(DeviceID >> 16); req[3] = (uint8_t)(DeviceID >> 24);
    std::vector<uint8_t> resp;
    long rc = ExchangeAndExtractRc(MSG_CLOSE_REQ, req, resp, 0x82, ERR_FAILED);
    if (rc == STATUS_NOERROR) SetLastErrorString("No error.");
    return rc;
}

long __stdcall PassThruConnect(
    unsigned long DeviceID, unsigned long ProtocolID,
    unsigned long Flags, unsigned long Baudrate, unsigned long* pChannelID)
{
    DebugLog("PassThruConnect dev=%lu proto=%lu flags=0x%lx baud=%lu", DeviceID, ProtocolID, Flags, Baudrate);
    if (pChannelID == nullptr) { SetLastErrorString("PassThruConnect: pChannelID null"); return ERR_NULL_PARAMETER; }

    std::vector<uint8_t> req;
    PutU32(req, DeviceID);
    PutU32(req, ProtocolID);
    PutU32(req, Flags);
    PutU32(req, Baudrate);
    std::vector<uint8_t> resp;
    long rc = ExchangeAndExtractRc(MSG_CONNECT_REQ, req, resp, 0x83, ERR_FAILED);
    if (rc != STATUS_NOERROR) return rc;
    *pChannelID = GetU32(resp, 4);
    SetLastErrorString("No error.");
    return STATUS_NOERROR;
}

long __stdcall PassThruDisconnect(unsigned long ChannelID)
{
    DebugLog("PassThruDisconnect %lu", ChannelID);
    std::vector<uint8_t> req; PutU32(req, ChannelID);
    std::vector<uint8_t> resp;
    long rc = ExchangeAndExtractRc(MSG_DISCONNECT_REQ, req, resp, 0x84, ERR_FAILED);
    if (rc == STATUS_NOERROR) SetLastErrorString("No error.");
    return rc;
}

long __stdcall PassThruReadMsgs(
    unsigned long ChannelID, PASSTHRU_MSG* pMsg, unsigned long* pNumMsgs, unsigned long Timeout)
{
    if (pMsg == nullptr || pNumMsgs == nullptr) { SetLastErrorString("PassThruReadMsgs: null param"); return ERR_NULL_PARAMETER; }

    unsigned long maxMsgs = *pNumMsgs;
    std::vector<uint8_t> req;
    PutU32(req, ChannelID);
    PutU32(req, maxMsgs);
    PutU32(req, Timeout);
    std::vector<uint8_t> resp;
    if (!IpcClient::EnsureConnected()) return ERR_DEVICE_NOT_CONNECTED;
    uint8_t respType = IpcClient::Exchange(MSG_READ_MSGS_REQ, req, resp);
    if (respType == 0) return ERR_FAILED;
    if (respType != 0x85 || resp.size() < 8) { SetLastErrorString("ReadMsgs bad response"); return ERR_FAILED; }
    long rc = (long)GetU32(resp, 0);
    uint32_t numActual = GetU32(resp, 4);
    size_t off = 8;
    for (uint32_t i = 0; i < numActual; i++)
    {
        if (!GetPassThruMsg(resp, off, pMsg[i])) return ERR_FAILED;
    }
    *pNumMsgs = numActual;
    DebugLog("PassThruReadMsgs ch=%lu max=%lu timeout=%lums -> rc=%ld count=%lu",
             ChannelID, maxMsgs, Timeout, rc, (unsigned long)numActual);
    for (uint32_t i = 0; i < numActual; i++)
    {
        char hex[200];
        FormatHex(hex, sizeof(hex), pMsg[i].Data, pMsg[i].DataSize, 32);
        DebugLog("  rx[%u] proto=%lu rxst=0x%lx ts=%lu data(%lu)=%s",
                 i, pMsg[i].ProtocolID, pMsg[i].RxStatus, pMsg[i].Timestamp,
                 pMsg[i].DataSize, hex);
    }
    SetLastErrorString("No error.");
    return rc;
}

long __stdcall PassThruWriteMsgs(
    unsigned long ChannelID, PASSTHRU_MSG* pMsg, unsigned long* pNumMsgs, unsigned long Timeout)
{
    if (pMsg == nullptr || pNumMsgs == nullptr) { SetLastErrorString("PassThruWriteMsgs: null param"); return ERR_NULL_PARAMETER; }

    DebugLog("PassThruWriteMsgs ch=%lu count=%lu timeout=%lums", ChannelID, *pNumMsgs, Timeout);
    for (uint32_t i = 0; i < *pNumMsgs; i++)
    {
        char hex[200];
        FormatHex(hex, sizeof(hex), pMsg[i].Data, pMsg[i].DataSize, 32);
        DebugLog("  tx[%u] proto=%lu flags=0x%lx data(%lu)=%s",
                 i, pMsg[i].ProtocolID, pMsg[i].TxFlags, pMsg[i].DataSize, hex);
    }

    std::vector<uint8_t> req;
    PutU32(req, ChannelID);
    PutU32(req, *pNumMsgs);
    PutU32(req, Timeout);
    for (uint32_t i = 0; i < *pNumMsgs; i++) PutPassThruMsg(req, pMsg[i]);

    std::vector<uint8_t> resp;
    long rc = ExchangeAndExtractRc(MSG_WRITE_MSGS_REQ, req, resp, 0x86, ERR_FAILED);
    if (rc != STATUS_NOERROR) return rc;
    if (resp.size() >= 8) *pNumMsgs = GetU32(resp, 4);
    SetLastErrorString("No error.");
    return STATUS_NOERROR;
}

long __stdcall PassThruStartPeriodicMsg(
    unsigned long ChannelID, PASSTHRU_MSG* pMsg, unsigned long* pMsgID, unsigned long TimeInterval)
{
    DebugLog("PassThruStartPeriodicMsg ch=%lu interval=%lu", ChannelID, TimeInterval);
    if (pMsgID == nullptr) { SetLastErrorString("PassThruStartPeriodicMsg: pMsgID null"); return ERR_NULL_PARAMETER; }
    if (pMsg  == nullptr) { SetLastErrorString("PassThruStartPeriodicMsg: pMsg null");  return ERR_NULL_PARAMETER; }

    std::vector<uint8_t> req, resp;
    PutU32(req, ChannelID);
    PutU32(req, TimeInterval);
    PutPassThruMsg(req, *pMsg);

    long rc = ExchangeAndExtractRc(MSG_START_PERIOD_REQ, req, resp, 0x89, ERR_DEVICE_NOT_CONNECTED);
    if (rc != STATUS_NOERROR) return rc;
    if (resp.size() < 8) { SetLastErrorString("PassThruStartPeriodicMsg: short response"); return ERR_FAILED; }
    *pMsgID = GetU32(resp, 4);
    SetLastErrorString("No error.");
    return STATUS_NOERROR;
}

long __stdcall PassThruStopPeriodicMsg(unsigned long ChannelID, unsigned long MsgID)
{
    DebugLog("PassThruStopPeriodicMsg ch=%lu id=%lu", ChannelID, MsgID);
    std::vector<uint8_t> req, resp;
    PutU32(req, ChannelID);
    PutU32(req, MsgID);
    long rc = ExchangeAndExtractRc(MSG_STOP_PERIOD_REQ, req, resp, 0x8A, ERR_DEVICE_NOT_CONNECTED);
    if (rc == STATUS_NOERROR) SetLastErrorString("No error.");
    return rc;
}

long __stdcall PassThruStartMsgFilter(
    unsigned long ChannelID, unsigned long FilterType,
    PASSTHRU_MSG* pMaskMsg, PASSTHRU_MSG* pPatternMsg, PASSTHRU_MSG* pFlowControlMsg,
    unsigned long* pFilterID)
{
    if (pFilterID == nullptr) { SetLastErrorString("PassThruStartMsgFilter: pFilterID null"); return ERR_NULL_PARAMETER; }
    PASSTHRU_MSG empty = {};
    const PASSTHRU_MSG& mask = pMaskMsg ? *pMaskMsg : empty;
    const PASSTHRU_MSG& pat  = pPatternMsg ? *pPatternMsg : empty;
    const PASSTHRU_MSG& fc   = pFlowControlMsg ? *pFlowControlMsg : empty;
    {
        char mHex[120], pHex[120], fHex[120];
        FormatHex(mHex, sizeof(mHex), mask.Data, mask.DataSize, 16);
        FormatHex(pHex, sizeof(pHex), pat.Data, pat.DataSize, 16);
        FormatHex(fHex, sizeof(fHex), fc.Data, fc.DataSize, 16);
        DebugLog("PassThruStartMsgFilter ch=%lu type=%lu(%s) mask(%lu)=%s pattern(%lu)=%s fc(%lu)=%s",
                 ChannelID, FilterType, FilterTypeName(FilterType),
                 mask.DataSize, mHex, pat.DataSize, pHex, fc.DataSize, fHex);
    }
    std::vector<uint8_t> req;
    PutU32(req, ChannelID);
    PutU32(req, FilterType);
    PutPassThruMsg(req, mask);
    PutPassThruMsg(req, pat);
    PutPassThruMsg(req, fc);

    std::vector<uint8_t> resp;
    long rc = ExchangeAndExtractRc(MSG_START_FILTER_REQ, req, resp, 0x87, ERR_FAILED);
    if (rc != STATUS_NOERROR) return rc;
    *pFilterID = GetU32(resp, 4);
    SetLastErrorString("No error.");
    return STATUS_NOERROR;
}

long __stdcall PassThruStopMsgFilter(unsigned long ChannelID, unsigned long FilterID)
{
    std::vector<uint8_t> req;
    PutU32(req, ChannelID);
    PutU32(req, FilterID);
    std::vector<uint8_t> resp;
    long rc = ExchangeAndExtractRc(MSG_STOP_FILTER_REQ, req, resp, 0x88, ERR_FAILED);
    if (rc == STATUS_NOERROR) SetLastErrorString("No error.");
    return rc;
}

long __stdcall PassThruSetProgrammingVoltage(
    unsigned long DeviceID, unsigned long PinNumber, unsigned long Voltage)
{
    SetLastErrorString("No error.");
    return STATUS_NOERROR;
}

long __stdcall PassThruReadVersion(
    unsigned long DeviceID, char* pFirmwareVersion, char* pDllVersion, char* pApiVersion)
{
    if (pFirmwareVersion == nullptr || pDllVersion == nullptr || pApiVersion == nullptr)
    { SetLastErrorString("PassThruReadVersion: null parameter"); return ERR_NULL_PARAMETER; }

    std::vector<uint8_t> req;
    PutU32(req, DeviceID);
    std::vector<uint8_t> resp;
    long rc = ExchangeAndExtractRc(MSG_READ_VERSION_REQ, req, resp, 0x8C, ERR_FAILED);
    if (rc != STATUS_NOERROR) return rc;

    size_t off = 4;
    if (!ReadStringU16(resp, off, pFirmwareVersion, 80) ||
        !ReadStringU16(resp, off, pDllVersion, 80) ||
        !ReadStringU16(resp, off, pApiVersion, 80))
    { SetLastErrorString("ReadVersion: malformed response"); return ERR_FAILED; }
    SetLastErrorString("No error.");
    return STATUS_NOERROR;
}

long __stdcall PassThruGetLastError(char* pErrorDescription)
{
    if (pErrorDescription == nullptr) return ERR_NULL_PARAMETER;
    CopyLastErrorString(pErrorDescription);
    return STATUS_NOERROR;
}

// Marshal a J2534 Ioctl call through the IPC. Per-IoctlID we encode the C
// input structure into a flat byte payload and decode the response into the
// caller's output structure. The wire layout matches the C# server's Ioctl
// dispatcher in Core/Ipc/RequestDispatcher.cs.
//
// Wire request payload: [u32 channelId][u32 ioctlId][u32 inLen][bytes in]
// Wire response payload: [u32 resultCode][u32 outLen][bytes out]
long __stdcall PassThruIoctl(
    unsigned long ChannelID, unsigned long IoctlID, void* pInput, void* pOutput)
{
    DebugLog("PassThruIoctl ch=%lu id=0x%lx", ChannelID, IoctlID);

    std::vector<uint8_t> inBytes;
    switch (IoctlID)
    {
    case 0x01: // GET_CONFIG  — pInput → SCONFIG_LIST*; encode paramIds only
    {
        if (pInput == nullptr) { SetLastErrorString("Ioctl GET_CONFIG: pInput null"); return ERR_NULL_PARAMETER; }
        SCONFIG_LIST* list = (SCONFIG_LIST*)pInput;
        PutU32(inBytes, list->NumOfParams);
        char paramHex[300]; paramHex[0] = '\0'; size_t pOff = 0;
        for (unsigned long i = 0; i < list->NumOfParams; i++)
        {
            PutU32(inBytes, list->ConfigPtr[i].Parameter);
            if (pOff + 12 < sizeof(paramHex))
                pOff += snprintf(paramHex + pOff, sizeof(paramHex) - pOff,
                                 i == 0 ? "0x%02lX" : ", 0x%02lX", list->ConfigPtr[i].Parameter);
        }
        DebugLog("  GET_CONFIG %lu params: %s", list->NumOfParams, paramHex);
        break;
    }
    case 0x02: // SET_CONFIG  — pInput → SCONFIG_LIST*; encode (param, value) pairs
    {
        if (pInput == nullptr) { SetLastErrorString("Ioctl SET_CONFIG: pInput null"); return ERR_NULL_PARAMETER; }
        SCONFIG_LIST* list = (SCONFIG_LIST*)pInput;
        PutU32(inBytes, list->NumOfParams);
        char paramHex[400]; paramHex[0] = '\0'; size_t pOff = 0;
        for (unsigned long i = 0; i < list->NumOfParams; i++)
        {
            PutU32(inBytes, list->ConfigPtr[i].Parameter);
            PutU32(inBytes, list->ConfigPtr[i].Value);
            if (pOff + 24 < sizeof(paramHex))
                pOff += snprintf(paramHex + pOff, sizeof(paramHex) - pOff,
                                 i == 0 ? "[0x%02lX]=%lu" : ", [0x%02lX]=%lu",
                                 list->ConfigPtr[i].Parameter, list->ConfigPtr[i].Value);
        }
        DebugLog("  SET_CONFIG %lu params: %s", list->NumOfParams, paramHex);
        break;
    }
    case 0x0C: // ADD_TO_FUNCT_MSG_LOOKUP_TABLE   — pInput → SBYTE_ARRAY*
    case 0x0D: // DELETE_FROM_FUNCT_MSG_LOOKUP_TABLE
    {
        if (pInput == nullptr) { SetLastErrorString("Ioctl FUNC: pInput null"); return ERR_NULL_PARAMETER; }
        SBYTE_ARRAY* arr = (SBYTE_ARRAY*)pInput;
        PutU32(inBytes, arr->NumOfBytes);
        for (unsigned long i = 0; i < arr->NumOfBytes; i++)
            inBytes.push_back(arr->BytePtr[i]);
        break;
    }
    case 0x03: // READ_VBATT
    case 0x07: // CLEAR_TX_BUFFER
    case 0x08: // CLEAR_RX_BUFFER
    case 0x09: // CLEAR_PERIODIC_MSGS
    case 0x0A: // CLEAR_MSG_FILTERS
    case 0x0B: // CLEAR_FUNCT_MSG_LOOKUP_TABLE
    case 0x0E: // READ_PROG_VOLTAGE
        // No input payload.
        break;
    case 0x04: // FIVE_BAUD_INIT — pre-CAN; not supported by the simulator
    case 0x05: // FAST_INIT — KWP2000; not supported
        SetLastErrorString("PassThruIoctl: protocol-init Ioctl not supported by simulator");
        return ERR_NOT_SUPPORTED;
    default:
        SetLastErrorString("PassThruIoctl: unknown IoctlID");
        return ERR_INVALID_IOCTL_ID;
    }

    std::vector<uint8_t> req;
    PutU32(req, ChannelID);
    PutU32(req, IoctlID);
    PutU32(req, (uint32_t)inBytes.size());
    if (!inBytes.empty()) req.insert(req.end(), inBytes.begin(), inBytes.end());

    std::vector<uint8_t> resp;
    long rc = ExchangeAndExtractRc(MSG_IOCTL_REQ, req, resp, 0x8B, ERR_DEVICE_NOT_CONNECTED);
    if (rc != STATUS_NOERROR) return rc;
    if (resp.size() < 8) { SetLastErrorString("PassThruIoctl: short response"); return ERR_FAILED; }

    uint32_t outLen = GetU32(resp, 4);
    if (resp.size() < 8 + outLen) { SetLastErrorString("PassThruIoctl: truncated output"); return ERR_FAILED; }

    // Decode output into the caller's structure based on IoctlID.
    switch (IoctlID)
    {
    case 0x01: // GET_CONFIG — output = [u32 numParams][numParams × u32 value]
    {
        if (pInput == nullptr) break;
        SCONFIG_LIST* list = (SCONFIG_LIST*)pInput;
        if (outLen < 4) { SetLastErrorString("Ioctl GET_CONFIG: short output"); return ERR_FAILED; }
        uint32_t numOut = GetU32(resp, 8);
        if (numOut > list->NumOfParams) numOut = list->NumOfParams;
        if (outLen < 4 + numOut * 4) { SetLastErrorString("Ioctl GET_CONFIG: truncated values"); return ERR_FAILED; }
        for (uint32_t i = 0; i < numOut; i++)
            list->ConfigPtr[i].Value = GetU32(resp, 12 + i * 4);
        break;
    }
    case 0x03: // READ_VBATT — output = [u32 mV]
    case 0x0E: // READ_PROG_VOLTAGE — output = [u32 mV]
    {
        if (pOutput == nullptr) break;
        if (outLen < 4) { SetLastErrorString("Ioctl voltage: short output"); return ERR_FAILED; }
        *(unsigned long*)pOutput = GetU32(resp, 8);
        break;
    }
    default:
        // No output payload expected; nothing to decode.
        break;
    }

    SetLastErrorString("No error.");
    return STATUS_NOERROR;
}

// No PassThruGetNextCarDAQ, no PassThruScanForDevices, no LogicalConnect,
// no Select, no QueueMsgs. v04.04 only. See the file header comment.

} // extern "C"
