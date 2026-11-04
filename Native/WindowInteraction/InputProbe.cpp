#include <windows.h>
#include <iostream>

static int moves;
static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM flags, LPARAM data)
{
    if (message == WM_MOUSEMOVE) ++moves;
    if (message == WM_APP + 1) { const int result = moves; moves = 0; return result; }
    if (message == WM_APP + 2) { SetCapture(window); return GetCapture() == window; }
    if (message == WM_APP + 3) return GetCapture() == window;
    if (message == WM_DESTROY) { ReleaseCapture(); PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, flags, data);
}

int wmain()
{
    WNDCLASSW wc{}; wc.lpfnWndProc = WindowProc; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"CanvasInputProbe";
    if (!RegisterClassW(&wc)) return 1;
    auto window = CreateWindowExW(WS_EX_TOPMOST, wc.lpszClassName, L"Canvas native input test", WS_OVERLAPPEDWINDOW,
        100, 100, 400, 240, nullptr, nullptr, wc.hInstance, nullptr);
    if (!window) return 2;
    ShowWindow(window, SW_SHOW);
    SetForegroundWindow(window);
    std::cout << reinterpret_cast<UINT_PTR>(window) << std::endl;
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    return 0;
}
