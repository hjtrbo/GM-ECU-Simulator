#include "ipc_client.h"
#include "error_buffer.h"
#include <mutex>

static HANDLE g_pipe = INVALID_HANDLE_VALUE;
static std::mutex g_pipeMutex;

constexpr const wchar_t* PIPE_PATH = L"\\\\.\\pipe\\GmEcuSim.PassThru";

static bool TryConnect()
{
    HANDLE h = CreateFileW(
        PIPE_PATH,
        GENERIC_READ | GENERIC_WRITE,
        0,
        nullptr,
        OPEN_EXISTING,
        0,
        nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    g_pipe = h;
    return true;
}

bool IpcClient::EnsureConnected()
{
    std::lock_guard<std::mutex> lock(g_pipeMutex);
    if (g_pipe != INVALID_HANDLE_VALUE) return true;
    if (TryConnect()) return true;

    SetLastErrorString("PassThruShim: GmEcuSimulator.exe is not running");
    return false;
}

void IpcClient::Disconnect()
{
    std::lock_guard<std::mutex> lock(g_pipeMutex);
    if (g_pipe != INVALID_HANDLE_VALUE)
    {
        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
    }
}

static bool WriteAll(HANDLE h, const void* data, DWORD len)
{
    DWORD written = 0;
    const uint8_t* p = (const uint8_t*)data;
    while (written < len)
    {
        DWORD n = 0;
        if (!WriteFile(h, p + written, len - written, &n, nullptr) || n == 0)
            return false;
        written += n;
    }
    return true;
}

static bool ReadAll(HANDLE h, void* data, DWORD len)
{
    DWORD readTotal = 0;
    uint8_t* p = (uint8_t*)data;
    while (readTotal < len)
    {
        DWORD n = 0;
        if (!ReadFile(h, p + readTotal, len - readTotal, &n, nullptr) || n == 0)
            return false;
        readTotal += n;
    }
    return true;
}

uint8_t IpcClient::Exchange(
    uint8_t requestType,
    const std::vector<uint8_t>& payloadIn,
    std::vector<uint8_t>& payloadOut)
{
    std::lock_guard<std::mutex> lock(g_pipeMutex);
    if (g_pipe == INVALID_HANDLE_VALUE)
    {
        SetLastErrorString("IPC not connected");
        return 0;
    }

    // Frame: [u32 length][u8 type][payload]
    uint32_t len = (uint32_t)(payloadIn.size() + 1);
    uint8_t header[5];
    header[0] = (uint8_t)(len & 0xFF);
    header[1] = (uint8_t)((len >> 8) & 0xFF);
    header[2] = (uint8_t)((len >> 16) & 0xFF);
    header[3] = (uint8_t)((len >> 24) & 0xFF);
    header[4] = requestType;

    if (!WriteAll(g_pipe, header, 5) ||
        (!payloadIn.empty() && !WriteAll(g_pipe, payloadIn.data(), (DWORD)payloadIn.size())))
    {
        SetLastErrorString("IPC write failed");
        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
        return 0;
    }

    uint8_t respHdr[5];
    if (!ReadAll(g_pipe, respHdr, 5))
    {
        SetLastErrorString("IPC read header failed");
        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
        return 0;
    }
    uint32_t respLen = respHdr[0] | (respHdr[1] << 8) | (respHdr[2] << 16) | (respHdr[3] << 24);
    uint8_t respType = respHdr[4];

    // Reject malformed frame lengths defensively. respLen counts type + payload,
    // so it must be >= 1. The 4 MiB cap matches Common.Wire.FrameTransport.
    constexpr uint32_t MaxFrameSize = 4u * 1024u * 1024u;
    if (respLen == 0 || respLen > MaxFrameSize)
    {
        SetLastErrorString("IPC bad response length");
        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
        return 0;
    }
    uint32_t respPayloadLen = respLen - 1;

    payloadOut.resize(respPayloadLen);
    if (respPayloadLen > 0 && !ReadAll(g_pipe, payloadOut.data(), respPayloadLen))
    {
        SetLastErrorString("IPC read payload failed");
        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
        return 0;
    }
    return respType;
}
