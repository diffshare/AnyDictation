using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using AnyDictation.App;
using Xunit;
using Xunit.Abstractions;

namespace AnyDictation.E2E;

public sealed class HookFactAttribute : FactAttribute
{
    public HookFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ANYDICTATION_RUN_HOOK_E2E") != "1")
            Skip = "実フックのテストは ANYDICTATION_RUN_HOOK_E2E=1 の明示実行のみです(デスクトップへ Ctrl+Win を数回注入します)。";
    }
}

/// <summary>
/// 本物の WH_KEYBOARD_LL に Ctrl+Win を注入して、UI スレッドが数秒止まってもホットキーが届くことを確認する。
/// 起動中の本番アプリへ影響させないため、注入するキーには本番のフックが無視する OwnMarker を付け、
/// テスト側のフックだけが別の印を無視する(テスト自身のマスクキー送出を除くため)。
/// マスクキー(0xE8)が Ctrl と Win の間に入るので、Win メニューも開かない。
/// </summary>
public sealed class KeyboardHookTests : IDisposable
{
    static readonly UIntPtr TestHookMarker = new(0x54455354); // "TEST"
    const ushort LCtrl = 0xA2, LWin = 0x5B;
    readonly string _dataDir;
    readonly ITestOutputHelper _output;

    public KeyboardHookTests(ITestOutputHelper output)
    {
        _output = output;
        // フックのログが本番の log.txt へ入らないよう、専用の隔離ディレクトリを使う
        string id = Guid.NewGuid().ToString("N");
        _dataDir = Path.Combine(Path.GetTempPath(), "AnyDictation.E2E", id);
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(Path.Combine(_dataDir, ".anydictation-e2e"), id);
        if (E2eMode.Enabled) return;
        Assert.True(E2eMode.Configure([$"--e2e-data-dir={_dataDir}"]));
    }

    public void Dispose()
    {
        // E2eMode の保存先は静的なので、最初のテストの隔離ディレクトリが残る。ログの確認用に残す。
    }

    sealed class Run : IDisposable
    {
        public readonly Thread UiThread;
        public Dispatcher Ui = null!;
        public readonly ConcurrentQueue<(long Ticks, int ThreadId)> Toggles = new();
        public KeyboardHook Hook = null!;

        public Run()
        {
            using var started = new ManualResetEventSlim();
            UiThread = new Thread(() => { Ui = Dispatcher.CurrentDispatcher; started.Set(); Dispatcher.Run(); }) { IsBackground = true };
            UiThread.SetApartmentState(ApartmentState.STA);
            UiThread.Start();
            started.Wait();
            Ui.Invoke(() =>
            {
                Hook = new KeyboardHook(TestHookMarker, () => KeyboardInput.SendMaskKey(TestHookMarker));
                Hook.Toggled += () => Toggles.Enqueue((Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId));
                Hook.Install(); // 本番と同じく UI スレッドから設定する
            });
        }

        public void Dispose()
        {
            Ui.Invoke(() => Hook.Dispose());
            Ui.InvokeShutdown();
            UiThread.Join(2000);
        }
    }

