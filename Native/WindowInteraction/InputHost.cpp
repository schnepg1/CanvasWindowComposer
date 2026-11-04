#include <windows.h>
#include <iostream>
#include <string>

static DWORD WINAPI WaitForOwner(void* state)
{
    char value{}; DWORD read{};
    ReadFile(GetStdHandle(STD_INPUT_HANDLE), &value, 1, &read, nullptr);
    SetEvent(*static_cast<HANDLE*>(state));
    return 0;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 4) return 1;
    const auto threadId = static_cast<DWORD>(wcstoul(argv[1], nullptr, 10));
    const auto processId = static_cast<DWORD>(wcstoul(argv[2], nullptr, 10));
    const auto window = reinterpret_cast<HWND>(static_cast<UINT_PTR>(wcstoull(argv[3], nullptr, 10)));
    if (GetWindowThreadProcessId(window, nullptr) != threadId) return 2;
    auto thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, threadId);
    if (!thread || GetProcessIdOfThread(thread) != processId) { if (thread) CloseHandle(thread); return 2; }
    wchar_t modulePath[MAX_PATH]{};
    const auto length = GetModuleFileNameW(nullptr, modulePath, MAX_PATH);
    if (!length || length >= MAX_PATH) return 3;
    std::wstring path(modulePath);
    path.resize(path.find_last_of(L"\\/") + 1);
    path += L"CanvasMouseFilter.dll";
    // Load only our adjacent DLL and system dependencies, never the target's
    // or the launching process's current working directory.
    auto module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) return 4;
    auto callback = reinterpret_cast<HOOKPROC>(GetProcAddress(module, "FilterMouse"));
    // x86 stdcall exports are decorated; x64 exports are not.
    if (!callback) callback = reinterpret_cast<HOOKPROC>(GetProcAddress(module, "_FilterMouse@12"));
    auto hook = callback ? SetWindowsHookExW(WH_MOUSE, callback, module, threadId) : nullptr;
    if (!hook) { FreeLibrary(module); return 5; }
    auto confirmation = reinterpret_cast<HOOKPROC>(GetProcAddress(module, "ConfirmLoaded"));
    if (!confirmation) confirmation = reinterpret_cast<HOOKPROC>(GetProcAddress(module, "_ConfirmLoaded@12"));
    wchar_t eventName[96]{};
    wsprintfW(eventName, L"Local\\CanvasInputFilter.%lu.%lu", GetCurrentProcessId(), threadId);
    auto loaded = CreateEventW(nullptr, TRUE, FALSE, eventName);
    auto check = confirmation ? SetWindowsHookExW(WH_CALLWNDPROC, confirmation, module, threadId) : nullptr;
    DWORD_PTR result{};
    bool verified = loaded && check && SendMessageTimeoutW(window,
        RegisterWindowMessageW(L"Canvas.WindowInteraction.VerifyNativeFilter"), GetCurrentProcessId(), threadId,
        SMTO_ABORTIFHUNG | SMTO_BLOCK, 1000, &result) && WaitForSingleObject(loaded, 0) == WAIT_OBJECT_0;
    if (check) UnhookWindowsHookEx(check);
    if (loaded) CloseHandle(loaded);
    if (!verified) { UnhookWindowsHookEx(hook); FreeLibrary(module); CloseHandle(thread); return 6; }
    auto ownerClosed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    auto reader = ownerClosed ? CreateThread(nullptr, 0, WaitForOwner, &ownerClosed, 0, nullptr) : nullptr;
    if (!reader) { UnhookWindowsHookEx(hook); FreeLibrary(module); CloseHandle(thread); if (ownerClosed) CloseHandle(ownerClosed); return 7; }
    std::cout << "ready" << std::endl;
    // EOF is the ownership token. Closing/disposal or a Canvas crash closes the
    // anonymous pipe, so the helper always removes its thread-scoped hook.
    HANDLE handles[] = { ownerClosed, thread };
    WaitForMultipleObjects(2, handles, FALSE, INFINITE);
    UnhookWindowsHookEx(hook);
    CancelSynchronousIo(reader);
    WaitForSingleObject(reader, INFINITE);
    CloseHandle(reader); CloseHandle(ownerClosed); CloseHandle(thread);
    FreeLibrary(module);
    return 0;
}
