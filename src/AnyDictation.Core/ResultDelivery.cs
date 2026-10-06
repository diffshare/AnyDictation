namespace AnyDictation;

/// <summary>貼り付けの実行環境(実体は Win32 / WPF)。テストでは fake を使う。</summary>
public interface IDeliveryEnvironment
{
    bool Exiting { get; }
    Task<bool> SetClipboardAsync(string text);
    Task<bool> WaitForModifierReleaseAsync();
    PasteContext CaptureContext();
    bool SendPaste();
    /// <summary>Ctrl/Shift/Alt/Win のいずれかが物理的に押されているか。</summary>
    bool ModifierHeld { get; }
    bool SendEnter();
}

public enum DeliveryOutcome
{
    Pasted,
    CopiedOnly,        // 貼り付けず、クリップボードにだけ入れた
    AbortedCopied,     // 利用者が中止を押した後に認識が完了していた: 貼り付けず、クリップボードにだけ入れた
    ClipboardFailed,
    PasteSendFailed,   // 貼り付けキーの送信に失敗(クリップボードには入っている)
    ExitingSkipped,    // 終了中のため以降の配送を行わない
}

/// <summary>EnterSent は貼り付けの後に Enter まで送れたか(pressEnter を求めなかった場合や貼り付けなかった場合は false)。</summary>
public readonly record struct DeliveryResult(DeliveryOutcome Outcome, PasteDecision Decision, bool EnterSent = false);

/// <summary>
/// 認識成功後の配送。ここで何が起きても API の再送状態にはしない(課金の重複を防ぐ)。
/// 各 await の直後と貼り付けキー送信の直前に終了を確認し、中止が先に要求されていた結果は貼り付けない。
/// </summary>
public static class ResultDelivery
{
    /// <summary>Ctrl+V から Enter までの間隔。Electron やブラウザは貼り付けを非同期に処理するため、直後の Enter が貼り付けより先に処理されるのを避ける。</summary>
    public static readonly TimeSpan PasteToEnterDelay = TimeSpan.FromMilliseconds(150);

    public static async Task<DeliveryResult> RunAsync(IDeliveryEnvironment env, string text, bool userAborted, bool pressEnter = false)
    {
        static DeliveryResult R(DeliveryOutcome o, PasteDecision d = default) => new(o, d);

        if (env.Exiting) return R(DeliveryOutcome.ExitingSkipped);
        bool copied = await env.SetClipboardAsync(text);
        if (env.Exiting) return R(DeliveryOutcome.ExitingSkipped);
        if (!copied) return R(DeliveryOutcome.ClipboardFailed);
        if (userAborted) return R(DeliveryOutcome.AbortedCopied);

        var decision = PasteDecider.Decide(env.CaptureContext());
        if (decision.Verdict == PasteVerdict.Paste)
        {
            bool released = await env.WaitForModifierReleaseAsync();
            if (env.Exiting) return R(DeliveryOutcome.ExitingSkipped);
            decision = released
                ? PasteDecider.Decide(env.CaptureContext()) // 待っている間の移動も反映する
                : new PasteDecision(PasteVerdict.CopyOnly, CopyReason.ModifierHeld);
        }
        if (decision.Verdict != PasteVerdict.Paste) return R(DeliveryOutcome.CopiedOnly, decision);

        if (env.Exiting) return R(DeliveryOutcome.ExitingSkipped);
        if (!env.SendPaste()) return R(DeliveryOutcome.PasteSendFailed, decision);
        if (!pressEnter) return R(DeliveryOutcome.Pasted, decision);

        // 貼り付けが成功したときだけ Enter を送る(コピーのみ、貼り付け失敗、終了中では送らない)
        // 待っている間に前面が移った、または修飾キーが押された場合は、別のアプリへの Enter や Modifier+Enter を避けて送らない
        await Task.Delay(PasteToEnterDelay);
        if (env.Exiting || env.ModifierHeld || PasteDecider.Decide(env.CaptureContext()).Verdict != PasteVerdict.Paste)
            return R(DeliveryOutcome.Pasted, decision);
        return new DeliveryResult(DeliveryOutcome.Pasted, decision, env.SendEnter());
    }
}
