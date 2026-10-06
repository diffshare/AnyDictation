using Xunit;

namespace AnyDictation.Tests;

public class HotkeyDetectorTests
{
    const int LC = HotkeyDetector.VkLControl, RC = HotkeyDetector.VkRControl;
    const int LW = HotkeyDetector.VkLWin, RW = HotkeyDetector.VkRWin;
    const int KeyC = 0x43, Left = 0x25, Shift = 0xA0;

    /// <summary>(vk, down) の列を流し、Toggle 回数と InjectMask 回数を返す。</summary>
    static (int toggles, int masks) Run(HotkeyDetector d, params (int vk, bool down)[] events)
    {
        int t = 0, m = 0;
        foreach (var (vk, down) in events)
        {
            var r = d.Process(vk, down);
            if (r.Toggle) t++;
            if (r.InjectMask) m++;
        }
        return (t, m);
    }

    [Theory]
    [InlineData(LC, LW)]
    [InlineData(LC, RW)]
    [InlineData(RC, LW)]
    [InlineData(RC, RW)]
    public void 押順とリリース順の全組み合わせで一回だけ発火する(int ctrl, int win)
    {
        // Ctrl 先 → Win、Win 先 → Ctrl、それぞれ両方のリリース順
        foreach (var (first, second) in new[] { (ctrl, win), (win, ctrl) })
            foreach (var releaseFirst in new[] { first, second })
            {
                int releaseSecond = releaseFirst == first ? second : first;
                var r = Run(new HotkeyDetector(),
                    (first, true), (second, true), (releaseFirst, false), (releaseSecond, false));
                Assert.Equal((1, 1), r);
            }
    }

    [Fact]
    public void キーリピートで複数発火しない()
    {
        var r = Run(new HotkeyDetector(),
            (LC, true), (LC, true), (LC, true),
            (LW, true), (LW, true), (LW, true), (LC, true), (LW, true),
            (LW, false), (LC, false));
        Assert.Equal((1, 1), r);
    }

    [Fact]
    public void 発火は離した時点で成立中は発火しない()
    {
        var d = new HotkeyDetector();
        Assert.Equal((0, 1), Run(d, (LC, true), (LW, true)));
        Assert.Equal((1, 0), Run(d, (LW, false)));
    }

    [Fact]
    public void 発火後に片方を押したままもう片方を押し直しても再発火しない()
    {
        var d = new HotkeyDetector();
        Assert.Equal((1, 1), Run(d, (LC, true), (LW, true), (LC, false)));
        Assert.Equal((0, 0), Run(d, (RC, true), (RC, false)));
        Assert.Equal((0, 0), Run(d, (LW, false)));
    }

    [Fact]
    public void 全キーを離せば次の押下でまた発火する()
    {
        var d = new HotkeyDetector();
        var r = Run(d,
            (LC, true), (LW, true), (LW, false), (LC, false),
            (RW, true), (RC, true), (RC, false), (RW, false));
        Assert.Equal((2, 2), r);
    }

    [Theory]
    [InlineData(KeyC)]
    [InlineData(Left)]
    [InlineData(Shift)]
    public void 成立後に他のキーが押されたら発火しない(int other)
    {
        var r = Run(new HotkeyDetector(), (LC, true), (LW, true), (other, true), (other, false), (LW, false), (LC, false));
        Assert.Equal(0, r.toggles);
    }

    const int LAlt = 0xA4, RAlt = 0xA5, RShift = 0xA1;

    [Fact]
    public void ShiftやAltを先に押してからCtrlとWinを押しても発火もマスクもしない()
    {
        // Shift→Ctrl→Win
        Assert.Equal((0, 0), Run(new HotkeyDetector(), (Shift, true), (LC, true), (LW, true), (LW, false), (LC, false), (Shift, false)));
        // Alt→Win→Ctrl
        Assert.Equal((0, 0), Run(new HotkeyDetector(), (LAlt, true), (LW, true), (LC, true), (LC, false), (LW, false), (LAlt, false)));
        // 右 Shift / AltGr(右Alt) / Ctrl→Shift→Win
        Assert.Equal((0, 0), Run(new HotkeyDetector(), (RShift, true), (RW, true), (RC, true), (RC, false), (RW, false), (RShift, false)));
        Assert.Equal((0, 0), Run(new HotkeyDetector(), (RAlt, true), (LC, true), (RW, true), (RW, false), (LC, false), (RAlt, false)));
        Assert.Equal((0, 0), Run(new HotkeyDetector(), (LC, true), (Shift, true), (LW, true), (LW, false), (LC, false), (Shift, false)));
    }

