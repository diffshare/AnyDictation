using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using static AnyDictation.App.AppLog;

namespace AnyDictation.App;

/// <summary>Toggled 以外のフックからの要求。Cancel / Submit は録音中の Esc / Enter、Repaste は Shift+Alt+Z。</summary>
internal enum HookAction { HoldStart, HoldEnd, Cancel, Submit, DismissNotice, Repaste }

/// <summary>
/// WH_KEYBOARD_LL で Ctrl+Win を監視する。Ctrl+Win 関連のイベントは抑制せず、自身の SendInput(OwnMarker)は無視する。
/// 抑制するのは録音中の Esc と Enter(と、その up)だけ。Shift+Alt+Z は同じスレッドの RegisterHotKey で受ける。
/// フックは UI スレッドから切り離した専用スレッド(メッセージループ付き)に置く。UI スレッドが数秒止まっても、
/// Windows がコールバックの timeout でフックを黙って外す(以後キーが届かない)ことを避けるため。
/// <see cref="Toggled"/> と <see cref="Triggered"/> はそのスレッドで同期的に呼ばれるので、購読側は軽く保ち、重い処理は自分のスレッドへ投げること。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    const uint ResetMessage = PInvoke.WM_APP + 1;
    const int RepasteHotkeyId = 1;
    const uint VkZ = 0x5A;

    readonly HotkeyDetector _detector = new(); // フックスレッドだけが触る
    readonly RecordingKeyFilter _keys = new(); // フックスレッドだけが触る
    readonly bool _repasteHotkey;
    volatile bool _noticeDismissible; // 取消の通知が表示中か。UI スレッドが書き、フックスレッドが読む
    volatile bool _recording; // 録音中か。UI スレッドが書き、フックスレッドが読む
    nuint _holdTimer; // 長押し判定のスレッドタイマー(フックスレッドだけが触る)
    readonly HOOKPROC _proc; // GC されないようフィールドで保持
    readonly UIntPtr _ignoredMarker;
    readonly Action _sendMask;
    Thread? _thread;
    volatile uint _threadId;
    HHOOK _hook;

    public event Action? Toggled;
    public event Action<HookAction>? Triggered;

    /// <summary>録音中の間だけ true にする。true の間、Esc と Enter を捕捉する。</summary>
    public bool Recording { set => _recording = value; }

    /// <summary>Esc で閉じられる通知が表示されている間だけ true にする。録音中でなければ、その間の Esc を捕捉して通知を閉じる。</summary>
    public bool NoticeDismissible { set => _noticeDismissible = value; }

    public KeyboardHook() : this(KeyboardInput.OwnMarker, () => KeyboardInput.SendMaskKey(KeyboardInput.OwnMarker), repasteHotkey: true) { }

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
            PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE); // PostThreadMessage の宛先になるメッセージキューを作る
            using var module = Process.GetCurrentProcess().MainModule!;
            _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _proc, PInvoke.GetModuleHandle(module.ModuleName), 0);
            if (_hook.IsNull)
            {
                error = $"キーボードフックを設定できません(Win32 エラー {Marshal.GetLastWin32Error()})。";
                return;
            }
            _threadId = PInvoke.GetCurrentThreadId();
            // 他のアプリが先に登録していれば失敗する。その場合も録音の操作は使えるので、ログだけ残す
            if (_repasteHotkey && !PInvoke.RegisterHotKey(HWND.Null, RepasteHotkeyId, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_SHIFT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VkZ))
                Log.RepasteHotkeyNotRegistered(Marshal.GetLastWin32Error());
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
        Log.KeyboardHookStarted();
        try
        {
            while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0).Value > 0)
            {
                if (msg.message == ResetMessage)
                {
                    _detector.Reset();
                    _keys.Reset();
                    StopHoldTimer();
                }
                else if (msg.message == PInvoke.WM_HOTKEY) Triggered?.Invoke(HookAction.Repaste);
                else if (msg.message == PInvoke.WM_TIMER && msg.wParam.Value == _holdTimer)
                {
                    StopHoldTimer();
                    if (_detector.Tick(NowMs()).HoldStart) Triggered?.Invoke(HookAction.HoldStart);
                }
            }
        }
        finally
        {
            if (_repasteHotkey) PInvoke.UnregisterHotKey(HWND.Null, RepasteHotkeyId);
            StopHoldTimer();
            Unhook();
            Log.KeyboardHookStopped();
        }
    }

    static long NowMs() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <summary>Ctrl+Win が揃った時点から長押しのしきい値の後に判定する。タイマーの刻みで早く届かないよう少し余裕を持たせる。</summary>
    void StartHoldTimer()
    {
        StopHoldTimer();
        _holdTimer = PInvoke.SetTimer(HWND.Null, 0, (uint)HotkeyDetector.HoldThresholdMs + 20, null);
    }

    void StopHoldTimer()
    {
        if (_holdTimer == 0) return;
        PInvoke.KillTimer(HWND.Null, _holdTimer);
        _holdTimer = 0;
    }

    void Unhook()
    {
        if (_hook.IsNull) return;
        PInvoke.UnhookWindowsHookEx(_hook);
        _hook = default;
    }

    /// <summary>セッション切替などで状態が不確かになったとき用。どのスレッドからでも呼べ、フックスレッドで実行される(非同期)。</summary>
    public void Reset()
    {
        uint id = _threadId;
        if (id != 0 && !PInvoke.PostThreadMessage(id, ResetMessage, default, default))
            Log.KeyboardHookResetNotDelivered(Marshal.GetLastWin32Error());
    }

    unsafe LRESULT Callback(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode >= 0)
        {
            var info = *(KBDLLHOOKSTRUCT*)lParam.Value;
            if (info.dwExtraInfo != _ignoredMarker)
            {
                uint msg = (uint)wParam.Value;
                bool down = msg is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;
                bool up = msg is PInvoke.WM_KEYUP or PInvoke.WM_SYSKEYUP;
                if (down || up)
                {
                    int vk = (int)info.vkCode;
                    _detector.Prune(KeyboardInput.IsPhysicallyDown, vk); // 処理中のキー自身は GetAsyncKeyState が未更新なので除外される
                    var r = _detector.Process(vk, down, NowMs());
                    if (r.InjectMask)
                    {
                        _sendMask();
                        StartHoldTimer();
                    }
                    if (r.Toggle) Toggled?.Invoke();
                    if (r.HoldEnd) Triggered?.Invoke(HookAction.HoldEnd);
                    // Esc/Enter も上の detector へ先に渡す(Ctrl+Win の間に挟まれたら、離したときに発火させないため)
                    var k = _keys.Process(vk, down, _recording, _noticeDismissible);
                    if (k.Action == RecordingKeyAction.Cancel) Triggered?.Invoke(HookAction.Cancel);
                    else if (k.Action == RecordingKeyAction.Submit) Triggered?.Invoke(HookAction.Submit);
                    else if (k.Action == RecordingKeyAction.Dismiss) Triggered?.Invoke(HookAction.DismissNotice);
                    if (k.Swallow) return new LRESULT(1);
                }
            }
        }
        return PInvoke.CallNextHookEx(_hook, nCode, wParam, lParam);
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
        if (!PInvoke.PostThreadMessage(id, PInvoke.WM_QUIT, default, default))
            Log.KeyboardHookStopNotDelivered(Marshal.GetLastWin32Error());
        else if (!thread.Join(TimeSpan.FromSeconds(2)))
            Log.KeyboardHookDidNotStop();
    }
}

/// <summary>録音終了後に前面ウィンドウが対象以外へ移ったかを記録する(元に戻っても「移った」として残す)。</summary>
internal sealed class ForegroundTracker : IDisposable
{
    readonly WINEVENTPROC _proc; // GC されないようフィールドで保持
    HWINEVENTHOOK _hook;
    readonly IntPtr _target;

    public bool ChangedAwayFromTarget { get; private set; }

    /// <summary>監視を開始できたか。false の間は途中の移動を検知できないので、自動貼り付けしてはいけない。</summary>
    public bool IsActive => !_hook.IsNull;

    public ForegroundTracker(IntPtr target)
    {
        _target = target;
        _proc = OnForeground;
        _hook = PInvoke.SetWinEventHook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND,
            HMODULE.Null, _proc, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT);
        if (_hook.IsNull) Log.ForegroundTrackerHookFailed();
    }

    unsafe void OnForeground(HWINEVENTHOOK hook, uint evt, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!hwnd.IsNull && (IntPtr)hwnd.Value != _target) ChangedAwayFromTarget = true;
    }

    public void Dispose()
    {
        if (!_hook.IsNull)
        {
            PInvoke.UnhookWinEvent(_hook);
            _hook = default;
        }
    }
}