    static void RequireNoPhysicalModifier()
    {
        var wait = Stopwatch.StartNew();
        while (KeyboardInput.AnyModifierDown())
        {
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(10), "手元で修飾キーが押されています。離してから実行してください。");
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Ctrl↓ Win↓ マスク Ctrl↑ Win↑。Ctrl と Win は OwnMarker 付きなので本番のフックは無視する。
    /// マスク(0xE8)はテスト入力側で送る。検出側のフックが無い・止まっている場合でも、Win の解放で Win メニューを開かせないため。
    /// 印は TestHookMarker で、テストのフックは無視する(無視しないと「他のキー」として成立を取り消す)。
    /// </summary>
    static void PressChord()
    {
        RequireNoPhysicalModifier();
        try
        {
            Send(LCtrl, false);
            Send(LWin, false);
            SendMask();
            Send(LCtrl, true);
            Send(LWin, true);
        }
        finally { ReleaseAll(); }
    }

    static void SendMask()
    {
        Send(KeyboardInput.VK_MASK, false, TestHookMarker);
        Send(KeyboardInput.VK_MASK, true, TestHookMarker);
    }

    /// <summary>途中の失敗でもデスクトップに Ctrl / Win を押したまま残さない。余分な up は無害。</summary>
    static void ReleaseAll()
    {
        foreach (var vk in new[] { LCtrl, LWin })
            SendInput(1, [new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = 2, dwExtraInfo = KeyboardInput.OwnMarker } }], Marshal.SizeOf<INPUT>());
    }

    static void Send(ushort vk, bool up, UIntPtr? marker = null)
    {
        var input = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u, dwExtraInfo = marker ?? KeyboardInput.OwnMarker } };
        Assert.Equal(1u, SendInput(1, [input], Marshal.SizeOf<INPUT>()));
        Thread.Sleep(30);
    }

    static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    [HookFact]
    public void HotkeyIsDetectedWhileTheUiThreadIsBlocked()
    {
        using var run = new Run();
        PressChord();
        Assert.True(WaitUntil(() => run.Toggles.Count == 1, TimeSpan.FromSeconds(2)), "UI が空いている状態で検出できない(テスト環境の問題)");

        using var blocked = new ManualResetEventSlim();
        run.Ui.BeginInvoke(() =>
        {
            blocked.Set();
            Thread.Sleep(4000); // 録音開始や貼り付けが UI を数秒占有する状況
        });
        Assert.True(blocked.Wait(TimeSpan.FromSeconds(2)), "UI スレッドがブロックに入らなかった");
        var blockEnds = Stopwatch.StartNew(); // UI がブロックに入った時点から
        PressChord();
        bool during = WaitUntil(() => run.Toggles.Count == 2, TimeSpan.FromSeconds(2));
        _output.WriteLine($"UI ブロック {blockEnds.ElapsedMilliseconds} ms 経過時点で検出済み={during}");
        Assert.True(during, "UI スレッドのブロック中にホットキーを検出できなかった");
        Assert.True(blockEnds.ElapsedMilliseconds < 3800, "検出がブロック中ではない(テストの前提が崩れている)");
        Assert.All(run.Toggles, t => Assert.NotEqual(run.UiThread.ManagedThreadId, t.ThreadId)); // 通知はフックスレッドで届く

        Thread.Sleep(Math.Max(0, 4500 - (int)blockEnds.ElapsedMilliseconds)); // ブロックが終わるまで待つ
        PressChord();
        Assert.True(WaitUntil(() => run.Toggles.Count == 3, TimeSpan.FromSeconds(2)), "UI のブロックが解けた後にフックが外れている");
    }

    [HookFact]
    public void ResetFromAnotherThreadClearsHeldKeysOnTheHookThread()
    {
        using var run = new Run();
        RequireNoPhysicalModifier();
        try
        {
            Send(LCtrl, false);
            Send(LWin, false); // 成立(armed)
            SendMask(); // Win メニューを開かせない(テストのフックは無視する印)
            Task.Run(run.Hook.Reset).Wait(); // SessionSwitch と同じく別スレッドから
            Thread.Sleep(300);
            Send(LCtrl, true);
            Send(LWin, true);
        }
        finally { ReleaseAll(); }
        Thread.Sleep(300);
        Assert.Empty(run.Toggles); // リセットで状態が消えたので離しても発火しない
        PressChord();
        Assert.True(WaitUntil(() => run.Toggles.Count == 1, TimeSpan.FromSeconds(2)), "リセット後に検出できない");
    }

    [HookFact]
    public void DisposeRemovesTheHookAndStopsTheThread()
    {
        // 静的な E2eMode のログは前のテストと共有なので、この Run の開始前の長さ以降だけを証拠にする(キー注入は不要)
        long before = File.Exists(AppPaths.LogFile) ? new FileInfo(AppPaths.LogFile).Length : 0;
        var run = new Run();
        run.Ui.Invoke(() => run.Hook.Dispose()); // フックスレッドの終了(解除)まで待つ
        string log = File.ReadAllText(AppPaths.LogFile)[(int)before..];
        Assert.Contains("keyboard hook started", log);
        Assert.Contains("keyboard hook stopped", log);
        Assert.DoesNotContain("did not stop", log);
        run.Ui.Invoke(() => run.Hook.Dispose()); // 二重 Dispose も安全
        run.Ui.InvokeShutdown();
        run.UiThread.Join(2000);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
