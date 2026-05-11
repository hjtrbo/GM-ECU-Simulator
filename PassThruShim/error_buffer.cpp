#include "error_buffer.h"
#include <cstring>

// J2534-1 specifies the GetLastError buffer is 80 chars; we use TLS so
// concurrent callers from different threads see their own last error.
static thread_local char g_lastError[80] = "No error.";

void SetLastErrorString(const char* msg)
{
    if (msg == nullptr) { g_lastError[0] = '\0'; return; }
    strncpy_s(g_lastError, sizeof(g_lastError), msg, _TRUNCATE);
}

void CopyLastErrorString(char* dest)
{
    if (dest == nullptr) return;
    strncpy_s(dest, 80, g_lastError, _TRUNCATE);
}
