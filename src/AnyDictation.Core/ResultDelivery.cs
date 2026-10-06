namespace AnyDictation;

/// <summary>貼り付けの実行環境(実体は Win32 / WPF)。テストでは fake を使う。</summary>
public interface IDeliveryEnvironment
{
    bool Exiting { get; }
    Task<bool> SetClipboardAsync(string text);
    Task<bool> WaitForModifierReleaseAsync();
    PasteContext CaptureContext();
    bool SendPaste();
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

public readonly record struct DeliveryResult(DeliveryOutcome Outcome, PasteDecision Decision);

/// <summary>
/// 認識成功後の配送。ここで何が起きても API の再送状態にはしない(課金の重複を防ぐ)。
/// 各 await の直後と貼り付けキー送信の直前に終了を確認し、中止が先に要求されていた結果は貼り付けない。
/// </summary>
public static class ResultDelivery
{
    public static async Task<DeliveryResult> RunAsync(IDeliveryEnvironment env, string text, bool userAborted)
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
        return env.SendPaste()
            ? R(DeliveryOutcome.Pasted, decision)
            : R(DeliveryOutcome.PasteSendFailed, decision);
    }
}
