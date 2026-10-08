using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Xunit;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AnyDictation.E2E;

public sealed class GuiFactAttribute : FactAttribute
{
    public GuiFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ANYDICTATION_RUN_E2E") != "1")
            Skip = "明示実行には ANYDICTATION_RUN_E2E=1 と ANYDICTATION_E2E_EXE が必要です。";
    }
}

public sealed class SettingsGuiTests(ITestOutputHelper output)
{
    [GuiFact]
    public void LiveProfileWithoutRateIsSavedWhenLeavingFieldsAndPersistsAcrossRestart()
    {
        using var run = new GuiRun(output);
        run.Check(() =>
        {
            run.Start();
            Assert.False(File.Exists(run.Settings)); // 起動直後の読み込みでは書き込まない
            run.InvokeName("Azure OpenAI Live を追加");
            run.Set("NameBox", "E2E Live");
            run.Set("EndpointBox", "https://e2e.invalid/"); // 必要な項目がそろった時点で保存する
            run.WaitFor(() => File.Exists(run.Settings) && File.ReadAllText(run.Settings).Contains("E2E Live"));
            run.Set("ModelBox", "e2e-deployment");
            run.Set("LanguageBox", "ja");
            Assert.Equal("", run.Value("LiveRateBox"));
            run.WaitFor(() => File.ReadAllText(run.Settings).Contains("e2e-deployment"));
            using (var saved = JsonDocument.Parse(File.ReadAllText(run.Settings)))
            {
                var profile = saved.RootElement.GetProperty("Profiles")[0];
                Assert.Equal("E2E Live", profile.GetProperty("Name").GetString());
                Assert.Equal("ja", profile.GetProperty("Language").GetString());
                Assert.Equal(JsonValueKind.Null, profile.GetProperty("LiveUsdPerMinute").ValueKind);
            }
            run.Restart();
            Assert.Equal("E2E Live", run.Value("NameBox"));
            Assert.Equal("https://e2e.invalid/", run.Value("EndpointBox"));
            Assert.Equal("e2e-deployment", run.Value("ModelBox"));
            Assert.Equal("ja", run.Value("LanguageBox"));
            Assert.Equal("", run.Value("LiveRateBox"));
        });
    }

    [GuiFact]
    public void IncompleteProfileIsNotSavedUntilRequiredFieldsAreFilled()
    {
        using var run = new GuiRun(output);
        run.Check(() =>
        {
            run.Start();
            run.InvokeName("Azure OpenAI を追加");
            run.Set("NameBox", "E2E Draft"); // エンドポイントが空のままなので保存しない
            run.Settle();
            Assert.False(File.Exists(run.Settings));
            Assert.Contains("エンドポイント", run.Element("EndpointError").Name);
            run.Set("EndpointBox", "https://e2e.invalid/");
            run.WaitFor(() => File.Exists(run.Settings) && File.ReadAllText(run.Settings).Contains("E2E Draft"));
            Assert.False(run.Exists("EndpointError"));
        });
    }

    [GuiFact]
    public void InvalidLiveRateKeepsSavedProfileAndValidRatePersists()
    {
        using var run = new GuiRun(output);
        run.Check(() =>
        {
            run.Start();
            run.InvokeName("Azure OpenAI Live を追加");
            run.Set("EndpointBox", "https://e2e.invalid/");
            run.WaitFor(() => File.Exists(run.Settings));
            string original = File.ReadAllText(run.Settings);
            foreach (var invalid in new[] { "abc", "0", "-0.017" })
            {
                run.Set("LiveRateBox", invalid);
                run.WaitFor(() => run.Exists("LiveRateError") && run.Element("LiveRateError").Name.Contains("単価"));
                Assert.Equal(original, File.ReadAllText(run.Settings)); // 不正な値は保存せず、保存済みの内容を保つ
                Assert.Contains("保存済みの内容を使います", run.Element("ActiveText").Name);
            }
            run.Set("LiveRateBox", "0.017");
            run.WaitFor(() => File.ReadAllText(run.Settings) != original);
            Assert.False(run.Exists("LiveRateError"));
            run.Restart();
            Assert.Equal("0.017", run.Value("LiveRateBox"));
        });
    }

