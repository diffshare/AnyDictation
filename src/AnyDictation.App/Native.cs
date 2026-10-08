using System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AnyDictation.App;

/// <summary>Win32 呼び出しの集約。P/Invoke は Microsoft.Windows.CsWin32(NativeMethods.txt)が生成する <see cref="PInvoke"/> を使う。</summary>
internal static class Native
{
    public const ushort VK_CONTROL = 0x11, VK_V = 0x56, VK_RETURN = 0x0D, VK_MASK = 0xE8;
    public const ushort VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    /// <summary>自身が SendInput したイベントを識別する dwExtraInfo。フックはこの値を持つイベントを無視する。</summary>
    public static readonly UIntPtr OwnMarker = new(0x414E5944); // "ANYD"

    public static IntPtr GetForegroundWindow() => PInvoke.GetForegroundWindow().Value;

    /// <summary>ウィンドウを非アクティブのツールウィンドウにする(フォーカスを奪わず、Alt+Tab に出さない)。</summary>
    public static void MakeNoActivateToolWindow(IntPtr hwnd)
    {
        var window = new HWND(hwnd);
        int style = PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            style | (int)(WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW));
    }

    public static void DestroyIcon(IntPtr handle) => PInvoke.DestroyIcon(new HICON(handle));

    public static bool IsPhysicallyDown(int vk) => (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0;

    public static bool AnyModifierDown() =>
        IsPhysicallyDown(0xA2) || IsPhysicallyDown(0xA3) || IsPhysicallyDown(VK_LWIN) || IsPhysicallyDown(VK_RWIN)
        || IsPhysicallyDown(0xA0) || IsPhysicallyDown(0xA1) || IsPhysicallyDown(0xA4) || IsPhysicallyDown(0xA5);

    static INPUT Key(ushort vk, bool up, UIntPtr marker) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            ki = new KEYBDINPUT { wVk = (VIRTUAL_KEY)vk, dwFlags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0, dwExtraInfo = marker },
        },
    };

    static unsafe bool Send(params INPUT[] inputs) => PInvoke.SendInput(inputs, sizeof(INPUT)) == inputs.Length;

    /// <summary>Win メニューを抑制するための未割り当てキー(0xE8)の押下と解放。marker は受け取るフックが自分の送出として無視する印。</summary>
    public static bool SendMaskKey(UIntPtr marker) => Send(Key(VK_MASK, false, marker), Key(VK_MASK, true, marker));

    public static bool SendCtrlV() => Send(Key(VK_CONTROL, false, OwnMarker), Key(VK_V, false, OwnMarker), Key(VK_V, true, OwnMarker), Key(VK_CONTROL, true, OwnMarker));

    public static bool SendEnter() => Send(Key(VK_RETURN, false, OwnMarker), Key(VK_RETURN, true, OwnMarker));

    /// <summary>貼り付け先ウィンドウのプロセスの権限。API 失敗は NotElevated にせず Unknown として返す。</summary>
    public static unsafe TargetElevation EvaluateElevation(IntPtr hwnd)
    {
        bool? we = TryIsProcessElevated(Environment.ProcessId);
        bool? target = null;
        uint pid = 0;
        if (hwnd != IntPtr.Zero && PInvoke.GetWindowThreadProcessId(new HWND(hwnd), &pid) != 0 && pid != 0)
            target = TryIsProcessElevated((int)pid);
        return ElevationPolicy.Evaluate(we, target);
    }

    /// <summary>プロセスのトークンが昇格済みか。判定 API が失敗したら null。</summary>
    static unsafe bool? TryIsProcessElevated(int pid)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process.IsNull) return null;
        try
        {
            HANDLE token;
            if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, &token)) return null;
            try
            {
                int elevated = 0;
                uint returned = 0;
                if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevation, &elevated, sizeof(int), &returned)) return null;
                return elevated != 0;
            }
            finally
            {
                PInvoke.CloseHandle(token);
            }
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }
}
