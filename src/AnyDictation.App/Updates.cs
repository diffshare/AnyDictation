using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace AnyDictation.App;

/// <summary>
/// インストール版(Velopack)の自動更新。起動時と 24 時間ごとに GitHub Releases を確認し、新しい版を裏でダウンロードする。
/// 適用はアプリの終了時か「再起動して更新」のときだけ行う。終了時は、ダウンロード中なら中止して終わるまで待つ。
/// portable 版と E2E では何もしない。
/// </summary>
internal sealed class AppUpdater
{
    const string RepoUrl = "https://github.com/diffshare/AnyDictation";
    static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    readonly UpdateManager _manager = CreateManager();
    readonly DispatcherTimer _timer = new() { Interval = Interval };
    readonly CancellationTokenSource _cts = new();
    Task? _running;
    Task? _download;

    /// <summary>ダウンロード済みで、次の終了時に適用する版。</summary>
    public VelopackAsset? Pending { get; private set; }
    public bool IsInstalled => _manager.IsInstalled;
    public event Action? PendingChanged;

    static UpdateManager CreateManager() => new(new GithubSource(RepoUrl, null, false));

    /// <summary>
    /// 前回ダウンロードした更新を、起動処理の前に適用して再起動する。適用を始めたら true を返すので、呼び出し側はすぐ終了する。
    /// Velopack の起動時の自動適用は二重起動のたびに動き、起動中のインスタンスを止めるため使わず、単一インスタンスを確定してからここで行う。
    /// 更新直後の再起動では、適用に失敗した場合の再起動の繰り返しを避けるため行わない。
    /// </summary>
    public static bool ApplyPendingOnStartup(bool restarted)
    {
        if (restarted) return false;
        var manager = CreateManager();
        if (!manager.IsInstalled || manager.UpdatePendingRestart is not { } pending) return false;
        Log.Write($"update apply on startup version={pending.Version}");
        try
        {
            manager.WaitExitThenApplyUpdates(pending, silent: true, restart: true);
            return true;
        }
        catch (Exception ex)
        {
            // 更新プログラムを起動できなくても、今の版で起動を続ける
            Log.Write($"update apply failed {ex.GetType().Name}");
            return false;
        }
    }

    public void Start()
    {
        if (!IsInstalled) return;
        Pending = _manager.UpdatePendingRestart;
        if (Pending != null) PendingChanged?.Invoke();
        _timer.Tick += (_, _) => StartCheck();
        _timer.Start();
        StartCheck();
    }

    void StartCheck()
    {
        if (_running is { IsCompleted: false }) return;
        _running = CheckAsync();
    }

    async Task CheckAsync()
    {
        // ダウンロード済みの版がある間は確認しない。次の版のダウンロードで、その版のパッケージが消えるため
        if (Pending != null) return;
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            // 確認の通信中に終了が始まっていたら、ダウンロードを始めない
            if (info == null || _cts.IsCancellationRequested) return;
            _download = _manager.DownloadUpdatesAsync(info, cancelToken: _cts.Token);
            await _download;
            Pending = info.TargetFullRelease;
            Log.Write($"update downloaded version={Pending.Version}");
            PendingChanged?.Invoke();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // 終了のための中止
        }
        catch (Exception ex)
        {
            // 通信の失敗などは次回の確認で再試行する。利用者には通知しない
            Log.Write($"update check failed {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 終了の直前に呼ぶ。確認とダウンロードを中止し、実行中のダウンロードだけ終わるまで待つ。
    /// ダウンロードは終わりに更新プログラム(Update.exe)を書き換えるため、その途中でプロセスが終わらないようにする。
    /// 確認の通信は待たない(途中で終わっても害はなく、中止できないため待つと終了が遅れる)。
    /// </summary>
    public async Task StopAsync()
    {
        _timer.Stop();
        _cts.Cancel();
        if (_download == null) return;
        try
        {
            await _download;
        }
        catch (Exception)
        {
            // 中止も失敗も CheckAsync が処理する。終了処理は止めない
        }
    }

    /// <summary>終了の直前に呼ぶ。ダウンロード済みの更新があれば、終了を待って適用する更新プログラムを起動する。</summary>
    public void ApplyOnExit(bool restart)
    {
        if (Pending == null) return;
        Log.Write($"update apply on exit version={Pending.Version} restart={restart}");
        try
        {
            _manager.WaitExitThenApplyUpdates(Pending, silent: true, restart);
        }
        catch (Exception ex)
        {
            // 適用できなくても終了は続ける。次回の起動時に再び適用を試みる
            Log.Write($"update apply failed {ex.GetType().Name}");
        }
    }
}
