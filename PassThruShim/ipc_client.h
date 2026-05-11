// IPC client for the PassThru shim. Connects to the C# simulator over a
// named pipe and exchanges length-prefixed binary frames matching the
// Common.Wire.FrameTransport format on the managed side.
#pragma once

#include <windows.h>
#include <cstdint>
#include <vector>

// Wire frame: [u32 length][u8 messageType][payload], little-endian. Length
// covers messageType + payload, NOT the length field itself.

// Message types — must match Common/Wire/IpcMessageTypes.cs
constexpr uint8_t MSG_OPEN_REQ          = 0x01;
constexpr uint8_t MSG_CLOSE_REQ         = 0x02;
constexpr uint8_t MSG_CONNECT_REQ       = 0x03;
constexpr uint8_t MSG_DISCONNECT_REQ    = 0x04;
constexpr uint8_t MSG_READ_MSGS_REQ     = 0x05;
constexpr uint8_t MSG_WRITE_MSGS_REQ    = 0x06;
constexpr uint8_t MSG_START_FILTER_REQ  = 0x07;
constexpr uint8_t MSG_STOP_FILTER_REQ   = 0x08;
constexpr uint8_t MSG_START_PERIOD_REQ  = 0x09;
constexpr uint8_t MSG_STOP_PERIOD_REQ   = 0x0A;
constexpr uint8_t MSG_IOCTL_REQ         = 0x0B;
constexpr uint8_t MSG_READ_VERSION_REQ  = 0x0C;

class IpcClient
{
public:
    // Returns true on success. Lazily launches the simulator process if the
    // pipe doesn't exist, then retries connecting for up to ~3 seconds.
    static bool EnsureConnected();

    // Sends a request frame and waits for a response. payloadIn may be empty.
    // payloadOut is populated with the response payload. Returns the response
    // type byte, or 0 on transport failure.
    static uint8_t Exchange(
        uint8_t requestType,
        const std::vector<uint8_t>& payloadIn,
        std::vector<uint8_t>& payloadOut);

    static void Disconnect();
};
