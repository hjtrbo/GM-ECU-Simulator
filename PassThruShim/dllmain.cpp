#include <windows.h>

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        OutputDebugStringA("[PassThruShim] DLL_PROCESS_ATTACH\n");
        break;
    case DLL_PROCESS_DETACH:
        OutputDebugStringA("[PassThruShim] DLL_PROCESS_DETACH\n");
        break;
    }
    return TRUE;
}
