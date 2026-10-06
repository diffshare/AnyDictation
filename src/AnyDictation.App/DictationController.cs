using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AnyDictation.App;

/// <summary>録音から認識、貼り付け/コピー、失敗時の再送待ちまでの流れ。UI スレッドからだけ呼ぶ。</summary>
internal sealed class DictationController : IDisposable, IDeliveryEnvironment
{
    public static readonly TimeSpan MaxRecording = TimeSpan.FromMinutes(5);
    const int MaxLiveChars = 120; // 状態表示に出す Live の途中経過の末尾の文字数
    static readonly TimeSpan ModifierReleaseTimeout = TimeSpan.FromSeconds(5);

    sealed class Job : IDisposable
    {
        public required byte[] Wav;
        public required IntPtr Target;
        public required ForegroundTracker Tracker;

        public void Dispose()
        {
            Tracker.Dispose();
            Array.Clear(Wav); // 音声はメモリ上にしか無いので、終わったら消す
        }
    }

    readonly System.Collections.Generic.List<LiveTranscriptionSession> _costSessions = new();
    public string LiveCostText => LiveCost.Describe(_costSessions.ConvertAll(s => s.GetCostSnapshot()));

    readonly JsonFileStore<AppSettings> _settings;
    readonly ICredentialStore _creds;
    readonly HistoryLog _history;
    readonly StatusWindow _status;
    readonly Recorder _recorder = new();
    readonly SessionStateMachine _state = new();
    readonly MicrophoneUseGate _microphoneUse = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly HttpClient _http = TranscriptionClients.CreateHttpClient();
    readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    Job? _job;
    LiveTranscriptionSession? _live; // 録音中、または認識待ちの Live セッション(UI スレッドだけが読み書きする)
    int _partialPending;
    string _recognitionNote = "";
    CancellationTokenSource? _cts;
    bool _userAborted;
    bool _delivering; // 通信が完了し、結果の配送中。この間は「中止」を受け付けない
    bool _exiting;
    RecordingStartTrace? _startTrace;
    bool _inputMeterLogged;
    bool _microphoneReady; // 現在の録音で最初の音声が届いた(開始音を鳴らした)
    string _startupNote = ""; // 現在の録音で表示する、マイクの起動待ちが長かったことの説明
    readonly MicrophoneStartupNotice _startupNotice = new();

    public event Action<SessionState>? StateChanged;
    public event Action<string, string>? Notice;
    public event Action? SettingsRequested;

    public DictationController(JsonFileStore<AppSettings> settings, ICredentialStore creds, HistoryLog history, StatusWindow status)
    {
        _settings = settings;
        _creds = creds;
        _history = history;
        _status = status;
        _tick.Tick += (_, _) => OnTick();
        _recorder.Failed += (session, msg) => _dispatcher.BeginInvoke(() => OnRecorderFailed(session, msg));
        _recorder.Ready += (session, startup) => _dispatcher.BeginInvoke(() => OnMicrophoneReady(session, startup));
        _status.CancelClicked += CancelRecording;
        _status.AbortClicked += AbortRecognition;
        _status.RetryClicked += () => _ = RetryAsync();
        _status.DiscardClicked += Discard;
    }

    public SessionState State => _state.State;
    public bool HasUnsentAudio => _state.State != SessionState.Idle;

    public bool TryBeginMicrophoneTest() => !E2eMode.Enabled && !_exiting && _microphoneUse.TryBeginTest(State);
    public void EndMicrophoneTest() => _microphoneUse.EndTest();

    // ---- 入口 ----

    public void Toggle()
    {
        if (_exiting || E2eMode.Enabled) return;
        if (_microphoneUse.IsTesting)
        {
            Notify(StatusKind.Warning, "マイクの入力テスト中です", "設定画面で入力テストを停止してから録音してください。", TimeSpan.FromSeconds(3), canClose: true);
            return;
        }
        switch (_state.State)
        {
            case SessionState.Idle:
                StartRecording();
                break;
            case SessionState.Recording:
                _ = StopAndRecognizeAsync(auto: false);
                break;
            case SessionState.Recognizing:
                Notify(StatusKind.Warning, "認識中です", "認識が終わるまで新しい録音は始められません。", TimeSpan.FromSeconds(3), canAbort: !_delivering && _cts != null);
                break;
            default:
                Notify(StatusKind.Failed, "未送信の音声があります", "「再送」で送信するか「破棄」してから、新しい録音を始めてください。", canRetry: true);
                break;
        }
    }

