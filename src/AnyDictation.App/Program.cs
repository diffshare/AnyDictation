using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Velopack;
using static AnyDictation.App.AppLog;

namespace AnyDictation.App;

internal static class Program
{
    const string MutexName = @"Local\AnyDictation.SingleInstance";
    internal const string ShowEventName = @"Local\AnyDictation.ShowSettings";

    [STAThread]
    static void Main(string[] args)
    {
        // インストール版の install/uninstall などの hook はここで処理して終了する。portable 版では何もしない
        bool restarted = false;
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.RemoveIfOwned())
            .OnRestarted(_ => restarted = true)
            .Run();
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
        if (!E2eMode.Enabled && AppUpdater.ApplyPendingOnStartup(restarted)) return;
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
    AppUpdater? _updater;
    readonly string _versionText = $"バージョン {typeof(Program).Assembly.GetName().Version?.ToString(3)}。";
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
        AppTheme.Initialize(this, _settings.Value.Theme); // 設定が壊れているときは既定(Windows に合わせる)
        _historyStore = new JsonFileStore<HistoryData>(AppPaths.HistoryFile, HistoryData.Validate);
        _historyStore.Load();
        var history = new HistoryLog(_historyStore);
        Log.Startup(_settings.IsCorrupt, _historyStore.IsCorrupt);

        ICredentialStore creds = E2eMode.Enabled ? new E2eCredentials() : new WindowsCredentialStore();
        _status = new StatusWindow();
        _controller = new DictationController(_settings, creds, history, _status);
        _settingsWindow = new SettingsWindow(_settings, creds, _historyStore, history, _controller);
        AppTheme.Watch(_settingsWindow);
        if (E2eMode.Enabled)
        {
            _settingsWindow.ShowUpdateState(_versionText + "E2E テストでは更新を確認しません。", canApply: false);
            _settingsWindow.AllowClose = true;
            _settingsWindow.Closed += (_, _) => { _controller.Shutdown(); _status.Close(); Shutdown(); };
            _settingsWindow.Open(SettingsTab.Profile);
            return;
        }
        _tray = new TrayIcon(
            openSettings: () => _settingsWindow.Open(SettingsTab.Profile),
            openHistory: () => _settingsWindow.Open(SettingsTab.History),
            toggle: () => RequestToggle(),
            restartToUpdate: RestartToUpdate,
            exit: () => RequestExit(restart: false));

        _updater = new AppUpdater();
        _settingsWindow.ShowUpdateState(_versionText + (_updater.IsInstalled
            ? "起動時と 24 時間ごとに更新を確認し、新しい版を自動でダウンロードします。"
            : "インストール版ではないため、自動更新しません。"), canApply: false);
        _updater.PendingChanged += OnUpdateReady;
        _settingsWindow.UpdateRequested += RestartToUpdate;
        _updater.Start();

        _controller.StateChanged += _tray.SetState;
        _controller.Notice += _tray.Balloon;
        _controller.SettingsRequested += () => _settingsWindow.Open(SettingsTab.Profile);

        _hook = new KeyboardHook();
        _hook.Toggled += OnHotkey;
        _hook.Triggered += OnHookAction;
        _controller.StateChanged += state => _hook.Recording = state == SessionState.Recording;
        _status.EscDismissibleChanged += visible => _hook.NoticeDismissible = visible;
        try
        {
            _hook.Install();
        }
        catch (InvalidOperationException ex)
        {
            Log.HookInstallFailed();
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
        Dispatcher.BeginInvoke(() => RequestToggle(detected));
    }

    /// <summary>フックスレッドから呼ばれる。OnHotkey と同じく UI スレッドへ渡すだけにする。</summary>
    void OnHookAction(HookAction action) => Dispatcher.BeginInvoke(() =>
    {
        Log.HookActionReceived(action);
        switch (action)
        {
            case HookAction.HoldStart: _controller.HoldStart(); break;
            case HookAction.HoldEnd: _controller.HoldEnd(); break;
            case HookAction.Cancel: _controller.CancelRecording(); break;
            case HookAction.Submit: _controller.SubmitRecording(); break;
            case HookAction.DismissNotice: _status.DismissIfEscDismissible(); break;
            case HookAction.Repaste: _controller.RepasteLast(); break;
        }
    });

    /// <summary>録音の開始/停止要求の入口。経路(hotkey/tray)と、検出から UI 処理までの遅れだけを記録する。detected が 0 ならトレイから。</summary>
    void RequestToggle(long detected = 0)
    {
        if (detected == 0) Log.ToggleRequestedFromTray();
        else Log.ToggleRequestedFromHotkey(Stopwatch.GetElapsedTime(detected).TotalMilliseconds);
        _controller.Toggle();
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) => _hook.Reset();

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Unhandled(e.Exception.GetType().Name);
        e.Handled = true;
        _status.Present(new StatusView(StatusKind.Failed, "予期しないエラー",
            $"{e.Exception.GetType().Name}。状態が不明な場合はトレイから終了して起動し直してください。", CanClose: true,
            AutoHide: TimeSpan.FromSeconds(10)));
    }

    void OnUpdateReady()
    {
        // 終了処理中は、破棄済みのトレイなどを触らない。Pending は設定済みなので終了時に適用される
        if (_exiting) return;
        string version = _updater!.Pending!.Version.ToString();
        _tray.ShowUpdateReady(version);
        _settingsWindow.ShowUpdateState(_versionText + $"{version} の準備ができました。終了時に更新します。", canApply: true);
        _tray.Balloon("更新の準備ができました",
            $"{version} に更新できます。トレイのメニューか設定画面の「一般」で「再起動して更新」を選ぶか、終了したときに更新します。");
    }

    /// <summary>録音、認識、再送待ちの間は更新しない。その音声を失わないため。</summary>
    void RestartToUpdate()
    {
        if (_controller.State != SessionState.Idle)
        {
            _tray.Balloon("今は更新できません", "録音中、認識中、再送待ちの間は更新できません。終わってから、もう一度選んでください。");
            return;
        }
        RequestExit(restart: true);
    }

    /// <summary>本当の終了。録音と通信を止め、メモリ上の音声を消して、フックとトレイを解放する。ダウンロード済みの更新があれば適用する。</summary>
    async void RequestExit(bool restart)
    {
        if (_exiting) return;
        if (_controller.HasUnsentAudio &&
            MessageBox.Show("録音中、または未送信の音声があります。終了すると音声は破棄されます。終了しますか?",
                "Any Dictation", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        _exiting = true;
        Log.Exiting();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _showWait?.Unregister(null);
        _showEvent.Dispose();
        _hook.Dispose();
        _controller.Shutdown();
        _tray.Dispose();
        _status.Close();
        _settingsWindow.AllowClose = true;
        _settingsWindow.Close();
        // 利用者からは終了済みに見える。更新のダウンロード中にプロセスが終わらないよう、止めて待つ(確認の例外は CheckAsync が処理済み)
        if (_updater != null) await _updater.StopAsync();
        _updater?.ApplyOnExit(restart);
        Shutdown();
    }
}
