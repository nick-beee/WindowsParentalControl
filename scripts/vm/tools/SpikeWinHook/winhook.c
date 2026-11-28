// Task 1.3 of session-probe-and-agent-channel: a hook DLL of Kid. Each process that loads it writes a line to
// C:\Users\Public\spike\winhook.log. Build on the VM: zig cc -shared -O2 -o winhook.dll winhook.c -luser32
// Not part of the product.
#include <windows.h>

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID r)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        char path[MAX_PATH];
        char line[700];
        DWORD written;
        GetModuleFileNameA(NULL, path, MAX_PATH);
        HANDLE f = CreateFileA("C:\\Users\\Public\\spike\\winhook.log", FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               NULL, OPEN_ALWAYS, 0, NULL);
        if (f != INVALID_HANDLE_VALUE)
        {
            int n = wsprintfA(line, "loaded in pid %lu %s\r\n", GetCurrentProcessId(), path);
            WriteFile(f, line, n, &written, NULL);
            CloseHandle(f);
        }
    }
    return TRUE;
}

__declspec(dllexport) LRESULT CALLBACK HookProc(int code, WPARAM w, LPARAM l)
{
    return CallNextHookEx(NULL, code, w, l);
}
