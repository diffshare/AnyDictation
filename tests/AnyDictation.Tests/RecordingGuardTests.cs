using Xunit;

namespace AnyDictation.Tests;

/// <summary>
/// Recorder(NAudio の WaveIn に依存するため実体は使えない)と DictationController が録音の通知に対して踏む手順を、
/// 同じ CaptureSlot / CaptureSession の呼び出しで再現する。
///   Recorder.Start            = slot.Begin()
///   Recorder の停止通知        = slot.OnDeviceStopped(session, error) が true のときだけ Failed を発行
///   Controller のキュー実行    = state が Recording かつ slot.IsCurrent(session) のときだけ中断処理
///   Recorder.Cancel/Release    = session.Discard() + slot.Clear()
///   Recorder.TakeInterrupted   = session.Take() + slot.Clear()
/// </summary>
sealed class RecorderHarness
{
    public CaptureSlot Slot { get; } = new();
    public SessionStateMachine State { get; } = new();
    public List<CaptureSession> FailedQueue { get; } = new(); // BeginInvoke でキューに積まれた Failed
    public List<CaptureResult> Interrupted { get; } = new();

    public CaptureSession Start()
    {
        Assert.Equal(ToggleOutcome.Started, State.Toggle());
        return Slot.Begin();
    }

    /// <summary>デバイスからの停止通知(RecordingStopped)。</summary>
    public void DeviceStopped(CaptureSession session, Exception? error)
    {
        if (Slot.OnDeviceStopped(session, error)) FailedQueue.Add(session);
    }

    /// <summary>DictationController.OnRecorderFailed の判定と中断処理。</summary>
    public void RunQueued()
    {
        foreach (var session in FailedQueue.ToList())
        {
            if (State.State != SessionState.Recording || !Slot.IsCurrent(session)) continue;
            var result = Slot.Current!.Take("マイクの異常で録音が中断されました。");
            Slot.Clear();
            Interrupted.Add(result);
            if (result.Silent) State.CancelRecording(); else State.RecordingInterrupted();
        }
        FailedQueue.Clear();
    }

    /// <summary>取消(Controller.CancelRecording → Recorder.Cancel)。</summary>
    public void Cancel()
    {
        Assert.True(State.CancelRecording());
        Slot.Current?.Discard();
        Slot.Clear();
    }

    /// <summary>通常の停止(Controller.StopAndRecognizeAsync → Recorder.StopAsync)。</summary>
    public async Task<CaptureResult> StopAsync(Action<CaptureSession> deviceStopsAfterRequest)
    {
        Assert.Equal(ToggleOutcome.Stopped, State.Toggle());
        var session = Slot.Current!;
        try
        {
            return await session.FinishAsync(() => deviceStopsAfterRequest(session), TimeSpan.FromSeconds(3));
        }
        finally
        {
            Slot.Clear();
        }
    }
}

