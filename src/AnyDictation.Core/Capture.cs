namespace AnyDictation;

public sealed record CaptureResult(byte[] Wav, bool Silent, string? Warning);

/// <summary>
/// 録音データの蓄積と停止の確定。デバイス層(NAudio の WaveIn)の DataAvailable / RecordingStopped を
/// Append / MarkStopped に流し込む。通常の停止は、デバイスが最後のバッファを渡して RecordingStopped を
/// 通知するまで待ってから確定する(語尾を落とさない)。待機は async なので、UI スレッドの
/// SynchronizationContext に通知が投げられるデバイスでもデッドロックしない。
/// 録音のサンプルレートは録音ごとに持つ(既定 16 kHz。Live だけ 24 kHz)。WAV にもそのレートを書く。
/// onAudio は Append された音声(Take / Discard 前のものだけ)のコピーを、デバイスのスレッドから同期で受け取る。
/// 呼び先はブロックしないこと。蓄積は onAudio の有無や失敗に関わらず行う。
/// </summary>
public sealed class CaptureSession
{
    public const int DefaultSampleRate = 16000;

    readonly object _gate = new();
    readonly Action<byte[]>? _onAudio;
    readonly MemoryStream _pcm = new();
    readonly SilenceDetector _silence = new();
    readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Exception? _deviceError;
    int _inputPeak;
    bool _inputReceived;
    bool _closed;
    bool _stopRequested; // このセッションに対する停止/取消/確定の要求。録音ごとに持ち、他の録音とは共有しない

    public CaptureSession(int sampleRate = DefaultSampleRate, Action<byte[]>? onAudio = null)
    {
        SampleRate = sampleRate;
        _onAudio = onAudio;
    }

    public int SampleRate { get; }

    public bool IsStopped => _stopped.Task.IsCompleted;

    /// <summary>前回の表示更新以降の入力ピーク。バッファ未到着は null。音声の蓄積には影響しない。</summary>
    public int? ReadInputPeak()
    {
        lock (_gate)
        {
            int? peak = !_closed && _inputReceived ? _inputPeak : null;
            _inputPeak = 0;
            _inputReceived = false;
            return peak;
        }
    }

    public void Append(byte[] buffer, int count)
    {
        lock (_gate)
        {
            if (_closed) return;
            _pcm.Write(buffer, 0, count);
            _onAudio?.Invoke(buffer.AsSpan(0, count).ToArray());
            var pcm = buffer.AsSpan(0, count);
            _silence.Add(pcm);
            if (count >= 2)
            {
                _inputPeak = Math.Max(_inputPeak, SilenceDetector.MeasurePeak(pcm));
                _inputReceived = true;
            }
        }
    }

    /// <summary>
    /// デバイスが録音を終えた(正常/異常)通知。以降 Append は来ない前提。
    /// 戻り値は「想定外の停止」かどうか: このセッションへの停止要求がなく、最初の通知のときだけ true(一度だけ)。
    /// </summary>
    public bool MarkStopped(Exception? error)
    {
        bool unexpected;
        lock (_gate)
        {
            _deviceError ??= error;
            unexpected = !_stopRequested && !_stopped.Task.IsCompleted;
        }
        _stopped.TrySetResult();
        return unexpected;
    }

    /// <summary>停止を要求し、最後のバッファと停止通知を待ってから確定する。</summary>
    public async Task<CaptureResult> FinishAsync(Action requestStop, TimeSpan timeout)
    {
        lock (_gate) _stopRequested = true; // これ以降のデバイス停止通知は想定内
        string? warning = null;
        if (!IsStopped)
        {
            try
            {
                requestStop();
            }
            catch (Exception e)
            {
                warning = $"録音の停止要求でエラーが発生しました({e.GetType().Name})。";
            }
            using var cts = new CancellationTokenSource();
            var finished = await Task.WhenAny(_stopped.Task, Task.Delay(timeout, cts.Token)).ConfigureAwait(true);
            cts.Cancel();
            if (finished != _stopped.Task)
                warning ??= "録音デバイスからの終了通知がタイムアウトしました。";
        }
        Exception? deviceError;
        lock (_gate) deviceError = _deviceError;
        if (deviceError != null) warning ??= $"録音デバイスが終了時にエラーを返しました({deviceError.GetType().Name})。";
        return Take(warning is null ? null : warning + "音声の末尾が欠けている可能性があります。");
    }

    /// <summary>これ以上待たずに、ここまでの音声を確定する(デバイス異常による中断など)。</summary>
    public CaptureResult Take(string? warning = null)
    {
        byte[] pcm;
        bool silent;
        lock (_gate)
        {
            _closed = true;
            _stopRequested = true;
            pcm = _pcm.ToArray();
            silent = _silence.IsSilent();
            Array.Clear(_pcm.GetBuffer());
            _pcm.SetLength(0);
        }
        var wav = WavEncoder.Encode(pcm, SampleRate, 16, 1);
        Array.Clear(pcm);
        return new CaptureResult(wav, silent, warning);
    }

    /// <summary>取消: 以降のデータを受けず、蓄積を消す。</summary>
    public void Discard()
    {
        lock (_gate)
        {
            _closed = true;
            _stopRequested = true;
            Array.Clear(_pcm.GetBuffer());
            _pcm.SetLength(0);
        }
    }
}

/// <summary>
/// 「いま有効な録音」を 1 つだけ指す。録音ごとの CaptureSession を無効化のタイミングで区別し、
/// 古い録音のデバイス通知(停止/異常)や、キューに積まれた古い異常通知が、新しい録音を中断しないようにする。
/// 有効化: Begin。無効化: Clear(停止の確定・中断・取消・終了の後始末)。
/// 通知の判定は Recorder(発行時)と DictationController(キュー実行時)の両方でここを通す。
/// </summary>
public sealed class CaptureSlot
{
    CaptureSession? _current;

    public CaptureSession? Current => _current;

    /// <summary>新しい録音を始める。前の録音が残っていれば破棄して無効にする。</summary>
    public CaptureSession Begin(int sampleRate = CaptureSession.DefaultSampleRate, Action<byte[]>? onAudio = null)
    {
        _current?.Discard();
        return _current = new CaptureSession(sampleRate, onAudio);
    }

    public bool IsCurrent(CaptureSession session) => ReferenceEquals(_current, session);

    public void Clear() => _current = null;

    /// <summary>
    /// デバイスの停止通知を session に渡す。現在の録音の想定外の停止(最初の 1 回)のときだけ true。
    /// 停止/取消/確定済みの録音や、現在の録音ではないセッションの通知では false。
    /// </summary>
    public bool OnDeviceStopped(CaptureSession session, Exception? error)
        => session.MarkStopped(error) && IsCurrent(session);
}