    void StartRecording()
    {
        var trace = new RecordingStartTrace();
        _startTrace = trace;
        _inputMeterLogged = false;
        _microphoneReady = false;
        _startupNote = "";
        trace.Mark("requested");
        // 録音開始前の確認だけ(キーは送信時に取得し直す)
        if (!SendPreflight.TryResolve(_settings, _creds, out var target, out var problem))
        {
            trace.Mark("preflight_failed");
            Notify(StatusKind.Failed, "録音を開始できません", problem, TimeSpan.FromSeconds(8), canClose: true);
            SettingsRequested?.Invoke();
            return;
        }
        trace.Mark("preflight_completed");
        LiveTranscriptionSession? live = null;
        if (target!.Profile.Provider == ProviderKind.AzureOpenAiLive)
        {
            try
            {
                live = new LiveTranscriptionSession(target.Profile, target.ApiKey, log: Log.Write);
            }
            catch (TranscriptionException e)
            {
                trace.Mark("live_setup_failed");
                Notify(StatusKind.Failed, "録音を開始できません", e.Message, TimeSpan.FromSeconds(8), canClose: true);
                SettingsRequested?.Invoke();
                return;
            }
        }
        try
        {
            // Live は 24 kHz で録音し、音声のコピーを送信キューへ積む(キューへの追加はブロックしない)
            _recorder.Start(_settings.Value.MicrophoneDeviceId, trace,
                live == null ? CaptureSession.DefaultSampleRate : LiveTranscriptionSession.SampleRate,
                live == null ? null : pcm => live.Enqueue(pcm));
        }
        catch (Exception e)
        {
            live?.Cancel();
            trace.Mark("microphone_start_failed");
            Log.Write($"recorder start failed: {e.GetType().Name} {e.Message}");
            Notify(StatusKind.Failed, "マイクを開始できません",
                e.Message + "\n設定でマイクを選び直すか、Windows のマイク権限を確認してください。",
                TimeSpan.FromSeconds(10), canClose: true);
            return;
        }
        _costSessions.Clear();
        _state.Toggle(); // Idle -> Recording
        if (live != null) AttachLive(live);
        _tick.Start(); // 開始音は最初の音声が届いてから鳴らす(OnMicrophoneReady)
        Log.Write($"recording started profile={target!.Profile.Name}");
        RaiseState();
        OnTick();
        trace.Mark("status_ui_updated");
    }

    async Task StopAndRecognizeAsync(bool auto)
    {
        if (_state.Toggle() != ToggleOutcome.Stopped) return; // Recording -> Recognizing
        _tick.Stop();
        var target = Native.GetForegroundWindow();
        var tracker = new ForegroundTracker(target); // 録音終了の時点から前面ウィンドウの移動を記録する
        CaptureResult result;
        try
        {
            RaiseState();
            Notify(StatusKind.Recognizing, "録音を終了しています…", "");
            result = await _recorder.StopAsync();
        }
        catch (Exception e) when (!_exiting)
        {
            tracker.Dispose();
            DropLive();
            Log.Write($"stop failed: {e.GetType().Name}");
            if (_state.State == SessionState.Recognizing) _state.AbortRecognition();
            RaiseState();
            Notify(StatusKind.Failed, "録音の終了処理に失敗しました", $"{e.GetType().Name}。音声は破棄しました。", TimeSpan.FromSeconds(10), canClose: true);
            return;
        }
        finally
        {
            if (!_exiting) RecordingSounds.Stopped();
        }
        if (_exiting)
        {
            tracker.Dispose();
            Array.Clear(result.Wav);
            return;
        }
        Log.Write($"recording stopped auto={auto} silent={result.Silent} warning={result.Warning != null} bytes={result.Wav.Length}");

        if (result.Silent)
        {
            bool wasLive = _live != null;
            DropLive();
            tracker.Dispose();
            _state.AbortRecognition();
            Array.Clear(result.Wav);
            RaiseState();
            Notify(StatusKind.Warning, "音声が検出されませんでした",
                "マイクが無音でした。ミュートや入力デバイス、Windows のマイク権限を確認してください。" +
                (wasLive ? "Live は録音中に音声を送信するため、送信済みの無音データは取り消せません。接続は閉じました。" : "音声は送信していません。") +
                "この表示は 8 秒後に消えます。",
                TimeSpan.FromSeconds(8), canClose: true);
            return;
        }

        _job = new Job { Wav = result.Wav, Target = target, Tracker = tracker };
        string note = (auto ? "5 分に達したため自動停止しました。" : "") + (result.Warning ?? "");
        await RunRecognitionAsync(note);
    }