public class RecordingGuardTests
{
    static byte[] Loud(int samples)
    {
        var b = new byte[samples * 2];
        for (int i = 0; i < samples; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2), (short)4000);
        return b;
    }

    [Fact]
    public void 旧録音を取消して新録音を始めた後に旧録音の停止通知が届いても新録音は中断されない()
    {
        var h = new RecorderHarness();
        var old = h.Start();
        h.Cancel();                       // 取消 → 旧セッションは破棄・無効
        var current = h.Start();          // 新しい録音
        current.Append(Loud(1600), 3200);

        h.DeviceStopped(old, null);       // 旧デバイスの RecordingStopped が遅れて到着

        Assert.Empty(h.FailedQueue);      // Failed は発行されない
        Assert.False(current.IsStopped);  // 新セッションに停止は伝わらない
        Assert.True(h.Slot.IsCurrent(current));
        Assert.Equal(SessionState.Recording, h.State.State);
        h.RunQueued();
        Assert.Empty(h.Interrupted);
        current.Append(Loud(1600), 3200); // 新録音は録音を続けられる
        Assert.Equal(6400, h.Slot.Current!.Take().Wav.Length - 44);
    }

    [Fact]
    public void 旧録音の異常通知がキューに残ったまま新録音を始めても_キュー実行は無視される()
    {
        var h = new RecorderHarness();
        var old = h.Start();
        h.DeviceStopped(old, new IOException("unplugged")); // 旧録音の想定外の停止 → Failed がキューへ
        Assert.Single(h.FailedQueue);

        h.Cancel();                       // キュー実行前に、利用者が取消
        var current = h.Start();          // 新しい録音を開始
        current.Append(Loud(1600), 3200);

        h.RunQueued();                    // 古い Failed がここで実行される

        Assert.Empty(h.Interrupted);      // 新録音は中断されない
        Assert.Equal(SessionState.Recording, h.State.State);
        Assert.True(h.Slot.IsCurrent(current));
        Assert.Equal(3200, current.Take().Wav.Length - 44); // 新しい音声は保持されたまま
    }

    [Fact]
    public async Task 旧録音の異常通知がキューに残ったまま停止され新録音が始まっても無視される()
    {
        var h = new RecorderHarness();
        var old = h.Start();
        old.Append(Loud(1600), 3200);
        h.DeviceStopped(old, new IOException("unplugged"));
        // キュー実行前に利用者が停止(Recognizing へ)し、認識が終わって新しい録音を始める
        var stopped = await h.StopAsync(s => { });
        Assert.False(stopped.Silent);
        h.State.RecognitionSucceeded();
        var current = h.Start();

        h.RunQueued();

        Assert.Empty(h.Interrupted);
        Assert.Equal(SessionState.Recording, h.State.State);
        Assert.True(h.Slot.IsCurrent(current));
    }

    [Fact]
    public void 現在の録音の想定外の停止は一度だけ扱われ_音声を保持した再送待ちになる()
    {
        var h = new RecorderHarness();
        var session = h.Start();
        session.Append(Loud(1600), 3200);

        h.DeviceStopped(session, new IOException("unplugged"));
        h.DeviceStopped(session, new IOException("unplugged again")); // 重複通知
        Assert.Single(h.FailedQueue);

        h.RunQueued();

        var kept = Assert.Single(h.Interrupted);
        Assert.Equal(3200, kept.Wav.Length - 44);                      // ここまでの音声は保持
        Assert.False(kept.Silent);
        Assert.Contains("中断", kept.Warning);
        Assert.Equal(SessionState.RetryPending, h.State.State);        // 送信または破棄を選ぶ状態
        Assert.Null(h.Slot.Current);

        // 同じ通知が再度キュー実行されても二重に扱わない
        h.FailedQueue.Add(session);
        h.RunQueued();
        Assert.Single(h.Interrupted);
    }

    [Fact]
    public void 無音の録音が想定外に止まった場合は破棄されIdleへ戻る()
    {
        var h = new RecorderHarness();
        var session = h.Start();
        session.Append(new byte[3200], 3200);
        h.DeviceStopped(session, new IOException("unplugged"));
        h.RunQueued();
        Assert.True(Assert.Single(h.Interrupted).Silent);
        Assert.Equal(SessionState.Idle, h.State.State);
    }

    [Fact]
    public async Task 現在の録音の正常な停止ではFailedにならず音声が確定する()
    {
        var h = new RecorderHarness();
        var session = h.Start();
        session.Append(Loud(1600), 3200);

        var result = await h.StopAsync(s => Task.Run(() =>
        {
            s.Append(Loud(1600), 3200);   // 停止要求の後に届く最後のバッファ
            h.DeviceStopped(s, null);     // 正常な RecordingStopped
        }));

        Assert.Empty(h.FailedQueue);
        Assert.Equal(6400, result.Wav.Length - 44);
        Assert.Null(result.Warning);
        h.RunQueued();
        Assert.Empty(h.Interrupted);
        Assert.Equal(SessionState.Recognizing, h.State.State);
    }

    [Fact]
    public void 取消のあとに届く当該録音の停止通知はFailedにならない()
    {
        var h = new RecorderHarness();
        var session = h.Start();
        h.Cancel();
        h.DeviceStopped(session, new IOException("device closed by dispose"));
        Assert.Empty(h.FailedQueue);
    }

    [Fact]
    public void 新しい録音の開始時に前の録音が残っていても破棄して無効にする()
    {
        var slot = new CaptureSlot();
        var first = slot.Begin();
        first.Append(Loud(100), 200);
        var second = slot.Begin();
        Assert.False(slot.IsCurrent(first));
        Assert.True(slot.IsCurrent(second));
        Assert.False(slot.OnDeviceStopped(first, null));
        Assert.Equal(0, first.Take().Wav.Length - 44); // 旧セッションの音声は消えている
    }

    [Fact]
    public void MarkStoppedは停止要求の前の最初の通知だけを想定外とする()
    {
        var a = new CaptureSession();
        Assert.True(a.MarkStopped(null));
        Assert.False(a.MarkStopped(null));

        var b = new CaptureSession();
        b.Discard();
        Assert.False(b.MarkStopped(null));

        var c = new CaptureSession();
        c.Take();
        Assert.False(c.MarkStopped(null));
    }
}
