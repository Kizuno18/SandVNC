#include <windows.h>
#include <string>

namespace {
std::wstring typed;
POINT lastMouse{};
bool dragging{};
int moves{}, clicks{};
int queueErrors{};
bool recreating{};
LRESULT CALLBACK procedure(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
    switch (message) {
    case WM_KEYDOWN: if (GetKeyState(int(w)) >= 0) ++queueErrors; return 0;
    case WM_KEYUP: if (GetKeyState(int(w)) < 0) ++queueErrors; return 0;
    case WM_APP + 1: {
        recreating = true;
        DestroyWindow(hwnd);
        HWND replacement = CreateWindowExW(WS_EX_NOACTIVATE, L"SandVncInputProbe", L"SandVnc recreated probe",
            WS_OVERLAPPEDWINDOW, 40, 40, 720, 400, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        ShowWindow(replacement, SW_SHOWNOACTIVATE);
        SetTimer(replacement, 1, 100, nullptr);
        recreating = false;
        return 0;
    }
    case WM_CHAR: typed += wchar_t(w); InvalidateRect(hwnd, nullptr, FALSE); return 0;
    case WM_MOUSEMOVE: lastMouse = {LOWORD(l), HIWORD(l)}; ++moves; return 0;
    case WM_LBUTTONDOWN: dragging = true; ++clicks; return 0;
    case WM_LBUTTONUP: dragging = false; return 0;
    case WM_TIMER: InvalidateRect(hwnd, nullptr, FALSE); return 0;
    case WM_PRINTCLIENT:
    case WM_PAINT: {
        PAINTSTRUCT paint{};
        HDC dc = message == WM_PAINT ? BeginPaint(hwnd, &paint) : HDC(w);
        RECT rect{}; GetClientRect(hwnd, &rect);
        FillRect(dc, &rect, GetSysColorBrush(COLOR_WINDOW));
        std::wstring text = L"SYNTHETIC INPUT PROBE - NOT PXG\nPID: " + std::to_wstring(GetCurrentProcessId()) +
            L"\nW held: " + std::to_wstring(GetAsyncKeyState('W') < 0) +
            L"  Ctrl: " + std::to_wstring(GetAsyncKeyState(VK_CONTROL) < 0) +
            L"  Shift: " + std::to_wstring(GetKeyState(VK_SHIFT) < 0) +
            L"\nMouse: " + std::to_wstring(lastMouse.x) + L", " + std::to_wstring(lastMouse.y) +
            L"  Dragging: " + std::to_wstring(dragging) + L"\nClicks: " + std::to_wstring(clicks) +
            L"  Moves: " + std::to_wstring(moves) + L"\nQueue state errors: " + std::to_wstring(queueErrors) + L"\nTyped: " + typed;
        rect.left += 20; rect.top += 20;
        DrawTextW(dc, text.c_str(), -1, &rect, DT_LEFT | DT_TOP);
        if (message == WM_PAINT) EndPaint(hwnd, &paint);
        return 0;
    }
    case WM_DESTROY: if (!recreating) PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(hwnd, message, w, l);
}
}
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    wchar_t path[32768]{};
    GetModuleFileNameW(nullptr, path, 32768);
    std::wstring dll = path;
    dll = dll.substr(0, dll.find_last_of(L"\\") + 1) + L"sandvnc-input.dll";
    HMODULE adapter = LoadLibraryW(dll.c_str());
    if (!adapter) return 2;
    auto initialize = reinterpret_cast<void(WINAPI*)(HMODULE, ULONG)>(GetProcAddress(adapter, "_InjectDllMain@8"));
    if (!initialize) initialize = reinterpret_cast<void(WINAPI*)(HMODULE, ULONG)>(GetProcAddress(adapter, "InjectDllMain"));
    if (!initialize) return 3;
    initialize(adapter, 0);
    WNDCLASSW wc{}; wc.lpfnWndProc = procedure; wc.hInstance = instance;
    wc.lpszClassName = L"SandVncInputProbe"; wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    RegisterClassW(&wc);
    HWND hwnd = CreateWindowExW(WS_EX_NOACTIVATE, wc.lpszClassName, L"SandVnc synthetic probe",
        WS_OVERLAPPEDWINDOW, 40, 40, 720, 400, nullptr, nullptr, instance, nullptr);
    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    SetTimer(hwnd, 1, 100, nullptr);
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    return 0;
}
