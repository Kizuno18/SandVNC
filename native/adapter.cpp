#include <windows.h>
#include <MinHook.h>
#include <array>
#include <atomic>
#include <deque>
#include <mutex>
#include <string>
#include <fstream>
#include "protocol.h"

namespace {
std::mutex gate;
std::array<BYTE, 256> keys{};
std::array<BYTE, 256> messageKeys{};
struct Event { UINT message; WPARAM w; LPARAM l; uint64_t epoch; std::array<BYTE, 256> keyboard{}; };
std::deque<Event> events;
HWND target{};
bool bound{};
ULONGLONG candidateSince{};
DWORD uiThread{};
POINT cursor{};
uint64_t epoch{}, sequence{}, applied{};
std::atomic<ULONGLONG> lastPulse{};
std::atomic<uint32_t> failure{};
std::atomic<bool> initialized{};
std::atomic<bool> hooksReady{};
std::atomic<bool> stop{};
constexpr UINT wakeMessage = WM_APP + 0x63;
decltype(&PeekMessageW) originalPeekW;
decltype(&PeekMessageA) originalPeekA;
decltype(&GetMessageW) originalGetW;
decltype(&GetMessageA) originalGetA;
decltype(&DestroyWindow) originalDestroy;

bool own(HWND hwnd) {
    DWORD pid{};
    return hwnd && GetWindowThreadProcessId(hwnd, &pid) && pid == GetCurrentProcessId();
}
bool physical(UINT message) {
    return (message >= WM_KEYFIRST && message <= WM_KEYLAST) ||
           (message >= WM_MOUSEFIRST && message <= WM_MOUSELAST) ||
           (message >= WM_NCMOUSEMOVE && message <= WM_NCXBUTTONDBLCLK) ||
           message == WM_INPUT || message == WM_INPUT_DEVICE_CHANGE ||
           (message >= WM_POINTERUPDATE && message <= WM_POINTERHWHEEL) || message == WM_TOUCH;
}
void releaseLocked() {
    events.clear();
    for (unsigned i = 0; i < keys.size(); ++i) {
        if (!keys[i]) continue;
        if (i == VK_LBUTTON || i == VK_RBUTTON || i == VK_MBUTTON) {
            UINT msg = i == VK_LBUTTON ? WM_LBUTTONUP : i == VK_RBUTTON ? WM_RBUTTONUP : WM_MBUTTONUP;
            events.push_back({msg, 0, MAKELPARAM(cursor.x, cursor.y), epoch});
        } else {
            auto scan = MapVirtualKeyW(i, MAPVK_VK_TO_VSC);
            events.push_back({WM_KEYUP, i, LPARAM(1 | (scan << 16) | 0xC0000000u), epoch});
        }
    }
    keys.fill(0);
    messageKeys.fill(0);
    if (uiThread) PostThreadMessageW(uiThread, wakeMessage, 0, 0);
}
SHORT WINAPI virtualKey(int key) {
    std::lock_guard lock(gate);
    return key >= 0 && key < 256 && keys[key] ? SHORT(0x8000) : 0;
}
SHORT WINAPI virtualMessageKey(int key) {
    std::lock_guard lock(gate);
    return key >= 0 && key < 256 && messageKeys[key] ? SHORT(0x8000) : 0;
}
BOOL WINAPI virtualKeyboard(PBYTE data) {
    if (!data) return FALSE;
    std::lock_guard lock(gate);
    std::copy(messageKeys.begin(), messageKeys.end(), data);
    return TRUE;
}
BOOL WINAPI virtualCursor(LPPOINT point) {
    if (!point) return FALSE;
    std::lock_guard lock(gate);
    *point = cursor;
    return target && ClientToScreen(target, point);
}
BOOL WINAPI setVirtualCursor(int x, int y) {
    std::lock_guard lock(gate);
    POINT point{x, y};
    if (!target || !ScreenToClient(target, &point)) return FALSE;
    cursor = point;
    return TRUE;
}
HWND WINAPI virtualForeground() { std::lock_guard lock(gate); return target; }
BOOL WINAPI setVirtualForeground(HWND hwnd) { return own(hwnd); }
HWND WINAPI setVirtualFocus(HWND hwnd) { return own(hwnd) ? virtualForeground() : nullptr; }
BOOL WINAPI releaseVirtualCapture() { return TRUE; }
BOOL WINAPI clipVirtualCursor(const RECT*) { return TRUE; }
BOOL WINAPI translateVirtual(const MSG*) { return FALSE; }
BOOL WINAPI denyClipboard(HWND) { SetLastError(ERROR_ACCESS_DENIED); return FALSE; }
HANDLE WINAPI readClipboard(UINT) { SetLastError(ERROR_ACCESS_DENIED); return nullptr; }
HANDLE WINAPI writeClipboard(UINT, HANDLE) { SetLastError(ERROR_ACCESS_DENIED); return nullptr; }
BOOL WINAPI rawRegistration(PCRAWINPUTDEVICE devices, UINT count, UINT size) {
    std::ofstream log("sandvnc-raw-registration.log", std::ios::app);
    if (devices && size == sizeof(RAWINPUTDEVICE)) {
        for (UINT i = 0; i < count; ++i) log << devices[i].usUsagePage << "," << devices[i].usUsage << "," << devices[i].dwFlags << "\n";
    }
    failure = 3;
    SetLastError(ERROR_NOT_SUPPORTED);
    return FALSE;
}
UINT WINAPI rawData(HRAWINPUT, UINT, LPVOID, PUINT, UINT) {
    failure = 3;
    SetLastError(ERROR_NOT_SUPPORTED);
    return UINT(-1);
}
BOOL WINAPI destroyVirtual(HWND hwnd) {
    {
        std::lock_guard lock(gate);
        if (hwnd == target) { if (bound) failure = 4; releaseLocked(); target = nullptr; uiThread = 0; }
    }
    return originalDestroy(hwnd);
}
bool popRemote(MSG* message, bool remove, HWND filter, UINT min, UINT max) {
    std::lock_guard lock(gate);
    if (GetCurrentThreadId() != uiThread || !target || (filter && filter != target)) return false;
    while (!events.empty() && events.front().epoch != epoch) events.pop_front();
    if (events.empty()) return false;
    const auto event = events.front();
    if ((min || max) && (event.message < min || event.message > max)) return false;
    *message = {target, event.message, event.w, event.l, GetTickCount(), cursor};
    if (remove) { messageKeys = event.keyboard; events.pop_front(); ++applied; }
    return true;
}
template <typename Peek>
BOOL peekVirtual(Peek original, MSG* msg, HWND hwnd, UINT min, UINT max, UINT flags) {
    if (popRemote(msg, (flags & PM_REMOVE) != 0, hwnd, min, max)) return TRUE;
    while (original(msg, hwnd, min, max, flags)) {
        if (!physical(msg->message) && msg->message != wakeMessage) return TRUE;
        if (!(flags & PM_REMOVE)) {
            MSG discarded{};
            original(&discarded, hwnd, msg->message, msg->message, PM_REMOVE);
        }
    }
    return FALSE;
}
BOOL WINAPI peekW(MSG* m, HWND h, UINT lo, UINT hi, UINT flags) {
    return peekVirtual(originalPeekW, m, h, lo, hi, flags);
}
BOOL WINAPI peekA(MSG* m, HWND h, UINT lo, UINT hi, UINT flags) {
    return peekVirtual(originalPeekA, m, h, lo, hi, flags);
}
template <typename Get>
BOOL getVirtual(Get original, MSG* m, HWND h, UINT lo, UINT hi) {
    for (;;) {
        if (popRemote(m, true, h, lo, hi)) return TRUE;
        BOOL result = original(m, h, lo, hi);
        if (result <= 0) return result;
        if (!physical(m->message) && m->message != wakeMessage) return result;
    }
}
BOOL WINAPI getW(MSG* m, HWND h, UINT lo, UINT hi) { return getVirtual(originalGetW, m, h, lo, hi); }
BOOL WINAPI getA(MSG* m, HWND h, UINT lo, UINT hi) { return getVirtual(originalGetA, m, h, lo, hi); }
BOOL CALLBACK findWindow(HWND hwnd, LPARAM) {
    if (!own(hwnd) || !IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER)) return TRUE;
    wchar_t expected[256]{}, actual[256]{};
    if (GetEnvironmentVariableW(L"SANDVNC_WINDOW_CLASS", expected, 256)) {
        GetClassNameW(hwnd, actual, 256);
        std::wstring className(actual);
        if (className != expected && !className.ends_with(L":" + std::wstring(expected))) return TRUE;
    }
    std::lock_guard lock(gate);
    if (!target && !failure) {
        target = hwnd;
        candidateSince = GetTickCount64();
        uiThread = GetWindowThreadProcessId(hwnd, nullptr);
    }
    return FALSE;
}
Reply state(uint32_t status) {
    std::lock_guard lock(gate);
    RECT rect{};
    if (target) GetClientRect(target, &rect);
    Reply reply{};
    reply.status = failure ? failure.load() : status;
    reply.epoch = epoch;
    reply.sequence = sequence;
    reply.pid = GetCurrentProcessId();
    reply.hwnd = uint32_t(reinterpret_cast<uintptr_t>(target));
    reply.width = rect.right;
    reply.height = rect.bottom;
    for (auto value : keys) if (value) ++reply.keys;
    reply.modifiers = (keys[VK_SHIFT] ? 1 : 0) | (keys[VK_CONTROL] ? 2 : 0) | (keys[VK_MENU] ? 4 : 0);
    reply.x = cursor.x; reply.y = cursor.y; reply.applied = applied;
    return reply;
}
uint32_t accept(const Command& command) {
    std::lock_guard lock(gate);
    if (command.magic != protocolMagic) return 10;
    if (command.operation == Operation::Reset) {
        if (command.epoch <= epoch) return 11;
        epoch = command.epoch; sequence = command.sequence;
        releaseLocked(); lastPulse = GetTickCount64();
        return 0;
    }
    if (command.epoch != epoch || command.sequence <= sequence) return 11;
    sequence = command.sequence;
    if (failure || !target) return failure ? failure.load() : 1;
    if (command.operation == Operation::Move || command.operation == Operation::Button) {
        RECT r{};
        if (!GetClientRect(target, &r) || r.right != command.width || r.bottom != command.height) return 14;
    }
    if (events.size() >= 256) { releaseLocked(); return 12; }
    auto add = [&](UINT message, WPARAM w, LPARAM l) { events.push_back({message, w, l, epoch, keys}); };
    auto buttons = [&]() -> WPARAM {
        return (keys[VK_LBUTTON] ? MK_LBUTTON : 0) | (keys[VK_RBUTTON] ? MK_RBUTTON : 0) |
               (keys[VK_MBUTTON] ? MK_MBUTTON : 0) | (keys[VK_SHIFT] ? MK_SHIFT : 0) |
               (keys[VK_CONTROL] ? MK_CONTROL : 0);
    };
    switch (command.operation) {
    case Operation::Key: {
        if (command.a < 8 || command.a > 165 || (command.b != 0 && command.b != 1)) return 13;
        const bool previous = keys[command.a] != 0;
        keys[command.a] = command.b ? 0x80 : 0;
        auto scan = MapVirtualKeyW(command.a, MAPVK_VK_TO_VSC);
        uint32_t bits = 1 | (scan << 16) | (previous ? 0x40000000u : 0) | (!command.b ? 0x80000000u : 0);
        if (command.a >= VK_PRIOR && command.a <= VK_DELETE) bits |= 1 << 24;
        add(command.b ? WM_KEYDOWN : WM_KEYUP, command.a, bits);
        break;
    }
    case Operation::Text:
        if (command.a < 32 || command.a > 65535) return 13;
        add(WM_CHAR, command.a, 1); break;
    case Operation::Move: {
        RECT r{}; GetClientRect(target, &r);
        if (command.a < 0 || command.b < 0 || command.a >= r.right || command.b >= r.bottom) return 13;
        cursor = {command.a, command.b};
        add(WM_MOUSEMOVE, buttons(), MAKELPARAM(cursor.x, cursor.y)); break;
    }
    case Operation::Button: {
        if (command.a < 0 || command.a > 2 || (command.b != 0 && command.b != 1)) return 13;
        const int vk[] = {VK_LBUTTON, VK_MBUTTON, VK_RBUTTON};
        const UINT down[] = {WM_LBUTTONDOWN, WM_MBUTTONDOWN, WM_RBUTTONDOWN};
        const UINT up[] = {WM_LBUTTONUP, WM_MBUTTONUP, WM_RBUTTONUP};
        keys[vk[command.a]] = command.b ? 0x80 : 0;
        add(command.b ? down[command.a] : up[command.a], buttons(), MAKELPARAM(cursor.x, cursor.y)); break;
    }
    case Operation::Pulse: break;
    case Operation::Status: break;
    default: return 13;
    }
    lastPulse = GetTickCount64();
    if (!events.empty()) PostThreadMessageW(uiThread, wakeMessage, 0, 0);
    return 0;
}
DWORD WINAPI watchdog(void*) {
    while (!stop) {
        Sleep(100);
        std::lock_guard lock(gate);
        if (lastPulse && GetTickCount64() - lastPulse > 2000) { releaseLocked(); lastPulse = 0; }
    }
    return 0;
}
template <typename Function>
bool hook(const char* name, Function replacement, void** original = nullptr) {
    return MH_CreateHookApi(L"user32", name, reinterpret_cast<void*>(replacement), original) == MH_OK;
}
DWORD WINAPI start(void*) {
    wchar_t pipeName[256]{};
    if (!GetEnvironmentVariableW(L"SANDVNC_PIPE", pipeName, 256)) return 0;
    const std::wstring path = L"\\\\.\\pipe\\" + std::wstring(pipeName);
    if (MH_Initialize() != MH_OK) return 0;
    bool ok = hook("GetAsyncKeyState", virtualKey) && hook("GetKeyState", virtualMessageKey) &&
        hook("GetKeyboardState", virtualKeyboard) && hook("GetCursorPos", virtualCursor) &&
        hook("SetCursorPos", setVirtualCursor) && hook("GetForegroundWindow", virtualForeground) &&
        hook("GetFocus", virtualForeground) && hook("SetForegroundWindow", setVirtualForeground) &&
        hook("SetFocus", setVirtualFocus) && hook("GetCapture", virtualForeground) &&
        hook("SetCapture", setVirtualFocus) && hook("ReleaseCapture", releaseVirtualCapture) &&
        hook("ClipCursor", clipVirtualCursor) && hook("TranslateMessage", translateVirtual) &&
        hook("OpenClipboard", denyClipboard) && hook("GetClipboardData", readClipboard) && hook("SetClipboardData", writeClipboard) &&
        hook("RegisterRawInputDevices", rawRegistration) && hook("GetRawInputData", rawData) &&
        hook("DestroyWindow", destroyVirtual, reinterpret_cast<void**>(&originalDestroy)) &&
        hook("PeekMessageW", peekW, reinterpret_cast<void**>(&originalPeekW)) &&
        hook("PeekMessageA", peekA, reinterpret_cast<void**>(&originalPeekA)) &&
        hook("GetMessageW", getW, reinterpret_cast<void**>(&originalGetW)) &&
        hook("GetMessageA", getA, reinterpret_cast<void**>(&originalGetA));
    if (!ok || MH_EnableHook(MH_ALL_HOOKS) != MH_OK) { failure = 2; return 0; }
    hooksReady = true;
    CloseHandle(CreateThread(nullptr, 0, watchdog, nullptr, 0, nullptr));
    for (;;) {
        { std::lock_guard lock(gate); if (failure || (target && GetTickCount64() - candidateSince >= 750)) { bound = true; break; } }
        EnumWindows(findWindow, 0); Sleep(50);
    }
    if (!WaitNamedPipeW(path.c_str(), 15000)) return 0;
    HANDLE pipe = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) return 0;
    DWORD count{};
    auto hello = state(0);
    if (!WriteFile(pipe, &hello, sizeof(hello), &count, nullptr)) { CloseHandle(pipe); return 0; }
    Command command{};
    auto readCommand = [&]() {
        DWORD total = 0;
        while (total < sizeof(command)) {
            if (!ReadFile(pipe, reinterpret_cast<BYTE*>(&command) + total, sizeof(command) - total, &count, nullptr) || !count) return false;
            total += count;
        }
        return true;
    };
    while (readCommand()) {
        auto reply = state(accept(command));
        if (!WriteFile(pipe, &reply, sizeof(reply), &count, nullptr)) break;
    }
    { std::lock_guard lock(gate); releaseLocked(); }
    CloseHandle(pipe);
    stop = true;
    return 0;
}
}
extern "C" __declspec(dllexport) void WINAPI InjectDllMain(HMODULE, ULONG) {
    if (!initialized.exchange(true)) CloseHandle(CreateThread(nullptr, 0, start, nullptr, 0, nullptr));
}
extern "C" __declspec(dllexport) DWORD WINAPI InitializeRemote(void*) {
    InjectDllMain(nullptr, 0);
    for (int attempt = 0; attempt < 500 && !hooksReady && !failure; ++attempt) Sleep(10);
    return hooksReady ? 0 : (failure ? failure.load() : 30);
}
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module);
    return TRUE;
}