    [Fact]
    public void 通常キーを押したままCtrlとWinを押しても発火しない()
    {
        Assert.Equal((0, 0), Run(new HotkeyDetector(), (KeyC, true), (LC, true), (LW, true), (LW, false), (LC, false), (KeyC, false)));
    }

    [Fact]
    public void ShiftやAltを離した後なら通常どおり発火する()
    {
        var d = new HotkeyDetector();
        Run(d, (Shift, true), (Shift, false));
        Assert.Equal((1, 1), Run(d, (LC, true), (LW, true), (LW, false), (LC, false)));
        // Shift 中の失敗の直後も、全キー解放後は復帰する
        Assert.Equal((0, 0), Run(d, (LAlt, true), (LW, true), (LC, true), (LC, false), (LW, false), (LAlt, false)));
        Assert.Equal((1, 1), Run(d, (LC, true), (LW, true), (LW, false), (LC, false)));
    }

    [Fact]
    public void 先に押したShiftが取りこぼされてもPruneで復帰する()
    {
        var d = new HotkeyDetector();
        Run(d, (Shift, true)); // up を取りこぼした想定
        d.Prune(vk => false, exceptVk: LC); // Shift は物理的に離れている
        Assert.Equal((1, 1), Run(d, (LC, true), (LW, true), (LW, false), (LC, false)));
    }

    [Fact]
    public void Pruneは処理中のキー自身を物理状態が未更新でも落とさない()
    {
        var d = new HotkeyDetector();
        Run(d, (LC, true));
        // WH_KEYBOARD_LL では Win の down 処理中、GetAsyncKeyState(Win) はまだ「離れている」を返す
        d.Prune(vk => vk == LC, exceptVk: LW);
        Assert.Equal((0, 1), Run(d, (LW, true)));
        // 次のイベント(Win の up)で Win は物理的にまだ押下中。Ctrl は押下中のまま
        d.Prune(vk => true, exceptVk: LW);
        Assert.Equal((1, 0), Run(d, (LW, false)));
    }

    [Fact]
    public void 他のキーを挟んだ後に揃っても発火しない_CtrlC後にWin()
    {
        var r = Run(new HotkeyDetector(), (LC, true), (KeyC, true), (KeyC, false), (LW, true), (LW, false), (LC, false));
        Assert.Equal((0, 0), r);
    }

    [Fact]
    public void 修飾キー無しの通常キーは何も起こさない()
    {
        Assert.Equal((0, 0), Run(new HotkeyDetector(), (KeyC, true), (KeyC, false), (Left, true), (Left, false)));
    }

    [Theory]
    [InlineData(LC)]
    [InlineData(RC)]
    [InlineData(LW)]
    [InlineData(RW)]
    public void 単独のCtrlやWinは発火もマスクもしない(int vk)
    {
        Assert.Equal((0, 0), Run(new HotkeyDetector(), (vk, true), (vk, true), (vk, false)));
    }

    [Fact]
    public void 同じ側のCtrl二つやWin二つだけでは成立しない()
    {
        Assert.Equal((0, 0), Run(new HotkeyDetector(), (LC, true), (RC, true), (RC, false), (LC, false)));
        Assert.Equal((0, 0), Run(new HotkeyDetector(), (LW, true), (RW, true), (RW, false), (LW, false)));
    }

    [Fact]
    public void upを取りこぼした古い状態はPruneで除去され再び発火できる()
    {
        var d = new HotkeyDetector();
        Run(d, (LC, true), (LW, true)); // 成立後に up を取りこぼした想定
        d.Prune(vk => false, exceptVk: LC);
        d.Reset();
        Assert.Equal((1, 1), Run(d, (LC, true), (LW, true), (LW, false), (LC, false)));
    }

    [Fact]
    public void Pruneは物理的に押されていないキーだけ外す()
    {
        var d = new HotkeyDetector();
        Run(d, (LC, true));
        d.Prune(vk => vk == LC, exceptVk: LW); // Ctrl は押下中のまま
        // Ctrl が残っているので Win を押すと成立する
        Assert.Equal((0, 1), Run(d, (LW, true)));
    }
}

