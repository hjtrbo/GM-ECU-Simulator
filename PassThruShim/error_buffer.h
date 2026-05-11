#pragma once

// Sets the per-thread last-error string returned by PassThruGetLastError.
// J2534-1 specifies an 80-character buffer; we accept any C string and clamp.
void SetLastErrorString(const char* msg);

// Copies the current thread's last-error string into the caller's buffer
// (must be at least 80 bytes per J2534-1).
void CopyLastErrorString(char* dest);
