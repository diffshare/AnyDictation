using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnyDictation.App;
using Xunit;
using Xunit.Abstractions;

namespace AnyDictation.E2E;

// README 用に、状態表示の主な表示をテストプロセス内で描いて PNG に保存する。
// 文言は DictationController が出すものに合わせた見本で、実際の録音や通信はしない。
public sealed class StatusWindowShotTests(ITestOutputHelper output)
{
    const string RecordingDetail = "もう一度 Ctrl+Win で停止して文字起こしします。(Enter: 貼り付け後に改行 / Esc: 取消)";
    const string LiveNote = "\nLive: 録音中から音声を送信しています。取消しても送信済みの音声は取り消せません。";
    const string LiveText = "明日の打ち合わせは午後 3 時からに変更になりました。資料は前日までに共有します";

    // 入力レベルの見本(右端が最新)
    static readonly int[] Peaks = [300, 900, 2500, 6000, 12000, 8000, 15000, 22000, 9000, 4000, 11000, 18000, 7000, 3000];

    static string Cost(decimal seconds) => LiveCost.Describe([new LiveCostSnapshot(seconds, 0.006m)]);

    static readonly (string Name, StatusView View)[] Samples =
    [
        ("status-recording", new(StatusKind.Recording, "録音中 0:07 / 5:00", RecordingDetail, CanCancel: true)),
        ("status-recording-live", new(StatusKind.Recording, "録音中 0:07 / 5:00", RecordingDetail + LiveNote, CanCancel: true,
            LiveText: LiveText, CostText: Cost(7))),
        ("status-recognizing", new(StatusKind.Recognizing, "認識中… (Azure MAI)", CanAbort: true)),
        ("status-recognizing-live", new(StatusKind.Recognizing, "認識中… (Azure OpenAI Live)", CanAbort: true,
            LiveText: LiveText + "。", CostText: Cost(8.4m))),
        ("status-pasted", new(StatusKind.Success, "貼り付けました")),
        ("status-copied", new(StatusKind.Warning, "クリップボードにコピーしました(貼り付けていません)",
            "録音終了後に別のウィンドウへ移動したため、自動貼り付けしませんでした。貼り付け先で Ctrl+V してください。", CanClose: true)),
        ("status-no-voice", new(StatusKind.Warning, "音声が検出されませんでした",
            "マイクが無音でした。ミュートや入力デバイス、Windows のマイク権限を確認してください。音声は送信していません。この表示は 8 秒後に消えます。",
            CanClose: true)),
        ("status-failed", new(StatusKind.Failed, "認識に失敗しました",
            "応答の受信がタイムアウトしました。再送してください。\n音声は保持しています。再送または破棄を選んでください。", CanRetry: true)),
        ("status-cancelled", new(StatusKind.Idle, "録音を取り消しました", "音声は送信せず破棄しました。")),
    ];

    [GuiFact]
    public void SavesScreenshotsOfEachStatus()
    {
        string dir = Environment.GetEnvironmentVariable("ANYDICTATION_E2E_SHOTS") is { Length: > 0 } custom ? custom
            : Path.Combine(Path.GetTempPath(), "AnyDictation.E2E", "status-shots");
        Directory.CreateDirectory(dir);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                AppTheme.Initialize(app, ThemePreference.Light);
                var status = new StatusWindow();
                status.Present(new StatusView(StatusKind.Idle, "準備中"));
                Pump(TimeSpan.FromSeconds(1)); // 初回の表示と、背景効果を外す処理を待つ
                foreach (var (name, view) in Samples)
                {
                    // 点滅のアニメーションを止めてから表示し直し、始まった直後(ほぼ不透明)を撮る
                    status.Present(new StatusView(StatusKind.Idle, "準備中"));
                    Pump(TimeSpan.FromMilliseconds(100));
                    status.Present(view);
                    if (view.Kind == StatusKind.Recording)
                        foreach (int peak in Peaks)
                            status.UpdateInputLevel(peak);
                    Pump(TimeSpan.FromMilliseconds(150));
                    string path = Path.Combine(dir, name + ".png");
                    Save(status, path);
                    output.WriteLine("screenshot: " + path);
                }
                status.Close();
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Status window screenshots did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        foreach (var (name, _) in Samples) Assert.True(new FileInfo(Path.Combine(dir, name + ".png")).Length > 0, name);
    }

    static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    // 影の余白を含むウィンドウ全体を、透過のまま 2 倍の解像度で描く。背後の画面は写らない。
    static void Save(Window window, string path)
    {
        const double scale = 2;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