public class SessionStateMachineTests
{
    [Fact]
    public void 録音開始と停止で認識へ進む()
    {
        var s = new SessionStateMachine();
        Assert.Equal(ToggleOutcome.Started, s.Toggle());
        Assert.Equal(SessionState.Recording, s.State);
        Assert.Equal(ToggleOutcome.Stopped, s.Toggle());
        Assert.Equal(SessionState.Recognizing, s.State);
    }

    [Fact]
    public void 認識中は新しい録音を始めない()
    {
        var s = new SessionStateMachine();
        s.Toggle(); s.Toggle();
        Assert.Equal(ToggleOutcome.BusyRecognizing, s.Toggle());
        Assert.Equal(SessionState.Recognizing, s.State);
    }

    [Fact]
    public void 失敗後は再送か破棄するまで新しい録音を始めない()
    {
        var s = new SessionStateMachine();
        s.Toggle(); s.Toggle();
        Assert.True(s.RecognitionFailed());
        Assert.Equal(ToggleOutcome.RetryPending, s.Toggle());
        Assert.Equal(SessionState.RetryPending, s.State);
        Assert.True(s.Retry());
        Assert.Equal(SessionState.Recognizing, s.State);
        Assert.True(s.RecognitionFailed());
        Assert.True(s.Discard());
        Assert.Equal(ToggleOutcome.Started, s.Toggle());
    }

    [Fact]
    public void 取消は録音中だけ有効でIdleへ戻る()
    {
        var s = new SessionStateMachine();
        Assert.False(s.CancelRecording());
        s.Toggle();
        Assert.True(s.CancelRecording());
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public void 不正な遷移は拒否される()
    {
        var s = new SessionStateMachine();
        Assert.False(s.Retry());
        Assert.False(s.Discard());
        Assert.False(s.RecognitionSucceeded());
        Assert.False(s.RecognitionFailed());
        s.Toggle();
        Assert.False(s.Retry());
        Assert.False(s.RecognitionSucceeded());
    }

    [Fact]
    public void 認識成功でIdleへ戻る()
    {
        var s = new SessionStateMachine();
        s.Toggle(); s.Toggle();
        Assert.True(s.RecognitionSucceeded());
        Assert.Equal(SessionState.Idle, s.State);
    }
}

public class PasteDeciderTests
{
    const long Target = 1000;

    static PasteContext Ctx(long fg = Target, bool changed = false, TargetElevation el = TargetElevation.NotElevated, bool tracking = true)
        => new(Target, fg, changed, el, tracking);

    [Fact]
    public void 元のウィンドウが前面のままなら貼り付ける()
    {
        Assert.Equal(PasteVerdict.Paste, PasteDecider.Decide(Ctx()).Verdict);
    }

    [Fact]
    public void 前面が別ウィンドウならコピーのみ()
    {
        var d = PasteDecider.Decide(Ctx(fg: 2000));
        Assert.Equal((PasteVerdict.CopyOnly, CopyReason.ForegroundChanged), (d.Verdict, d.Reason));
    }

    [Fact]
    public void 途中で別アプリへ移り元に戻っていても貼り付けない()
    {
        var d = PasteDecider.Decide(Ctx(changed: true));
        Assert.Equal((PasteVerdict.CopyOnly, CopyReason.ForegroundChanged), (d.Verdict, d.Reason));
    }

    [Fact]
    public void 管理者権限の貼り付け先はコピーのみで理由が説明される()
    {
        var d = PasteDecider.Decide(Ctx(el: TargetElevation.Elevated));
        Assert.Equal(CopyReason.TargetElevated, d.Reason);
        Assert.Contains("管理者", d.Describe());
    }

    [Fact]
    public void 権限を判定できない場合もコピーのみで理由が説明される()
    {
        var d = PasteDecider.Decide(Ctx(el: TargetElevation.Unknown));
        Assert.Equal((PasteVerdict.CopyOnly, CopyReason.ElevationUnknown), (d.Verdict, d.Reason));
        Assert.Contains("判定できなかった", d.Describe());
    }

    [Fact]
    public void 前面ウィンドウの監視が使えないなら貼り付けずに理由を示す()
    {
        // 見かけ上は元のウィンドウが前面で移動なしでも、監視が無ければ「途中の移動なし」を保証できない
        var d = PasteDecider.Decide(Ctx(tracking: false));
        Assert.Equal((PasteVerdict.CopyOnly, CopyReason.TrackingUnavailable), (d.Verdict, d.Reason));
        Assert.Contains("監視", d.Describe());
    }

