using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AnyDictation.App;

internal static class Program
{
    const string MutexName = @"Local\AnyDictation.SingleInstance";
    internal const string ShowEventName = @"Local\AnyDictation.ShowSettings";

    [STAThread]
    static void Main(string[] args)
    {
        if (!E2eMode.Configure(args)) { Environment.ExitCode = 2; return; }
        using var mutex = new Mutex(true, E2eMode.Enabled ? MutexName + ".E2E." + E2eMode.InstanceId : MutexName, out bool first);
        if (!first)
        {
            if (E2eMode.Enabled) { Environment.ExitCode = 3; return; }
            // 既に起動中: 既存インスタンスの設定画面を前面へ出して終了する
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            return;
        }
        new AnyApp().Run();
    }
}

internal sealed class AnyApp : Application
{
    JsonFileStore<AppSettings> _settings = null!;
    JsonFileStore<HistoryData> _historyStore = null!;
    StatusWindow _status = null!;
    SettingsWindow _settingsWindow = null!;
    DictationController _controller = null!;
    KeyboardHook _hook = null!;
    TrayIcon _tray = null!;
    EventWaitHandle _showEvent = null!;
    RegisteredWaitHandle? _showWait;
    bool _exiting;

    public AnyApp()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        _settings = new JsonFileStore<AppSettings>(AppPaths.SettingsFile, AppSettings.Validate);
        _settings.Load();
        _historyStore = new JsonFileStore<HistoryData>(AppPaths.HistoryFile, HistoryData.Validate);
        _historyStore.Load();
        var history = new HistoryLog(_historyStore);
        Log.Write($"startup settingsCorrupt={_settings.IsCorrupt} historyCorrupt={_historyStore.IsCorrupt}");

        ICredentialStore creds = E2eMode.Enabled ? new E2eCredentials() : new WindowsCredentialStore();
        _status = new StatusWindow();
        _controller = new DictationController(_settings, creds, history, _status);
        _settingsWindow = new SettingsWindow(_settings, creds, _historyStore, history, _controller);
        if (E2eMode.Enabled)
        {
            _settingsWindow.AllowClose = true;
            _settingsWindow.Closed += (_, _) => { _controller.Shutdown(); _status.Close(); Shutdown(); };
            _settingsWindow.Open(SettingsTab.Profile);
            return;
        }
        _tray = new TrayIcon(
            openSettings: () => _settingsWindow.Open(SettingsTab.Profile),
            openHistory: () => _settingsWindow.Open(SettingsTab.History),
            toggle: () => RequestToggle("tray"),
            exit: RequestExit);

        _controller.StateChanged += _tray.SetState;
        _controller.Notice += _tray.Balloon;
        _controller.SettingsRequested += () => _settingsWindow.Open(SettingsTab.Profile);

        _hook = new KeyboardHook();
        _hook.Toggled += OnHotkey;
        try
        {
            _hook.Install();
        }
        catch (InvalidOperationException ex)
        {
            Log.Write("hook install failed");
            MessageBox.Show(ex.Message + "\nトレイのメニューからは録音を開始できます。", "Any Dictation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _settingsWindow.Open(SettingsTab.Profile)), null, Timeout.Infinite, false);

        // 初回(プロファイル未設定)と設定ファイル破損時は設定画面を開く
        if (_settings.IsCorrupt || _historyStore.IsCorrupt || _settings.Value.Profiles.Count == 0)
            _settingsWindow.Open(SettingsTab.Profile);
    }

    /// <summary>フックスレッドから呼ばれる。ログ書き込みなど重い処理はせず、UI スレッドへ渡すだけにする。</summary>
    void OnHotkey()
    {
        long detected = Stopwatch.GetTimestamp();
        Dispatcher.BeginInvoke(() => RequestToggle("hotkey", detected));
    }

    /// <summary>録音の開始/停止要求の入口。経路(hotkey/tray)と、検出から UI 処理までの遅れだけを記録する。</summary>
    void RequestToggle(string source, long detected = 0)
    {
        string delay = detected == 0 ? "" : FormattableString.Invariant($" ui_delay_ms={Stopwatch.GetElapsedTime(detected).TotalMilliseconds:F0}");
        Log.Write($"toggle requested source={source}{delay}");
        _controller.Toggle();
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) => _hook.Reset();

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write($"unhandled {e.Exception.GetType().Name}");
        e.Handled = true;
        _status.Present(new StatusView(StatusKind.Failed, "予期しないエラー",
            $"{e.Exception.GetType().Name}。状態が不明な場合はトレイから終了して起動し直してください。", CanClose: true,
            AutoHide: TimeSpan.FromSeconds(10)));
    }

    /// <summary>本当の終了。録音と通信を止め、メモリ上の音声を消して、フックとトレイを解放する。</summary>
    void RequestExit()
    {
        if (_exiting) return;
        if (_controller.HasUnsentAudio &&
            MessageBox.Show("録音中、または未送信の音声があります。終了すると音声は破棄されます。終了しますか?",
                "Any Dictation", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        _exiting = true;
        Log.Write("exit");
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _showWait?.Unregister(null);
        _showEvent.Dispose();
        _hook.Dispose();
        _controller.Shutdown();
        _tray.Dispose();
        _status.Close();
        _settingsWindow.AllowClose = true;
        _settingsWindow.Close();
        Shutdown();
    }
}
