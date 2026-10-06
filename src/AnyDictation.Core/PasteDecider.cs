namespace AnyDictation;

public enum PasteVerdict { Paste, CopyOnly }

public enum CopyReason { None, NoTarget, TrackingUnavailable, ForegroundChanged, TargetElevated, ElevationUnknown, ModifierHeld }

/// <summary>貼り付け先プロセスの権限。判定 API が失敗した場合は NotElevated ではなく Unknown にする。</summary>
public enum TargetElevation { NotElevated, Elevated, Unknown }

public static class ElevationPolicy
{
    /// <summary>
    /// weAreElevated / targetIsElevated は判定 API の結果(null は API 失敗)。
    /// こちらが管理者権限なら UIPI で弾かれないので NotElevated。判定が一つでも失敗したら Unknown(貼り付けない側へ倒す)。
    /// </summary>
    public static TargetElevation Evaluate(bool? weAreElevated, bool? targetIsElevated)
    {
        if (weAreElevated is null) return TargetElevation.Unknown;
        if (weAreElevated == true) return TargetElevation.NotElevated;
        if (targetIsElevated is null) return TargetElevation.Unknown;
        return targetIsElevated == true ? TargetElevation.Elevated : TargetElevation.NotElevated;
    }
}

public readonly record struct PasteContext(
    long TargetWindow,
    long ForegroundNow,
    bool ForegroundChangedSinceStop,
    TargetElevation Elevation,
    bool TrackingAvailable);

public readonly record struct PasteDecision(PasteVerdict Verdict, CopyReason Reason)
{
    public string Describe() => Reason switch
    {
        CopyReason.NoTarget => "貼り付け先のウィンドウを特定できなかったため",
        CopyReason.TrackingUnavailable => "前面ウィンドウの監視を開始できず、途中の移動を検知できないため",
        CopyReason.ForegroundChanged => "録音終了後に別のウィンドウへ移動したため",
        CopyReason.TargetElevated => "貼り付け先が管理者権限のアプリで、通常権限からは入力できないため",
        CopyReason.ElevationUnknown => "貼り付け先の権限を判定できなかったため",
        CopyReason.ModifierHeld => "Ctrl/Win キーが離されなかったため",
        _ => "",
    };
}

public static class PasteDecider
{
    /// <summary>録音終了時の前面ウィンドウが今も前面で、その間に一度も別ウィンドウへ移っていないと確認できた場合だけ貼り付ける。</summary>
    public static PasteDecision Decide(PasteContext c)
    {
        if (c.TargetWindow == 0) return new(PasteVerdict.CopyOnly, CopyReason.NoTarget);
        if (!c.TrackingAvailable) return new(PasteVerdict.CopyOnly, CopyReason.TrackingUnavailable);
        if (c.Elevation == TargetElevation.Elevated) return new(PasteVerdict.CopyOnly, CopyReason.TargetElevated);
        if (c.Elevation == TargetElevation.Unknown) return new(PasteVerdict.CopyOnly, CopyReason.ElevationUnknown);
        if (c.ForegroundChangedSinceStop || c.ForegroundNow != c.TargetWindow)
            return new(PasteVerdict.CopyOnly, CopyReason.ForegroundChanged);
        return new(PasteVerdict.Paste, CopyReason.None);
    }
}
