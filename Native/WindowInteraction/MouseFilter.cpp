#include <windows.h>

// WH_MOUSE runs when the target retrieves native mouse input, including moves
// synthesized by SetCursorPos. Posted, mapped WM_MOUSEMOVE messages bypass it.
// Keep this callback native: it is loaded into the receiving application's GUI
// thread and must never initialize the CLR or call back into the Canvas process.
extern "C" __declspec(dllexport) LRESULT CALLBACK FilterMouse(int code, WPARAM message, LPARAM data)
{
    if (code >= 0 && message == WM_MOUSEMOVE) return 1;
    return CallNextHookEx(nullptr, code, message, data);
}

extern "C" __declspec(dllexport) LRESULT CALLBACK ConfirmLoaded(int code, WPARAM message, LPARAM data)
{
    if (code >= 0)
    {
        const auto call = reinterpret_cast<const CWPSTRUCT*>(data);
        if (call->message == RegisterWindowMessageW(L"Canvas.WindowInteraction.VerifyNativeFilter")
            && static_cast<DWORD>(call->lParam) == GetCurrentThreadId())
        {
            wchar_t name[96]{};
            wsprintfW(name, L"Local\\CanvasInputFilter.%lu.%lu", static_cast<DWORD>(call->wParam), GetCurrentThreadId());
            auto ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, name);
            if (ready) { SetEvent(ready); CloseHandle(ready); }
        }
    }
    return CallNextHookEx(nullptr, code, message, data);
}
