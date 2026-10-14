using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>Consumes skip keys through key-up, including repeats and delayed raw mouse signals.</summary>
internal sealed class StartupIntroInputFilter : IMessageFilter
{
    private readonly Func<bool> _skip;
    private readonly Func<int, bool> _isDown;
    private readonly Func<long> _now;
    private readonly HashSet<int> _held = new();
    private long _doubleClickUntil;
    private int _doubleClickButton;

    public StartupIntroInputFilter(Func<bool> skip, Func<int, bool>? isDown = null, Func<long>? now = null)
    {
        _skip = skip;
        _isDown = isDown ?? (key => (PInvoke.GetAsyncKeyState(key) & 0x8000) != 0);
        _now = now ?? (() => Environment.TickCount64);
    }

    public void CaptureHeldInput()
    {
        for (int key = 1; key < 256; key++)
            if (_isDown(key)) _held.Add(key);
    }

    public bool HasPendingGesture
    {
        get
        {
            // Key-up may be routed to another application after Alt+Tab.
            _held.RemoveWhere(key => !_isDown(key));
            return _held.Count > 0 || _now() < _doubleClickUntil;
        }
    }

    public bool PreFilterMessage(ref Message m)
    {
        int key = InputKey(m);
        if (IsRelease(m.Msg)) return key != 0 && _held.Remove(key);
        if (IsDoubleClick(m.Msg) && key == _doubleClickButton && _now() < _doubleClickUntil) return true;
        if (!IsSkipMessage(m.Msg)) return false;
        if (key != 0 && _held.Contains(key))
        {
            // Raw input may have skipped the intro before this form message arrives.
            ArmMouseDoubleClick(m.Msg, key);
            return true;
        }
        if (!_skip()) return false;
        if (key != 0) _held.Add(key);
        ArmMouseDoubleClick(m.Msg, key);
        return true;
    }

    private void ArmMouseDoubleClick(int message, int button)
    {
        if (message is not (0x0201 or 0x0203 or 0x0204 or 0x0206 or 0x0207 or 0x0209 or 0x020B or 0x020D)) return;
        _doubleClickButton = button;
        _doubleClickUntil = _now() + SystemInformation.DoubleClickTime;
    }

    private static bool IsRelease(int message) => message is 0x0101 or 0x0105 or 0x0202 or 0x0205 or 0x0208 or 0x020C;
    private static bool IsDoubleClick(int message) => message is 0x0203 or 0x0206 or 0x0209 or 0x020D;
    private static int InputKey(Message message) => message.Msg switch
    {
        0x0100 or 0x0101 or 0x0104 or 0x0105 => (int)message.WParam,
        0x0201 or 0x0202 or 0x0203 => 1,
        0x0204 or 0x0205 or 0x0206 => 2,
        0x0207 or 0x0208 or 0x0209 => 4,
        0x020B or 0x020C or 0x020D => ((message.WParam.ToInt64() >> 16) & 0xFFFF) == 1 ? 5 : 6,
        _ => 0
    };

    internal static bool IsSkipMessage(int message) => message is
        0x0100 or 0x0104 or
        0x0201 or 0x0203 or 0x0204 or 0x0206 or 0x0207 or 0x0209 or
        0x020A or 0x020B or 0x020D or 0x020E or 0x0312;
}