    [Fact]
    public void 貼り付け先不明はコピーのみ()
    {
        var d = PasteDecider.Decide(new(0, 0, false, TargetElevation.NotElevated, true));
        Assert.Equal(CopyReason.NoTarget, d.Reason);
    }

    // 権限判定: API 失敗を「昇格していない」にしない。こちらが昇格済みでも自分の判定失敗は Unknown
    [Theory]
    [InlineData(false, false, TargetElevation.NotElevated)]
    [InlineData(false, true, TargetElevation.Elevated)]
    [InlineData(false, null, TargetElevation.Unknown)]   // 相手の判定に失敗
    [InlineData(null, false, TargetElevation.Unknown)]   // 自分の判定に失敗: 「自分が昇格済みなら許可」の経路へ入らない
    [InlineData(null, true, TargetElevation.Unknown)]
    [InlineData(null, null, TargetElevation.Unknown)]
    [InlineData(true, false, TargetElevation.NotElevated)]
    [InlineData(true, true, TargetElevation.NotElevated)] // 双方昇格済みなら UIPI の問題はない
    [InlineData(true, null, TargetElevation.NotElevated)]
    public void ElevationPolicyはAPI失敗をUnknownにする(bool? we, bool? target, TargetElevation expected)
    {
        Assert.Equal(expected, ElevationPolicy.Evaluate(we, target));
    }
}

public class AudioTests
{
    [Fact]
    public void 無音と有音をピークで判定する()
    {
        var quiet = new SilenceDetector();
        quiet.Add(new byte[] { 10, 0, 0xF6, 0xFF, 5, 0 }); // 10, -10, 5
        Assert.True(quiet.IsSilent());

        var loud = new SilenceDetector();
        loud.Add(new byte[] { 0, 0, 0x00, 0x10 }); // 0, 4096
        Assert.False(loud.IsSilent());
        Assert.Equal(4096, loud.Peak);
    }

    [Fact]
    public void WAVヘッダーが正しい()
    {
        var pcm = new byte[3200];
        var wav = WavEncoder.Encode(pcm, 16000, 16, 1);
        Assert.Equal(44 + 3200, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(32000, BitConverter.ToInt32(wav, 28));
        Assert.Equal(3200, BitConverter.ToInt32(wav, 40));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
    }
}

public class HotkeyHoldTests
{
    const int LC = HotkeyDetector.VkLControl, LW = HotkeyDetector.VkLWin, RW = HotkeyDetector.VkRWin;
    const int KeyC = 0x43, Shift = 0xA0;
    const long Hold = HotkeyDetector.HoldThresholdMs;

    [Fact]
    public void しきい値に満たない押下は従来どおり離したときにToggleする()
    {
        var d = new HotkeyDetector();
        d.Process(LC, true, 1000);
        d.Process(LW, true, 1100);
        Assert.Equal(default, d.Tick(1100 + Hold - 1));
        var r = d.Process(LW, false, 1100 + Hold - 1);
        Assert.True(r.Toggle);
        Assert.False(r.HoldEnd);
    }

    [Fact]
    public void Tickの前にしきい値を超えて離したら短押しとして扱わず何も返さない()
    {
        var d = new HotkeyDetector();
        d.Process(LC, true, 0);
        d.Process(LW, true, 0);
        Assert.Equal(default, d.Process(LW, false, Hold + 10)); // タイマー(しきい値+20ms)より先に離した
        Assert.False(d.Process(RW, true, Hold + 20).InjectMask); // Ctrl を押したままの押し直しでも再成立しない
        Assert.Equal(default, d.Tick(Hold * 2));
    }

    [Fact]
    public void しきい値以上押し続けるとHoldStartになり離したときはToggleではなくHoldEnd()
    {
        var d = new HotkeyDetector();
        d.Process(LC, true, 1000);
        d.Process(LW, true, 1100);
        var start = d.Tick(1100 + Hold);
        Assert.True(start.HoldStart);
        Assert.False(start.Toggle);
        Assert.Equal(default, d.Tick(1100 + Hold + 50)); // HoldStart は 1 回だけ

        var end = d.Process(LC, false, 3000);
        Assert.True(end.HoldEnd);
        Assert.False(end.Toggle);
        Assert.Equal(default, d.Process(LW, false, 3010)); // 残りを離しても何も起きない
    }

