using System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace AnyDictation.App;

/// <summary>プロセスのトークンが昇格済みかの判定。</summary>
internal static class ProcessElevation
{
    /// <summary>貼り付け先ウィンドウのプロセスの権限。API 失敗は NotElevated にせず Unknown として返す。</summary>
    public static unsafe TargetElevation Evaluate(IntPtr hwnd)
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
