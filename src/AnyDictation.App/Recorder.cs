using System;
using System.Threading.Tasks;
using AnyDictation;
using NAudio.Wave;

namespace AnyDictation.App;

/// <summary>選択したマイクから 16bit/mono(既定 16kHz、Live は 24kHz)で録音し、メモリ上にだけ保持する。蓄積と停止の確定は CaptureSession、有効な録音の判定は CaptureSlot。</summary>
internal sealed class Recorder : IDisposable
{
    static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    readonly CaptureSlot _slot = new();
    WaveIn? _waveIn;
    DateTime _startedAt;

    /// <summary>現在の録音が、停止操作なしに終わった(デバイス切断など)。録音ごとに一度だけ。UI スレッドとは限らない。</summary>
    public event Action<CaptureSession, string>? Failed;

    /// <summary>現在の録音で最初の音声が届いた(マイクの起動が済んだ)。値は開始からの経過時間。録音ごとに一度だけ。UI スレッドとは限らない。</summary>
    public event Action<CaptureSession, TimeSpan>? Ready;

    public int? ReadInputPeak() => _slot.Current?.ReadInputPeak();

    public bool IsRecording => _waveIn != null;
    public TimeSpan Elapsed => IsRecording ? DateTime.UtcNow - _startedAt : TimeSpan.Zero;

    /// <summary>キューに積まれた通知を実行する時点で、その録音がまだ現在の録音かを確認するために使う。</summary>
    public bool IsCurrent(CaptureSession session) => _slot.IsCurrent(session);

    /// <summary>onAudio は受け取った音声のコピーをデバイスのスレッドから同期で受ける(Live の送信キュー用。ブロックしないこと)。</summary>
    public void Start(string? microphoneId, RecordingStartTrace? trace = null,
        int sampleRate = CaptureSession.DefaultSampleRate, Action<byte[]>? onAudio = null)
    {
        if (_waveIn != null) throw new InvalidOperationException("すでに録音中です。");
        trace?.Mark("device_resolution_started");
        int deviceNumber = Microphones.Resolve(microphoneId);
        trace?.Mark("device_resolution_completed");
        var session = _slot.Begin(sampleRate, onAudio);
        var w = new WaveIn { DeviceNumber = deviceNumber, WaveFormat = new WaveFormat(sampleRate, 16, 1), BufferMilliseconds = 100 };
        var startup = System.Diagnostics.Stopwatch.StartNew();
        int bufferReceived = 0;
        int signalLogged = 0;
        w.DataAvailable += (_, e) =>
        {
            session.Append(e.Buffer, e.BytesRecorded); // 閉じた旧セッションへの Append は無視される
            if (e.BytesRecorded == 0 || !_slot.IsCurrent(session)) return;
            if (System.Threading.Interlocked.Exchange(ref bufferReceived, 1) == 0)
            {
                trace?.Mark("first_audio_buffer_received");
                Ready?.Invoke(session, startup.Elapsed);
            }
            if (trace == null) return;
            if (System.Threading.Volatile.Read(ref signalLogged) == 0 &&
                SilenceDetector.MeasurePeak(e.Buffer.AsSpan(0, e.BytesRecorded)) >= SilenceDetector.DefaultThreshold &&
                System.Threading.Interlocked.Exchange(ref signalLogged, 1) == 0)
                trace.Mark("first_non_silent_buffer_received");
        };
        w.RecordingStopped += (_, e) =>
        {
            // 判定はこの録音(session)自身の状態と現在の録音かどうかで行う。他の録音の停止要求とは共有しない
            if (_slot.OnDeviceStopped(session, e.Exception))
                Failed?.Invoke(session, "マイクからの録音が中断されました。デバイスの接続を確認してください。");
        };
        try
        {
            trace?.Mark("wavein_start_call_started");
            w.StartRecording();
            trace?.Mark("wavein_start_call_returned");
        }
        catch
        {
            w.Dispose();
            _slot.Clear();
            throw;
        }
        _startedAt = DateTime.UtcNow;
        _waveIn = w;
    }

    /// <summary>
    /// 停止を要求し、最後のバッファと停止通知を待ってから WAV を確定する。待機は await なので UI スレッドを塞がない。
    /// 停止中のエラーやタイムアウトは result.Warning で伝え、それまでの音声は返す。
    /// </summary>
    public async Task<CaptureResult> StopAsync()
    {
        var session = _slot.Current ?? throw new InvalidOperationException("録音していません。");
        var w = _waveIn!;
        try
        {
            return await session.FinishAsync(w.StopRecording, StopTimeout);
        }
        finally
        {
            Release();
        }
    }

    /// <summary>デバイス異常で録音が止まったとき、ここまでの音声を待たずに確定する(確定した録音は無効になる)。</summary>
    public CaptureResult TakeInterrupted()
    {
        var session = _slot.Current ?? throw new InvalidOperationException("録音していません。");
        var result = session.Take("マイクの異常で録音が中断されました。");
        Release();
        return result;
    }

    /// <summary>取消・終了: 音声を破棄する(API へは送らない)。録音は無効になる。</summary>
    public void Cancel()
    {
        _slot.Current?.Discard();
        Release();
    }

    void Release()
    {
        var w = _waveIn;
        _waveIn = null;
        _slot.Clear();
        if (w == null) return;
        try { w.StopRecording(); } catch { /* 既に停止済みなら無視して解放へ */ }
        w.Dispose();
    }

    public void Dispose() => Cancel();
}
