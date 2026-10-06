using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AnyDictation.App;

/// <summary>Toggled 以外のフックからの要求。Cancel / Submit は録音中の Esc / Enter、Repaste は Shift+Alt+Z。</summary>
internal enum HookAction { HoldStart, HoldEnd, Cancel, Submit, Repaste }

/// <summary>
/// WH_KEYBOARD_LL で Ctrl+Win を監視する。Ctrl+Win 関連のイベントは抑制せず、自身の SendInput(OwnMarker)は無視する。
/// 抑制するのは録音中の Esc と Enter(と、その up)だけ。Shift+Alt+Z は同じスレッドの RegisterHotKey で受ける。
/// フックは UI スレッドから切り離した専用スレッド(メッセージループ付き)に置く。UI スレッドが数秒止まっても、
/// Windows がコールバックの timeout でフックを黙って外す(以後キーが届かない)ことを避けるため。
/// <see cref="Toggled"/> と <see cref="Triggered"/> はそのスレッドで同期的に呼ばれるので、購読側は軽く保ち、重い処理は自分のスレッドへ投げること。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    const uint ResetMessage = Native.WM_APP + 1;
    const int RepasteHotkeyId = 1;
    const uint VkZ = 0x5A;

    readonly HotkeyDetector _detector = new(); // フックスレッドだけが触る
    readonly RecordingKeyFilter _keys = new(); // フックスレッドだけが触る
    readonly bool _repasteHotkey;
    volatile bool _recording; // 録音中か。UI スレッドが書き、フックスレッドが読む
    UIntPtr _holdTimer; // 長押し判定のスレッドタイマー(フックスレッドだけが触る)
    readonly Native.LowLevelKeyboardProc _proc; // GC されないようフィールドで保持
    readonly UIntPtr _ignoredMarker;
    readonly Action _sendMask;
    Thread? _thread;
    volatile uint _threadId;
    IntPtr _hook;

    public event Action? Toggled;
    public event Action<HookAction>? Triggered;

    /// <summary>録音中の間だけ true にする。true の間、Esc と Enter を捕捉する。</summary>
    public bool Recording { set => _recording = value; }

    public KeyboardHook() : this(Native.OwnMarker, () => Native.SendMaskKey(Native.OwnMarker), repasteHotkey: true) { }

    /// <summary>
    /// ignoredMarker を持つ入力は自分の送出として無視する。テストが本番のフックと干渉しないよう印を差し替えるために公開している。
    /// テストは Shift+Alt+Z を登録しない(既定)。
    /// </summary>
    public KeyboardHook(UIntPtr ignoredMarker, Action sendMask, bool repasteHotkey = false)
    {
        _ignoredMarker = ignoredMarker;
        _sendMask = sendMask;
        _repasteHotkey = repasteHotkey;
        _proc = Callback;
    }

    /// <summary>フックスレッドを起動し、設定の成否が分かるまで待つ。設定できなければ例外を投げる。</summary>
    public void Install()
    {
        if (_thread != null) throw new InvalidOperationException("キーボードフックは設定済みです。");
        using var ready = new ManualResetEventSlim();
        string? error = null;
        var thread = new Thread(() => Run(ready, ref error)) { IsBackground = true, Name = "AnyDictation.KeyboardHook" };
        thread.Start();
        ready.Wait();
        if (error != null)
        {
            thread.Join();
            throw new InvalidOperationException(error);
        }
        _thread = thread;
    }

    void Run(ManualResetEventSlim ready, ref string? error)
    {
        try
        {
            Native.PeekMessage(out _, IntPtr.Zero, 0, 0, Native.PM_NOREMOVE); // PostThreadMessage の宛先になるメッセージキューを作る
            using var module = Process.GetCurrentProcess().MainModule!;
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(module.ModuleName), 0);
            if (_hook == IntPtr.Zero)
            {
                error = $"キーボードフックを設定できません(Win32 エラー {Marshal.GetLastWin32Error()})。";
                return;
            }
            _threadId = Native.GetCurrentThreadId();
            // 他のアプリが先に登録していれば失敗する。その場合も録音の操作は使えるので、ログだけ残す
            if (_repasteHotkey && !Native.RegisterHotKey(IntPtr.Zero, RepasteHotkeyId, Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, VkZ))
                Log.Write($"repaste hotkey not registered win32={Marshal.GetLastWin32Error()}");
        }
        catch (Exception e)
        {
            // 呼び出し元へ失敗として伝える。バックグラウンドスレッドの未処理例外でプロセスを落とさない
            error = $"キーボードフックを初期化できません({e.GetType().Name})。";
            Unhook();
            return;
        }
        finally
        {
            ready.Set();
        }
        Log.Write("keyboard hook started");
        try
        {
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == ResetMessage)
                {
                    _detector.Reset();
                    _keys.Reset();
                    StopHoldTimer();
                }
                else if (msg.message == Native.WM_HOTKEY) Triggered?.Invoke(HookAction.Repaste);
                else if (msg.message == Native.WM_TIMER && msg.wParam == _holdTimer)
                {
                    StopHoldTimer();
                    if (_detector.Tick(NowMs()).HoldStart) Triggered?.Invoke(HookAction.HoldStart);
                }
            }
        }
        finally
        {
            if (_repasteHotkey) Native.UnregisterHotKey(IntPtr.Zero, RepasteHotkeyId);
            StopHoldTimer();
            Unhook();
            Log.Write("keyboard hook stopped");
        }
    }

    static long NowMs() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <summary>Ctrl+Win が揃った時点から長押しのしきい値の後に判定する。タイマーの刻みで早く届かないよう少し余裕を持たせる。</summary>
    void StartHoldTimer()
    {
        StopHoldTimer();
        _holdTimer = Native.SetTimer(IntPtr.Zero, UIntPtr.Zero, (uint)HotkeyDetector.HoldThresholdMs + 20, IntPtr.Zero);
    }

    void StopHoldTimer()
    {
        if (_holdTimer == UIntPtr.Zero) return;
        Native.KillTimer(IntPtr.Zero, _holdTimer);
        _holdTimer = UIntPtr.Zero;
    }

    void Unhook()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>セッション切替などで状態が不確かになったとき用。どのスレッドからでも呼べ、フックスレッドで実行される(非同期)。</summary>
    public void Reset()
    {
        uint id = _threadId;
        if (id != 0 && !Native.PostThreadMessage(id, ResetMessage, UIntPtr.Zero, IntPtr.Zero))
            Log.Write($"keyboard hook reset not delivered win32={Marshal.GetLastWin32Error()}");
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (info.dwExtraInfo != _ignoredMarker)
            {
                int msg = (int)wParam;
                bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                if (down || up)
                {
                    int vk = (int)info.vkCode;
                    _detector.Prune(Native.IsPhysicallyDown, vk); // 処理中のキー自身は GetAsyncKeyState が未更新なので除外される
                    var r = _detector.Process(vk, down, NowMs());
                    if (r.InjectMask)
                    {
                        _sendMask();
                        StartHoldTimer();
                    }
                    if (r.Toggle) Toggled?.Invoke();
                    if (r.HoldEnd) Triggered?.Invoke(HookAction.HoldEnd);
                    // Esc/Enter も上の detector へ先に渡す(Ctrl+Win の間に挟まれたら、離したときに発火させないため)
                    var k = _keys.Process(vk, down, _recording);
                    if (k.Action == RecordingKeyAction.Cancel) Triggered?.Invoke(HookAction.Cancel);
                    else if (k.Action == RecordingKeyAction.Submit) Triggered?.Invoke(HookAction.Submit);
                    if (k.Swallow) return (IntPtr)1;
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>フックスレッドを止めて解除の完了まで待つ。</summary>
    public void Dispose()
    {
        var thread = _thread;
        if (thread == null) return;
        _thread = null;
        uint id = _threadId;
        _threadId = 0;
        if (id == 0) return;
        if (!Native.PostThreadMessage(id, Native.WM_QUIT, UIntPtr.Zero, IntPtr.Zero))
            Log.Write($"keyboard hook stop not delivered win32={Marshal.GetLastWin32Error()}");
        else if (!thread.Join(TimeSpan.FromSeconds(2)))
            Log.Write("keyboard hook did not stop within 2s");
    }
}

/// <summary>録音終了後に前面ウィンドウが対象以外へ移ったかを記録する(元に戻っても「移った」として残す)。</summary>
internal sealed class ForegroundTracker : IDisposable
{
    readonly Native.WinEventProc _proc; // GC されないようフィールドで保持
    IntPtr _hook;
    readonly IntPtr _target;

    public bool ChangedAwayFromTarget { get; private set; }

    /// <summary>監視を開始できたか。false の間は途中の移動を検知できないので、自動貼り付けしてはいけない。</summary>
    public bool IsActive => _hook != IntPtr.Zero;

    public ForegroundTracker(IntPtr target)
    {
        _target = target;
        _proc = OnForeground;
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        if (_hook == IntPtr.Zero) Log.Write("foreground tracker hook failed");
    }

    void OnForeground(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != IntPtr.Zero && hwnd != _target) ChangedAwayFromTarget = true;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
