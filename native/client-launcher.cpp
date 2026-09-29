#include <windows.h>
#include <filesystem>
#include <iostream>
#include <string>
#include <fstream>

int wmain() {
    auto sbie = GetModuleHandleW(L"SbieDll.dll");
    using QueryProcess = LONG(WINAPI*)(HANDLE, wchar_t*, void*, void*, void*);
    auto query = sbie ? reinterpret_cast<QueryProcess>(GetProcAddress(sbie, "SbieApi_QueryProcess")) : nullptr;
    wchar_t box[64]{};
    if (!query || query(reinterpret_cast<HANDLE>(GetCurrentProcessId()), box, nullptr, nullptr, nullptr) != 0 ||
        (std::wstring(box) != L"SandVnc_A" && std::wstring(box) != L"SandVnc_B")) return 20;
    wchar_t ownPath[32768]{};
    GetModuleFileNameW(nullptr, ownPath, 32768);
    const auto folder = std::filesystem::path(ownPath).parent_path();
    const auto root = folder.parent_path().parent_path();
    const auto clientFolder = root / L"runtime" / L"client";
    const auto executable = clientFolder / L"win32" / L"pxgme32.exe";
    const auto adapter = folder / L"sandvnc-input.dll";
    SetEnvironmentVariableW(L"SANDVNC_WINDOW_CLASS", L"SDL_app");
    std::ofstream log(clientFolder / L"sandvnc-launcher.log", std::ios::app);
    log << "Launcher PID " << GetCurrentProcessId() << "\n" << std::flush;
    std::wstring command = L"\"" + executable.wstring() + L"\"";
    STARTUPINFOW startup{sizeof(startup)};
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_SHOWNOACTIVATE;
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED,
        nullptr, clientFolder.c_str(), &startup, &process)) { log << "CreateProcess error " << GetLastError() << "\n"; return 21; }
    bool resumed = false;
    log << "Child PID " << process.dwProcessId << "\n" << std::flush;
    void* allocation = nullptr;
    auto cleanup = [&]() {
        if (!resumed) TerminateProcess(process.hProcess, 22);
        if (allocation) VirtualFreeEx(process.hProcess, allocation, 0, MEM_RELEASE);
        CloseHandle(process.hThread); CloseHandle(process.hProcess);
    };
    auto remoteCall = [&](LPTHREAD_START_ROUTINE function, void* argument, DWORD& result) {
        HANDLE thread = CreateRemoteThread(process.hProcess, nullptr, 0, function, argument, 0, nullptr);
        if (!thread) { log << "CreateRemoteThread error " << GetLastError() << "\n" << std::flush; return false; }
        bool ok = WaitForSingleObject(thread, 10000) == WAIT_OBJECT_0 && GetExitCodeThread(thread, &result);
        CloseHandle(thread);
        log << "Remote call completed " << ok << " result " << result << "\n" << std::flush;
        return ok;
    };
    const auto path = adapter.wstring();
    SIZE_T bytes = (path.size() + 1) * sizeof(wchar_t), written{};
    allocation = VirtualAllocEx(process.hProcess, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    DWORD module{};
    if (!allocation || !WriteProcessMemory(process.hProcess, allocation, path.c_str(), bytes, &written) || written != bytes ||
        !remoteCall(reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW")), allocation, module) || !module) {
        std::cerr << "Adapter load: " << GetLastError() << "\n"; cleanup(); return 22;
    }
    HMODULE local = LoadLibraryExW(adapter.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
    auto entry = local ? GetProcAddress(local, "_InitializeRemote@4") : nullptr;
    DWORD initialized{};
    auto offset = entry ? reinterpret_cast<uintptr_t>(entry) - reinterpret_cast<uintptr_t>(local) : 0;
    bool ready = offset && remoteCall(reinterpret_cast<LPTHREAD_START_ROUTINE>(uintptr_t(module) + offset), nullptr, initialized) && initialized == 0;
    if (local) FreeLibrary(local);
    if (!ready) { std::cerr << "Adapter initialize: " << initialized << "\n"; cleanup(); return 23; }
    if (ResumeThread(process.hThread) == DWORD(-1)) { cleanup(); return 24; }
    resumed = true;
    log << "Resumed child\n" << std::flush;
    std::cout << "Launched PXG PID " << process.dwProcessId << " in approved box\n";
    cleanup();
    return 0;
}
