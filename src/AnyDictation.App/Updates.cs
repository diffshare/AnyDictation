using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;
using static AnyDictation.App.AppLog;

namespace AnyDictation.App;

/// <summary>
/// インストール版(Velopack)の自動更新。起動時と 24 時間ごとに GitHub Releases を確認し、新しい版を裏でダウンロードする。
/// 適用は「再起動して更新」のときだけ行い、通常の終了や次回の起動では適用しない。ダウンロード済みの更新は次回の起動時に読み直す。
/// 終了時は、ダウンロード中なら中止して終わるまで待つ。portable 版と E2E では何もしない。
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

    /// <summary>ダウンロード済みで、「再起動して更新」で適用する版。</summary>
    public VelopackAsset? Pending { get; private set; }
    public bool IsInstalled => _manager.IsInstalled;
    public event Action? PendingChanged;

    static UpdateManager CreateManager() => new(new GithubSource(RepoUrl, null, false));

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
            Log.UpdateDownloaded(Pending.Version.ToString());
            PendingChanged?.Invoke();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // 終了のための中止
        }
        catch (Exception ex)
        {
            // 通信の失敗などは次回の確認で再試行する。利用者には通知しない
            Log.UpdateCheckFailed(ex.GetType().Name);
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

    /// <summary>「再起動して更新」の終了の直前に呼ぶ。ダウンロード済みの更新があれば、終了を待って適用し再起動する更新プログラムを起動する。</summary>
    public void ApplyAndRestart()
    {
        if (Pending == null) return;
        Log.UpdateApplyAndRestart(Pending.Version.ToString());
        try
        {
            _manager.WaitExitThenApplyUpdates(Pending, silent: true, restart: true);
        }
        catch (Exception ex)
        {
            // 適用できなくても終了は続ける。次回の起動時に、ダウンロード済みの更新として再び「再起動して更新」を示す
            Log.UpdateApplyFailed(ex.GetType().Name);
        }
    }
}
