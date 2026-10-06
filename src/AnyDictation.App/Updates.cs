using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace AnyDictation.App;

/// <summary>
/// インストール版(Velopack)の自動更新。起動時と 24 時間ごとに GitHub Releases を確認し、新しい版を裏でダウンロードする。
/// 適用はアプリの終了時か「再起動して更新」のときだけ行う。portable 版と E2E では何もしない。
/// </summary>
internal sealed class AppUpdater
{
    const string RepoUrl = "https://github.com/diffshare/AnyDictation";
    static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    readonly UpdateManager _manager = CreateManager();
    readonly DispatcherTimer _timer = new() { Interval = Interval };
    bool _checking;

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
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        _ = CheckAsync();
    }

    async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info == null || info.TargetFullRelease.Version == Pending?.Version) return;
            await _manager.DownloadUpdatesAsync(info);
            Pending = info.TargetFullRelease;
            Log.Write($"update downloaded version={Pending.Version}");
            PendingChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // 通信の失敗などは次回の確認で再試行する。利用者には通知しない
            Log.Write($"update check failed {ex.GetType().Name}");
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>終了の直前に呼ぶ。ダウンロード済みの更新があれば、終了を待って適用する更新プログラムを起動する。</summary>
    public void ApplyOnExit(bool restart)
    {
        if (Pending == null) return;
        _timer.Stop();
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
