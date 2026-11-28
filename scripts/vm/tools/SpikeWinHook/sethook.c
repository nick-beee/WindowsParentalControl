// Installs a global WH_GETMESSAGE and a global WH_CBT hook with winhook.dll for the given seconds (as Kid).
// Build on the VM: zig cc -O2 -o sethook.exe sethook.c -luser32. Not part of the product.
#include <windows.h>
#include <stdlib.h>

int main(int argc, char **argv)
{
    int seconds = argc > 2 ? atoi(argv[2]) : 120;
    HMODULE dll = LoadLibraryA(argc > 1 ? argv[1] : "winhook.dll");
    HOOKPROC proc = (HOOKPROC)GetProcAddress(dll, "HookProc");
    HHOOK a = SetWindowsHookExA(WH_GETMESSAGE, proc, dll, 0);
    HHOOK b = SetWindowsHookExA(WH_CBT, proc, dll, 0);
    HANDLE f = CreateFileA("C:\\Users\\Public\\spike\\sethook.log", FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, 0, NULL);
    char line[200];
    DWORD w;
    int n = wsprintfA(line, "dll=%p proc=%p getmessage=%p cbt=%p err=%lu\r\n", dll, proc, a, b, GetLastError());
    WriteFile(f, line, n, &w, NULL);
    CloseHandle(f);
    DWORD end = GetTickCount() + seconds * 1000;
    MSG m;
    while (GetTickCount() < end)
    {
        while (PeekMessageA(&m, NULL, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageA(&m); }
        Sleep(50);
    }
    UnhookWindowsHookEx(a);
    UnhookWindowsHookEx(b);
    return 0;
}