    /// <summary>停止操作なしにマイクが止まった。ここまでの音声は捨てず、未送信のまま「再送(送信)/破棄」の選択にする。</summary>
    void OnRecorderFailed(CaptureSession session, string message)
    {
        // キューに積まれている間に取消/停止/新しい録音の開始があった場合、古い通知は無視する
        if (_state.State != SessionState.Recording || !_recorder.IsCurrent(session)) return;
        _tick.Stop();
        var target = Native.GetForegroundWindow();
        var result = _recorder.TakeInterrupted();
        RecordingSounds.Stopped();
        bool wasLive = _live != null;
        DropLive(); // 保持した音声は、再送で新しいセッションへ全部送り直す
        Log.Write($"recorder interrupted silent={result.Silent} bytes={result.Wav.Length} live={wasLive}");
        if (result.Silent)
        {
            _state.CancelRecording();
            Array.Clear(result.Wav);
            RaiseState();
            Notify(StatusKind.Failed, "録音が中断されました", message + "音声が検出されていなかったため破棄しました。", TimeSpan.FromSeconds(10), canClose: true);
            return;
        }
        _state.RecordingInterrupted();
        _job = new Job { Wav = result.Wav, Target = target, Tracker = new ForegroundTracker(target) };
        RaiseState();
        Notify(StatusKind.Failed, "録音が中断されました",
            message + (wasLive
                ? "\nLive の接続は閉じました。ここまでの録音をメモリに保持しています。「再送」で全体を新しい Live セッションへ送ります(送信済みの分も再度送るため追加課金)。「破棄」もできます。"
                : "\nここまでの録音をメモリに保持しています(未送信)。「再送」で送信するか、「破棄」してください。"), canRetry: true);
    }

    /// <summary>
    /// 最初の音声が届いた時点で開始音を鳴らし、表示を「録音中」にする。
    /// Bluetooth のハンズフリーマイクなどは起動に 1 秒前後かかり、その間に話した音声は録音されないため。
    /// </summary>
    void OnMicrophoneReady(CaptureSession session, TimeSpan startup)
    {
        // キューに積まれている間に停止・取消・新しい録音があった場合は無視する
        if (_state.State != SessionState.Recording || !_recorder.IsCurrent(session)) return;
        _microphoneReady = true;
        if (_startupNotice.Take(startup) is { } note) _startupNote = "\n" + note;
        Log.Write(FormattableString.Invariant($"microphone ready startup_ms={startup.TotalMilliseconds:F0} noticed={_startupNote.Length > 0}"));
        _startTrace?.Mark("sound_play_requested");
        RecordingSounds.Started();
        _startTrace?.Mark("sound_play_call_returned");
        OnTick();
    }

    // ---- Live ----

    /// <summary>録音中の Live セッションを _live として接続を始め、失敗と途中経過の通知をこのコントローラーへ結ぶ。</summary>
    void AttachLive(LiveTranscriptionSession live)
    {
        _live = live;
        _costSessions.Add(live);
        live.PartialChanged += () => OnLivePartial(live);
        // 録音中に通信が失敗したら、録音も止めて最後の音声まで保持し、再送待ちにする(停止の流れをそのまま使う)
        _ = live.Result.ContinueWith(t =>
        {
            if (t.IsFaulted) _dispatcher.BeginInvoke(() => OnLiveFailed(live));
        }, TaskScheduler.Default);
        live.Start();
    }

    void OnLiveFailed(LiveTranscriptionSession live)
    {
        // キューに積まれている間に停止・取消・新しい録音があった場合は無視する
        if (_state.State != SessionState.Recording || !ReferenceEquals(_live, live)) return;
        Log.Write("live failed while recording; stopping the recording");
        _ = StopAndRecognizeAsync(auto: false);
    }