    [Fact]
    public void 長押しの後は全部離せば次の押下で再び使える()
    {
        var d = new HotkeyDetector();
        d.Process(LC, true, 0);
        d.Process(LW, true, 0);
        d.Tick(Hold);
        d.Process(LW, false, 2000);
        d.Process(LC, false, 2000);
        Assert.True(d.Process(RW, true, 3000).InjectMask == false); // Ctrl が無いので成立しない
        d.Process(RW, false, 3000);
        d.Process(LC, true, 4000);
        Assert.True(d.Process(LW, true, 4000).InjectMask);
        Assert.True(d.Process(LW, false, 4100).Toggle);
    }

    [Fact]
    public void 長押しの途中で片方を押し直しても再成立しない()
    {
        var d = new HotkeyDetector();
        d.Process(LC, true, 0);
        d.Process(LW, true, 0);
        d.Tick(Hold);
        Assert.True(d.Process(LW, false, 1000).HoldEnd);
        Assert.False(d.Process(RW, true, 1100).InjectMask);
        Assert.Equal(default, d.Tick(1100 + Hold));
    }

    [Fact]
    public void しきい値の前に他のキーが押されたら長押しにならず発火もしない()
    {
        var d = new HotkeyDetector();
        d.Process(LC, true, 0);
        d.Process(LW, true, 0);
        d.Process(KeyC, true, 100);
        Assert.Equal(default, d.Tick(Hold + 100));
        Assert.Equal(default, d.Process(LW, false, 2000));
    }

    [Fact]
    public void 先にShiftが押されていたら長押しにならない()
    {
        var d = new HotkeyDetector();
        d.Process(Shift, true, 0);
        d.Process(LC, true, 0);
        d.Process(LW, true, 0);
        Assert.Equal(default, d.Tick(Hold * 2));
    }

    [Fact]
    public void 長押しの成立後に他のキーが押されてもHoldEndは返す()
    {
        var d = new HotkeyDetector();
        d.Process(LC, true, 0);
        d.Process(LW, true, 0);
        d.Tick(Hold);
        Assert.Equal(default, d.Process(KeyC, true, 1000));
        Assert.True(d.Process(LC, false, 1100).HoldEnd);
    }

    [Fact]
    public void 成立していなければTickは何もしない()
    {
        var d = new HotkeyDetector();
        Assert.Equal(default, d.Tick(10_000));
        d.Process(LC, true, 0);
        Assert.Equal(default, d.Tick(10_000)); // Ctrl だけ
    }

    [Fact]
    public void Resetで長押しの状態も消える()
    {
        var d = new HotkeyDetector();
        d.Process(LC, true, 0);
        d.Process(LW, true, 0);
        d.Tick(Hold);
        d.Reset();
        Assert.Equal(default, d.Process(LW, false, 1000));
    }
}

public class HoldRecordingGuardTests
{
    [Fact]
    public void 長押しが対象にした録音が続いていれば停止する()
    {
        var g = new HoldRecordingGuard();
        g.Begin(1);
        Assert.True(g.ShouldStopOnRelease(1, recording: true));
    }

    [Fact]
    public void 長押しの途中で取消され別の録音が始まっていてもその録音は止めない()
    {
        var g = new HoldRecordingGuard();
        g.Begin(1);
        Assert.False(g.ShouldStopOnRelease(2, recording: true));
    }

    [Fact]
    public void 録音が終わっていたり対象がなければ停止しない()
    {
        var g = new HoldRecordingGuard();
        g.Begin(1);
        Assert.False(g.ShouldStopOnRelease(1, recording: false));
        Assert.False(g.ShouldStopOnRelease(1, recording: true)); // 一度の解放で消費済み
        g.Begin(0);
        Assert.False(g.ShouldStopOnRelease(0, recording: true));
    }
}

public class RecordingKeyFilterTests
{
    const int Esc = RecordingKeyFilter.VkEscape, Enter = RecordingKeyFilter.VkReturn, KeyA = 0x41;

