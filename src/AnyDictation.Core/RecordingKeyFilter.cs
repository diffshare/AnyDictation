namespace AnyDictation;

public enum RecordingKeyAction { None, Cancel, Submit, Dismiss }

/// <summary>Action は録音側へ伝える操作、Swallow はそのキーイベントをフォアグラウンドのアプリへ渡さない指示。</summary>
public readonly record struct RecordingKeyResult(RecordingKeyAction Action, bool Swallow);

/// <summary>
/// 録音中だけ Esc を取消、Enter を「停止して貼り付け後に Enter」として捕捉する純粋な判断。
/// 録音中でなければ何もせず通す。捕捉した down の対になる up(とキーリピート)は、その間に録音が終わっても捕捉し、
/// 通した down の up とキーリピートは録音が始まっても通す(アプリ側で down と up が食い違わないようにする)。
/// 録音中でなくても、取消の通知が表示されている間(dismissible)の Esc は、その通知を閉じる(Dismiss)として同じ規則で捕捉する。
/// 修飾キーは見ない(Ctrl+Win を押したままの長押し録音中にも使えるように)。フックスレッドだけが触る。
/// </summary>
public sealed class RecordingKeyFilter
{
    public const int VkReturn = 0x0D;
    public const int VkEscape = 0x1B;

    readonly Dictionary<int, bool> _downs = new(); // 押下中のキー。値は捕捉したか

    public RecordingKeyResult Process(int vk, bool isDown, bool recording, bool dismissible = false)
    {
        if (vk is not (VkEscape or VkReturn)) return default;
        if (!isDown) return new(RecordingKeyAction.None, _downs.Remove(vk, out bool swallowedDown) && swallowedDown);
        if (_downs.TryGetValue(vk, out bool swallowed)) return new(RecordingKeyAction.None, swallowed); // キーリピートは最初の down と同じ扱い
        if (vk == VkEscape && !recording && dismissible)
        {
            _downs[vk] = true;
            return new(RecordingKeyAction.Dismiss, true);
        }
        _downs[vk] = recording;
        if (!recording) return default;
        return new(vk == VkEscape ? RecordingKeyAction.Cancel : RecordingKeyAction.Submit, true);
    }

    public void Reset() => _downs.Clear();
}
