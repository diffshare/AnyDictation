namespace AnyDictation;

/// <summary>
/// Ctrl+Win の長押しが対象にしている録音を覚え、離したときに「同じ録音がまだ続いている場合だけ」停止させる。
/// 長押しの途中で取消され、別の録音(トレイなど)が始まっていても、その録音は止めない。録音の識別子は 1 以上、0 は対象なし。
/// </summary>
public sealed class HoldRecordingGuard
{
    long _id;

    public void Begin(long recordingId) => _id = recordingId;

    public bool ShouldStopOnRelease(long currentRecordingId, bool recording)
    {
        long id = _id;
        _id = 0;
        return id != 0 && id == currentRecordingId && recording;
    }
}