    /// <summary>受信スレッドから呼ばれる。UI への反映は 1 件ずつに畳み、古いセッションの通知は無視する。</summary>
    void OnLivePartial(LiveTranscriptionSession live)
    {
        if (Interlocked.Exchange(ref _partialPending, 1) != 0) return;
        _dispatcher.BeginInvoke(() =>
        {
            Volatile.Write(ref _partialPending, 0);
            // 録音中の表示は 250 ms のタイマーが更新する。ここは停止後(認識待ち)の更新
            if (!ReferenceEquals(_live, live) || _state.State != SessionState.Recognizing || _delivering || _cts == null) return;
            Notify(StatusKind.Recognizing, $"認識中… ({live.Profile.Name})", _recognitionNote, canAbort: true, live: live.GetPartialTail(MaxLiveChars));
        });
    }

    /// <summary>現在の Live セッションを破棄する(結果が出た後は何も起きない)。</summary>
    void DropLive()
    {
        var live = _live;
        _live = null;
        live?.Cancel();
    }

    // ---- 認識 ----

    async Task RunRecognitionAsync(string note)
    {
        var job = _job!;
        // 録音中から接続していた Live セッションがあれば、それが今回の録音の送信先(設定が途中で変わっても同じ接続で完結させる)。
        // なければ(通常の認識と再送)、この時点で選択中のプロファイルへ送る
        var live = _live;
        SendTarget? send = null;
        if (live == null)
        {
            if (!SendPreflight.TryResolve(_settings, _creds, out send, out var problem))
            {
                FailRecognition(problem);
                return;
            }
            if (send!.Profile.Provider == ProviderKind.AzureOpenAiLive && !TryStartLiveFromHeldAudio(send, job.Wav, out live, out problem))
            {
                FailRecognition(problem);
                return;
            }
        }

        var profile = live?.Profile ?? send!.Profile;
        bool usedLive = live != null;
        _cts = new CancellationTokenSource();
        _userAborted = false;
        _delivering = false;
        _recognitionNote = note;
        Notify(StatusKind.Recognizing, $"認識中… ({profile.Name})", note, canAbort: true, live: live?.GetPartialTail(MaxLiveChars));
        Log.Write($"recognition started provider={profile.Provider} model={profile.Model}");
        string text;
        try
        {
            if (live != null)
            {
                live.Complete(); // 最後のバッファまで積み終えている。送信キューを流し切って commit を 1 回送る
                using var abort = _cts.Token.Register(live.Cancel);
                text = await live.Result;
            }
            else
            {
                var client = TranscriptionClients.Create(profile.Provider, _http);
                text = await client.TranscribeAsync(new TranscriptionRequest(profile.Clone(), send!.ApiKey, job.Wav), _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (_exiting) return;
            FailRecognition(_userAborted
                ? "認識を中止しました。音声は保持しています。" + (usedLive ? "Live で送信済みの音声は取り消せません。再送すると全体をもう一度送ります(追加課金)。" : "")
                : "認識がキャンセルされました。");
            return;
        }
        catch (TranscriptionException e)
        {
            Log.Write($"recognition failed kind={e.Kind}");
            FailRecognition(usedLive ? "Live 通信が失敗したため、録音は停止しました。" + e.Message + "\n再送すると保持した音声全体を新しい Live セッションで送ります(追加課金)。" : e.Message);
            return;
        }
        catch (Exception e)
        {
            Log.Write($"recognition failed unexpected={e.GetType().Name}");
            FailRecognition($"想定外のエラーが発生しました({e.GetType().Name})。再送できます。");
            return;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            DropLive();
        }

        // 通信は完了した。これ以降は「中止」を受け付けず、中止が先に押されていたかだけを見る
        _delivering = true;
        Log.Write("recognition succeeded");
        await DeliverAsync(text, profile.Name, _userAborted);
    }

    /// <summary>
    /// Live が選ばれた状態での再送(または、録音後に Live へ切り替えた場合)。保持した WAV が 24 kHz なら新しいセッションへ全部送る。
    /// 16 kHz の録音は人工的にリサンプルせず、バッチのプロファイルを選ぶよう案内する(音声は保持したまま)。
    /// </summary>
    bool TryStartLiveFromHeldAudio(SendTarget send, byte[] wav, out LiveTranscriptionSession? live, out string problem)
    {
        live = null;
        problem = "";
        if (!WavEncoder.TryReadPcm16Mono(wav, out int rate, out var pcm) || rate != LiveTranscriptionSession.SampleRate)
        {
            problem = $"この録音は {rate / 1000} kHz で保持されており、Live({LiveTranscriptionSession.SampleRate / 1000} kHz 固定)へは送れません。" +
                "Live 以外のプロファイルを選んで再送するか、破棄してください。";
            return false;
        }
        try
        {
            live = new LiveTranscriptionSession(send.Profile, send.ApiKey, log: Log.Write);
        }
        catch (TranscriptionException e)
        {
            problem = e.Message;
            return false;
        }
        live.EnqueueAll(pcm);
        AttachLive(live);
        return true;
    }

    void FailRecognition(string message)
    {
        _state.RecognitionFailed();
        RaiseState();
        Notify(StatusKind.Failed, "認識に失敗しました", message + "\n音声は保持しています。再送または破棄を選んでください。", canRetry: true);
    }

    async Task RetryAsync()
    {
        if (_job == null || !_state.Retry()) return;
        RaiseState();
        Log.Write("retry requested");
        await RunRecognitionAsync("");
    }

    void Discard()
    {
        if (!_state.Discard()) return;
        DisposeJob();
        RaiseState();
        Log.Write("pending audio discarded");
        Notify(StatusKind.Idle, "音声を破棄しました", "", TimeSpan.FromSeconds(3));
    }

    void AbortRecognition()
    {
        if (_state.State != SessionState.Recognizing || _delivering || _cts == null) return;
        _userAborted = true;
        _cts.Cancel();
    }

    // ---- 結果の受け渡し(認識成功後。ここで何が起きても API の再送状態にはしない) ----

    async Task DeliverAsync(string text, string profileName, bool abortedFirst)
    {
        string historyNote = "";
        try
        {
            _history.Add(text, profileName); // 課金済みの結果は、配送の成否に関わらず先に履歴へ残す
        }
        catch (Exception e)
        {
            Log.Write($"history save failed: {e.GetType().Name}");
            historyNote = "(履歴は保存できませんでした)";
        }

        if (!_exiting)
            Notify(StatusKind.Recognizing, "認識が完了しました",
                abortedFirst ? "中止の操作より先に認識が完了していました。貼り付けずに結果を保存します。" : "結果を貼り付ける準備をしています(この間は中止できません)。");

        var result = await ResultDelivery.RunAsync(this, text, abortedFirst);
        Log.Write($"delivery outcome={result.Outcome} reason={result.Decision.Reason}");

        _state.RecognitionSucceeded();
        DisposeJob();
        RaiseState();
        if (_exiting || result.Outcome == DeliveryOutcome.ExitingSkipped) return;

        var d = result.Decision;
        switch (result.Outcome)
        {
            case DeliveryOutcome.Pasted:
                Notify(StatusKind.Success, "貼り付けました", historyNote, TimeSpan.FromSeconds(2.5));
                break;
            case DeliveryOutcome.AbortedCopied:
                Notify(StatusKind.Warning, "中止前に認識が完了していました",
                    "貼り付けは行わず、結果をクリップボードと履歴に保存しました(再送信はしていません)。" + historyNote, TimeSpan.FromSeconds(10), canClose: true);
                Notice?.Invoke("Any Dictation", "中止前に認識が完了していたため、結果をクリップボードにコピーしました(貼り付けなし)。");
                break;
            case DeliveryOutcome.ClipboardFailed:
                Notify(StatusKind.Failed, "クリップボードへ書き込めません",
                    "認識は成功しましたが、他のアプリがクリップボードを使用中でした。履歴から取り出してください。" + historyNote, TimeSpan.FromSeconds(10), canClose: true);
                break;
            case DeliveryOutcome.PasteSendFailed:
                Notify(StatusKind.Warning, "クリップボードにコピーしました(貼り付けていません)",
                    "貼り付けキーの送信に失敗したため、自動貼り付けしませんでした。貼り付け先で Ctrl+V してください。" + historyNote, TimeSpan.FromSeconds(10), canClose: true);
                Notice?.Invoke("Any Dictation", "認識結果をクリップボードにコピーしました(自動貼り付けなし)。");
                break;
            default:
                Notify(StatusKind.Warning, "クリップボードにコピーしました(貼り付けていません)",
                    d.Describe() + "、自動貼り付けしませんでした。貼り付け先で Ctrl+V してください。" + historyNote, TimeSpan.FromSeconds(10), canClose: true);
                Notice?.Invoke("Any Dictation", "認識結果をクリップボードにコピーしました(自動貼り付けなし)。");
                break;
        }
    }

    // ---- IDeliveryEnvironment ----

    bool IDeliveryEnvironment.Exiting => _exiting;

    Task<bool> IDeliveryEnvironment.SetClipboardAsync(string text) => ClipboardHelper.SetTextAsync(text);

    async Task<bool> IDeliveryEnvironment.WaitForModifierReleaseAsync()
    {
        var deadline = DateTime.UtcNow + ModifierReleaseTimeout;
        while (Native.AnyModifierDown())
        {
            if (_exiting || DateTime.UtcNow > deadline) return false;
            await Task.Delay(25);
        }
        await Task.Delay(40); // Win キー解放の処理が済むのを待つ
        return !Native.AnyModifierDown();
    }

    PasteContext IDeliveryEnvironment.CaptureContext()
    {
        var job = _job!;
        return new PasteContext(
            job.Target.ToInt64(),
            Native.GetForegroundWindow().ToInt64(),
            job.Tracker.ChangedAwayFromTarget,
            Native.EvaluateElevation(job.Target),
            job.Tracker.IsActive);
    }

    bool IDeliveryEnvironment.SendPaste() => Native.SendCtrlV();

    // ---- 取消・タイマー・終了 ----

    void CancelRecording()
    {
        if (!_state.CancelRecording()) return;
        _tick.Stop();
        // 取消を受け付けた後は未送信のキューも送らない。デバイスの解放(Recorder.Cancel)の完了を待たず、先に Live を閉じる
        bool wasLive = _live != null;
        DropLive();
        _recorder.Cancel();
        RecordingSounds.Stopped();
        RaiseState();
        Log.Write($"recording cancelled live={wasLive}");
        Notify(StatusKind.Idle, "録音を取り消しました",
            wasLive ? "接続を閉じ、保持していた音声は破棄しました。取消の前に Live へ送信済みの音声は取り消せません。" : "音声は送信せず破棄しました。",
            TimeSpan.FromSeconds(wasLive ? 6 : 3));
    }

    void OnTick()
    {
        if (_state.State != SessionState.Recording) return;
        var e = _recorder.Elapsed;
        if (e >= MaxRecording)
        {
            _ = StopAndRecognizeAsync(auto: true);
            return;
        }
        var live = _live;
        Notify(StatusKind.Recording, _microphoneReady ? $"録音中 {e:m\\:ss} / 5:00" : "マイクを準備しています…",
            (_microphoneReady ? "" : "開始音が鳴ってから話してください。") +
            "もう一度 Ctrl+Win で停止して文字起こしします。" +
            (live != null ? "\nLive: 録音中から音声を送信しています。取消しても送信済みの音声は取り消せません。" : "") +
            _startupNote,
            canCancel: true, live: live?.GetPartialTail(MaxLiveChars));
        int? peak = _recorder.ReadInputPeak();
        _status.UpdateInputLevel(peak);
        if (!_inputMeterLogged && peak != null)
        {
            _inputMeterLogged = true;
            _startTrace?.Mark("first_input_meter_updated");
        }
    }

    public void Shutdown()
    {
        _exiting = true;
        _tick.Stop();
        _userAborted = false;
        _cts?.Cancel();
        DropLive();
        _recorder.Cancel();
        DisposeJob();
        _http.Dispose();
    }

    public void Dispose() => Shutdown();

    // ---- 補助 ----

    void DisposeJob()
    {
        _job?.Dispose();
        _job = null;
        _delivering = false;
    }

    void RaiseState() => StateChanged?.Invoke(_state.State);

    void Notify(StatusKind kind, string title, string detail, TimeSpan? autoHide = null,
        bool canCancel = false, bool canAbort = false, bool canRetry = false, bool canClose = false, string? live = null)
        => _status.Present(new StatusView(kind, title, detail, canCancel, canAbort, canRetry, canClose, autoHide, live ?? "", LiveCostText));
}
