namespace AnyDictation;

public enum SessionState { Idle, Recording, Recognizing, RetryPending }

public enum ToggleOutcome { Started, Stopped, BusyRecognizing, RetryPending }

/// <summary>録音/認識/再送待ちの遷移規則。認識中と再送待ち中は新しい録音を始めさせない。</summary>
public sealed class SessionStateMachine
{
    public SessionState State { get; private set; } = SessionState.Idle;

    public ToggleOutcome Toggle()
    {
        switch (State)
        {
            case SessionState.Idle:
                State = SessionState.Recording;
                return ToggleOutcome.Started;
            case SessionState.Recording:
                State = SessionState.Recognizing;
                return ToggleOutcome.Stopped;
            case SessionState.Recognizing:
                return ToggleOutcome.BusyRecognizing;
            default:
                return ToggleOutcome.RetryPending;
        }
    }

    /// <summary>録音の取消(音声は送信せず破棄)。</summary>
    public bool CancelRecording() => Move(SessionState.Recording, SessionState.Idle);

    /// <summary>マイクの異常で録音が中断した。ここまでの音声は未送信のまま再送待ち(送信または破棄の選択)にする。</summary>
    public bool RecordingInterrupted() => Move(SessionState.Recording, SessionState.RetryPending);

    /// <summary>無音などで API へ送らずに終了する。</summary>
    public bool AbortRecognition() => Move(SessionState.Recognizing, SessionState.Idle);

    public bool RecognitionSucceeded() => Move(SessionState.Recognizing, SessionState.Idle);

    public bool RecognitionFailed() => Move(SessionState.Recognizing, SessionState.RetryPending);

    public bool Retry() => Move(SessionState.RetryPending, SessionState.Recognizing);

    public bool Discard() => Move(SessionState.RetryPending, SessionState.Idle);

    bool Move(SessionState from, SessionState to)
    {
        if (State != from) return false;
        State = to;
        return true;
    }
}