    [GuiFact]
    public void EmptyHistoryAndGeneralTabsAreUsableWithoutExternalActions()
    {
        using var run = new GuiRun(output);
        run.Check(() =>
        {
            run.Start();
            run.SelectName("履歴");
            Assert.Empty(run.Element("HistoryList").FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem)));
            run.SelectName("一般");
            Assert.Contains("まだ Live", run.Element("LastLiveCostText").Name);
            Assert.False(run.Element("StartupBox").IsEnabled);
            Assert.Contains(run.DataDir, run.Element("PathText").Name);
            Assert.Contains("E2E テストでは更新を確認しません", run.Element("UpdateText").Name);
            Assert.False(run.Exists("UpdateButton"));
            run.SelectName("使い方");
            run.SelectName("マイク");
            Assert.False(run.Element("MicrophoneStartButton").IsEnabled);
            Assert.False(run.Element("MicrophoneStopButton").IsEnabled);
            Assert.False(File.Exists(run.Settings)); // タブを巡っても書き込まない
        });
    }

    [GuiFact]
    public void EveryControlIsReachableOnEveryTabAtDefaultAndMinimumSize()
    {
        using var run = new GuiRun(output);
        run.Check(() =>
        {
            run.Start();
            Assert.Equal(["プロファイル", "マイク", "履歴", "一般", "使い方"], run.TabNames());
            run.InvokeName("Azure OpenAI Live を追加"); // Live の追加欄まで含めた最も縦長のフォームで確認する
            run.Shot("プロファイル-default-live");
            var controlsByTab = new (string Tab, string[] Ids)[]
            {
                ("プロファイル", ["NameBox", "ProviderBox", "EndpointBox", "ModelBox", "LanguageBox", "LiveRateBox", "KeyBox", "UseButton"]),
                ("マイク", ["MicrophoneBox", "MicrophoneStopButton"]),
                ("履歴", ["HistoryList"]),
                ("一般", ["StartupBox", "ThemeBox", "PathText", "UpdateText"]),
                ("使い方", []),
            };
            foreach (var size in new[] { "default", "min" })
            {
                if (size == "min") run.ShrinkToMinimum(); // MinWidth / MinHeight に丸められる
                foreach (var (tab, ids) in controlsByTab)
                {
                    run.SelectName(tab);
                    run.Settle();
                    foreach (var id in ids) run.AssertReachable(id);
                    run.Shot($"{tab}-{size}");
                }
            }
            run.SelectName("プロファイル");
            run.Set("LiveRateBox", "abc"); // 最小サイズでも欄の下のエラーが読める
            run.Settle();
            run.AssertReachable("LiveRateError");
            run.Shot("プロファイル-min-error");
        });
    }

    [GuiFact]
    public void UseTargetIsSavedImmediatelyAndPersists()
    {
        using var run = new GuiRun(output);
        run.Check(() =>
        {
            run.Start();
            run.InvokeName("Azure MAI を追加");
            Assert.Equal("", run.Value("EndpointBox")); // 既定値は持たず、入力例だけを表示する
            Assert.Equal("https://<resource>.cognitiveservices.azure.com/", run.Element("EndpointPlaceholder").Name);
            run.Set("NameBox", "A");
            run.Set("EndpointBox", "https://e2e.invalid/");
            Assert.False(run.Exists("EndpointPlaceholder"));
            // 使用中がない状態で最初に追加した A は、入力がそろって保存された時点で使用中になる
            run.WaitFor(() => run.Element("ActiveText").Name.Contains("使用中です"));
            run.InvokeName("OpenAI / 互換を追加"); // 既定値だけで足りるため、すぐ保存される
            run.Set("NameBox", "B");
            Assert.Contains("使用中のプロファイル: 「A」", run.Element("ActiveText").Name);
            Assert.True(run.Element("UseButton").IsEnabled);
            run.Invoke("UseButton");
            run.WaitFor(() => run.Element("ActiveText").Name.Contains("使用中です"));
            Assert.False(run.Element("UseButton").IsEnabled);
            run.Shot("use-saved");
            string IdOf(string name)
            {
                using var saved = JsonDocument.Parse(File.ReadAllText(run.Settings));
                return saved.RootElement.GetProperty("Profiles").EnumerateArray()
                    .Single(p => p.GetProperty("Name").GetString() == name).GetProperty("Id").GetString()!;
            }
            string ActiveId()
            {
                using var saved = JsonDocument.Parse(File.ReadAllText(run.Settings));
                return saved.RootElement.GetProperty("ActiveProfileId").GetString()!;
            }
            run.WaitFor(() => ActiveId() == IdOf("B"));

            run.Restart(); // 「保存」を押さずに閉じても、使用先の切替は保存済み
            run.SelectListItem("ProfileList", "A");
            Assert.Contains("使用中のプロファイル: 「B」", run.Element("ActiveText").Name);
            run.Invoke("UseButton");
            run.WaitFor(() => ActiveId() == IdOf("A"));
        });
    }

    [GuiFact]
    public void CorruptSettingsAreProtectedInRealWindow()
    {
        using var run = new GuiRun(output);
        File.WriteAllText(run.Settings, "{invalid-json");
        run.Check(() =>
        {
            run.Start();
            Assert.False(run.ElementByName("Azure MAI を追加").IsEnabled); // 壊れたファイルを上書きする操作はできない
            Assert.Contains("上書きせず保持", run.Element("SettingsCorruptText").Name);
            Assert.Equal("{invalid-json", File.ReadAllText(run.Settings));
        });
    }

    [GuiFact]
    public void InvalidIsolationPathsExitWithoutOpeningNormalApp()
    {
        using var run = new GuiRun(output);
        run.Start();
        using (var duplicate = GuiRun.Launch(run.DataDir))
        {
            Assert.True(duplicate.WaitForExit(10000));
            Assert.Equal(3, duplicate.ExitCode);
        }
        File.Delete(Path.Combine(run.DataDir, ".anydictation-e2e"));
        foreach (var path in new[] { run.DataDir, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
        {
            using var process = GuiRun.Launch(path);
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(2, process.ExitCode);
            Assert.False(File.Exists(run.Settings));
        }
    }
}

internal sealed class GuiRun : IDisposable
{
    readonly ITestOutputHelper _output;
    readonly UIA3Automation _automation = new();
    Process? _process;
    Application? _app;
    Window? _window;
    public string DataDir { get; }
    public string Settings => Path.Combine(DataDir, "settings.json");

    public GuiRun(ITestOutputHelper output)
    {
        _output = output;
        string id = Guid.NewGuid().ToString("N");
        DataDir = Path.Combine(Path.GetTempPath(), "AnyDictation.E2E", id);
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(DataDir, ".anydictation-e2e"), id);
        output.WriteLine("E2E artifacts: " + DataDir);
    }

    public static Process Launch(string dataDir)
    {
        string exe = Environment.GetEnvironmentVariable("ANYDICTATION_E2E_EXE") ??
            throw new InvalidOperationException("ANYDICTATION_E2E_EXE にテスト用ビルドの exe の絶対パスを指定してください。");
        if (!Path.IsPathFullyQualified(exe) || !File.Exists(exe) || Path.GetFileName(exe) != "AnyDictation.exe")
            throw new InvalidOperationException("テスト用 AnyDictation.exe の絶対パスが必要です。");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.ArgumentList.Add("--e2e-data-dir=" + dataDir);
        return Process.Start(info) ?? throw new InvalidOperationException("テスト用プロセスを起動できません。");
    }

    public void Start()
    {
        _process = Launch(DataDir);
        _app = Application.Attach(_process.Id);
        _window = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(15)) ??
            throw new InvalidOperationException("設定ウィンドウを取得できません。対話デスクトップと UIA のアクセスを確認してください。");
        Assert.Equal(_process.Id, _window.Properties.ProcessId.Value);
    }

    // タブを切り替えた直後は UIA ツリーの反映が遅れるため、短時間だけ待つ。
    public AutomationElement Element(string id)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (_window!.FindFirstDescendant(cf => cf.ByAutomationId(id)) is { } found) return found;
            if (clock.Elapsed > TimeSpan.FromSeconds(2)) throw new InvalidOperationException("UIA 要素が見つかりません: " + id);
            Thread.Sleep(50);
        }
    }
    // 入力欄は抜けたときに保存されるため、値を入れた後にフォーカスを欄の外(表示中のタブ)へ移す。
    public void Set(string id, string value)
    {
        var element = Element(id);
        element.Focus();
        element.Patterns.Value.Pattern.SetValue(value);
        Element("Tabs").FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.TabItem))
            .First(x => x.Patterns.SelectionItem.Pattern.IsSelected.Value).Focus();
        Settle();
    }
    public AutomationElement ElementByName(string name) =>
        _window!.FindFirstDescendant(cf => cf.ByName(name)) ?? throw new InvalidOperationException("UIA 要素が見つかりません: " + name);
    public string Value(string id) => Element(id).Patterns.Value.Pattern.Value.Value;
    public void Invoke(string id) => Element(id).Patterns.Invoke.Pattern.Invoke();
    // 折りたたまれた要素は UIA に残るが IsOffscreen になるため、画面上にあるものだけを「ある」とみなす。
    public bool Exists(string id) =>
        _window!.FindFirstDescendant(cf => cf.ByAutomationId(id)) is { } e && !e.Properties.IsOffscreen.Value;
    public string[] TabNames() => Element("Tabs").FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.TabItem))
        .Select(x => x.Name).ToArray();
    public void SelectListItem(string listId, string text) => Element(listId)
        .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))
        .First(x => x.Name.TrimStart('●', '　', ' ') == text).Patterns.SelectionItem.Pattern.Select();
    public void Settle() => Thread.Sleep(300);

    // Transform パターンで MinWidth / MinHeight まで縮める(UIA 操作のみ。実マウスは使わない)。
    public void ShrinkToMinimum()
    {
        // UIA は呼び出しスレッドの DPI 認識で座標を解釈する。非対応のままだと仮想化された座標になり、最小値へ縮まない。
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { _window!.Patterns.Transform.Pattern.Resize(100, 100); }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        Settle();
        var (width, height, dpi) = PhysicalSize();
        _output.WriteLine($"min size: UIA {_window.BoundingRectangle.Width}x{_window.BoundingRectangle.Height}, physical {width}x{height} px at {dpi} dpi");
    }

    // ウィンドウ内、かつ祖先のスクロール領域の表示範囲内にあれば到達済み。WPF の IsOffscreen はスクロールの切り取りを反映しない。
    // 範囲外なら、最寄りの縦スクロール領域を 10% ずつ送って探す。
    public void AssertReachable(string id)
    {
        var element = Element(id);
        AutomationElement? scrollArea = null;
        for (var p = element.Parent; p != null && scrollArea == null; p = p.Parent)
            if (p.Patterns.Scroll.TryGetPattern(out var scroll) && scroll.VerticallyScrollable.Value) scrollArea = p;
        bool Visible()
        {
            var inner = element.BoundingRectangle;
            var outer = _window!.BoundingRectangle;
            var clip = scrollArea?.BoundingRectangle ?? outer;
            return !element.Properties.IsOffscreen.Value && outer.Contains(inner) && clip.Contains(inner);
        }
        for (int percent = 0; !Visible() && scrollArea != null && percent <= 100; percent += 10)
        {
            scrollArea.Patterns.Scroll.Pattern.SetScrollPercent(-1, percent); // -1 は横方向を動かさない(UIA の NoScroll)
            Settle();
        }
        Assert.True(Visible(), $"{id} に到達できません: {element.BoundingRectangle} / window {_window!.BoundingRectangle}");
    }

    public void Shot(string name)
    {
        string dir = Environment.GetEnvironmentVariable("ANYDICTATION_E2E_SHOTS") is { Length: > 0 } custom ? custom : Path.Combine(DataDir, "shots");
        Directory.CreateDirectory(dir);
        Settle();
        string path = Path.Combine(dir, name + ".png");
        SaveOwnWindow(path);
        _output.WriteLine("screenshot: " + path);
    }

    public void InvokeName(string name) => (_window!.FindFirstDescendant(cf => cf.ByName(name)) ??
        throw new InvalidOperationException(name)).Patterns.Invoke.Pattern.Invoke();
    public void SelectName(string name) => (_window!.FindFirstDescendant(cf => cf.ByName(name)) ??
        throw new InvalidOperationException(name)).Patterns.SelectionItem.Pattern.Select();
    public void WaitFor(Func<bool> predicate)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (predicate()) return;
            Thread.Sleep(50);
        }
        Assert.True(predicate(), "GUI 状態変化の待機がタイムアウトしました。");
    }

    public void Check(Action test)
    {
        try { test(); }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(DataDir, "failure.txt"), e.ToString());
            if (_window != null)
            {
                try
                {
                    SaveOwnWindow();
                    var elements = _window.FindAllDescendants();
                    File.WriteAllLines(Path.Combine(DataDir, "uia-tree.txt"), elements.Select(x =>
                        x.AutomationId + " | " + x.ControlType + " | " + x.Name));
                }
                catch (Exception diagnostic) { _output.WriteLine("診断取得失敗: " + diagnostic.GetType().Name); }
            }
            throw;
        }
    }

    // PrintWindow は自身の HWND のみ描画し、背後のユーザーアプリを撮影しない。
    void SaveOwnWindow(string? path = null)
    {
        var hwnd = (IntPtr)_window!.Properties.NativeWindowHandle.Value;
        // テストプロセスが DPI 非対応だと、GetWindowRect が仮想化された小さい寸法を返し、画像が途中で切れる。
        // この呼び出しの間だけ、スレッドを Per-Monitor v2 にして実寸を取る。
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { SaveOwnWindowCore(hwnd, path); }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    (int Width, int Height, uint Dpi) PhysicalSize()
    {
        var hwnd = (IntPtr)_window!.Properties.NativeWindowHandle.Value;
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            GetWindowRect(hwnd, out var rect);
            return (rect.Right - rect.Left, rect.Bottom - rect.Top, GetDpiForWindow(hwnd));
        }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    void SaveOwnWindowCore(IntPtr hwnd, string? path)
    {
        if (!GetWindowRect(hwnd, out var rect)) return;
        using var image = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using var graphics = Graphics.FromImage(image);
        var dc = graphics.GetHdc();
        bool captured;
        try { captured = PrintWindow(hwnd, dc, 2); }
        finally { graphics.ReleaseHdc(dc); }
        if (captured) image.Save(path ?? Path.Combine(DataDir, "failure.png"), ImageFormat.Png);
    }

    public void Restart() { Stop(); Start(); }
    void Stop()
    {
        if (_process == null) return;
        try
        {
            if (!_process.HasExited)
            {
                _window?.Patterns.Window.Pattern.Close();
                if (!_process.WaitForExit(5000)) { _process.Kill(); _process.WaitForExit(5000); }
            }
        }
        finally { _app?.Dispose(); _process.Dispose(); _app = null; _process = null; _window = null; }
    }
    public void Dispose() { Stop(); _automation.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
