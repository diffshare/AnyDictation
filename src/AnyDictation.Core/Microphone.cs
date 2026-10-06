namespace AnyDictation;

public sealed record MicrophoneDevice(int Number, string? Id, string Name);

public static class MicrophoneSelection
{
    /// <summary>一覧と既定の表示で同じ名前を出す。番号は WinMM の列挙順(1 始まりで表示)。ProductName は WinMM の制限で 31 文字までに切れる。</summary>
    public static string Display(MicrophoneDevice device) => $"{device.Name}［{device.Number + 1}］";

    /// <summary>
    /// 「Windows の既定のマイク」の項目名。保存値は null(WAVE_MAPPER)のままで、これは表示だけ。
    /// preferred は WinMM が現在の既定として返す番号。取得できなければ null、既定なしは -1。
    /// </summary>
    public static string DescribeDefault(int? preferred, IReadOnlyList<MicrophoneDevice> devices)
    {
        const string Label = "Windows の既定のマイク";
        if (preferred == null) return Label + "（現在の既定を取得できません）";
        if (preferred < 0) return Label + "（現在: 利用できるマイクなし）";
        var device = devices.FirstOrDefault(d => d.Number == preferred);
        return device == null ? Label + "（現在の既定を取得できません）" : $"{Label}（現在: {Display(device)}）";
    }

    public static int Resolve(string? id, IReadOnlyList<MicrophoneDevice> devices)
    {
        if (id == null) return -1; // WAVE_MAPPER: Windows の既定入力
        var matches = devices.Where(d => !string.IsNullOrEmpty(d.Id) && string.Equals(id, d.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException("選択したマイクが見つかりません。設定で一覧を再読み込みし、マイクを選び直してください。");
        if (matches.Length != 1)
            throw new InvalidOperationException("マイクを一意に識別できません。設定で別のマイクを選んでください。");
        return matches[0].Number;
    }
}

/// <summary>UI スレッドが本録音の状態と入力テストの排他を判断する。テストは Idle のときだけ開始する。</summary>
public sealed class MicrophoneUseGate
{
    public bool IsTesting { get; private set; }
    public bool TryBeginTest(SessionState state)
    {
        if (IsTesting || state != SessionState.Idle) return false;
        IsTesting = true;
        return true;
    }
    public void EndTest() => IsTesting = false;
}

/// <summary>入力テスト用。音声バッファを保持せず、直近の振幅だけを読み出す。終了後の旧コールバックは無視する。</summary>
public sealed class InputLevelSession
{
    readonly object _sync = new();
    bool _open = true;
    bool _received;
    int _peak;
    public bool IsOpen { get { lock (_sync) return _open; } }
    public void Add(ReadOnlySpan<byte> pcm)
    {
        lock (_sync)
        {
            if (!_open) return;
            _received = true;
            _peak = Math.Max(_peak, SilenceDetector.MeasurePeak(pcm));
        }
    }
    public int? ReadPeak()
    {
        lock (_sync)
        {
            if (!_open || !_received) return null;
            int value = _peak;
            _peak = 0;
            return value;
        }
    }
    public void Close()
    {
        lock (_sync) { _open = false; _peak = 0; _received = false; }
    }
}