    [Fact]
    public void 録音中のEscは取消で捕捉しupも捕捉する()
    {
        var f = new RecordingKeyFilter();
        Assert.Equal(new RecordingKeyResult(RecordingKeyAction.Cancel, true), f.Process(Esc, true, recording: true));
        // 取消で録音が終わった後に up が届いても、down と対にして捕捉する
        Assert.Equal(new RecordingKeyResult(RecordingKeyAction.None, true), f.Process(Esc, false, recording: false));
        // 対応を使い切った後の up は通す
        Assert.Equal(default, f.Process(Esc, false, recording: false));
    }

    [Fact]
    public void 録音中のEnterは確定で捕捉しキーリピートでは繰り返さない()
    {
        var f = new RecordingKeyFilter();
        Assert.Equal(new RecordingKeyResult(RecordingKeyAction.Submit, true), f.Process(Enter, true, true));
        Assert.Equal(new RecordingKeyResult(RecordingKeyAction.None, true), f.Process(Enter, true, true));
        Assert.Equal(new RecordingKeyResult(RecordingKeyAction.None, true), f.Process(Enter, true, false));
        Assert.Equal(new RecordingKeyResult(RecordingKeyAction.None, true), f.Process(Enter, false, false));
    }

    [Theory]
    [InlineData(Esc)]
    [InlineData(Enter)]
    public void 録音中でなければ何もせず通す(int vk)
    {
        var f = new RecordingKeyFilter();
        Assert.Equal(default, f.Process(vk, true, recording: false));
        Assert.Equal(default, f.Process(vk, false, recording: false));
    }

    [Fact]
    public void 録音前に押したキーのupは録音が始まっても通す()
    {
        var f = new RecordingKeyFilter();
        Assert.Equal(default, f.Process(Enter, true, recording: false));
        Assert.Equal(default, f.Process(Enter, false, recording: true));
    }

    [Fact]
    public void 録音前に通したdownのキーリピートは録音が始まっても通し動作しない()
    {
        var f = new RecordingKeyFilter();
        Assert.Equal(default, f.Process(Enter, true, recording: false));
        Assert.Equal(default, f.Process(Enter, true, recording: true)); // トレイから録音を始めた後のリピート
        Assert.Equal(default, f.Process(Enter, false, recording: true));
        // up の後の新しい down は通常どおり捕捉する
        Assert.Equal(RecordingKeyAction.Submit, f.Process(Enter, true, recording: true).Action);
    }

    [Fact]
    public void 取消の通知の表示中は録音中でなくてもEscで閉じ_リピートとupも捕捉する()
    {
        var f = new RecordingKeyFilter();
        Assert.Equal(new RecordingKeyResult(RecordingKeyAction.Dismiss, true), f.Process(Esc, true, recording: false, dismissible: true));
        // 通知が消えた後でも、対になるリピートと up は捕捉する
        Assert.Equal(new RecordingKeyResult(RecordingKeyAction.None, true), f.Process(Esc, true, false, dismissible: false));
        Assert.Equal(new RecordingKeyResult(RecordingKeyAction.None, true), f.Process(Esc, false, false, dismissible: false));
        Assert.Equal(default, f.Process(Esc, false, false));
    }

    [Fact]
    public void 通知が表示されていなければEscは通す()
    {
        var f = new RecordingKeyFilter();
        Assert.Equal(default, f.Process(Esc, true, recording: false, dismissible: false));
        // 通した down のリピートは、通知が出た後でも通す(閉じない)
        Assert.Equal(default, f.Process(Esc, true, false, dismissible: true));
        Assert.Equal(default, f.Process(Esc, false, false, dismissible: true));
    }

    [Fact]
    public void 録音中のEscは通知の表示中でも取消を優先する()
    {
        var f = new RecordingKeyFilter();
        Assert.Equal(RecordingKeyAction.Cancel, f.Process(Esc, true, recording: true, dismissible: true).Action);
    }

    [Fact]
    public void 通知の表示中でもEnterには影響しない()
    {
        var f = new RecordingKeyFilter();
        Assert.Equal(default, f.Process(Enter, true, recording: false, dismissible: true));
    }

    [Fact]
    public void 他のキーは録音中でも通す()
    {
        var f = new RecordingKeyFilter();
        Assert.Equal(default, f.Process(KeyA, true, true));
        Assert.Equal(default, f.Process(KeyA, false, true));
    }

    [Fact]
    public void Resetで捕捉の記録が消える()
    {
        var f = new RecordingKeyFilter();
        f.Process(Esc, true, true);
        f.Reset();
        Assert.Equal(default, f.Process(Esc, false, false));
    }
}
