using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Wpf.Ui.Appearance;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AnyDictation.App;

internal enum StatusKind { Idle, Recording, Recognizing, Success, Warning, Failed }

internal sealed record StatusView(
    StatusKind Kind,
    string Title,
    string Detail = "",
    bool CanCancel = false,
    bool CanAbort = false,
    bool CanRetry = false,
    bool CanClose = false,
    TimeSpan? AutoHide = null,
    string LiveText = "",
    string CostText = "",
    bool EscDismissible = false); // 表示中は録音中でなくても Esc で閉じられる(取消の通知)

/// <summary>
/// 小さな常時前面の状態表示。WS_EX_NOACTIVATE により、表示してもクリックしても前面ウィンドウ(入力フォーカス)を奪わない。
/// </summary>
internal partial class StatusWindow : Window
{
    readonly DispatcherTimer _hideTimer = new();
    bool _escDismissible; // 今の表示内容が Esc で閉じられるか
    bool _escActive; // 通知した最後の「表示中かつ Esc で閉じられる」
    readonly Run _liveRun = new();
    readonly Rectangle _caret = new() { Width = 2, Height = 16, Margin = new Thickness(2, 0, 0, 0) }; // Live の途中文字の末尾
    readonly Storyboard _pulse, _caretBlink;
    readonly HashSet<Storyboard> _running = new();

    public event Action? CancelClicked, AbortClicked, RetryClicked, DiscardClicked;

    /// <summary>「表示中で、Esc で閉じられる内容」かどうかが変わるたびに UI スレッドで呼ばれる。表示の変更、自動で隠れる、閉じるのどれでも、ウィンドウの表示状態から導く。</summary>
    public event Action<bool>? EscDismissibleChanged;

    const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080;

    public StatusWindow()
    {
        InitializeComponent();
        _caret.SetResourceReference(Shape.FillProperty, "Accent");
        // 途中文字は表示のたびに Run の文字だけを差し替える(キャレットは一度だけ文中に置く)
        LiveText.Inlines.Add(_liveRun);
        LiveText.Inlines.Add(new InlineUIContainer(_caret) { BaselineAlignment = BaselineAlignment.Center });
        // 録音中の赤丸は 1.2 秒周期で不透明度 1↔0.3、キャレットは 1 秒周期で点滅
        _pulse = Repeat(RecDot, new DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(0.6))
        {
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
        var blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1) };
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.5))));
        _caretBlink = Repeat(_caret, blink);
        IsVisibleChanged += (_, _) => RefreshEscDismissible();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
        SourceInitialized += (_, _) =>
        {
            // フォーカスを奪わず、Alt+Tab に出さない
            var hwnd = new HWND(new WindowInteropHelper(this).Handle);
            int style = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, style | WsExNoActivate | WsExToolWindow);
        };
        // WPF UI はテーマを当てるとき、アプリの全ウィンドウに背景効果(Mica)を付ける。透明な余白が塗られるため、当てられた後に外す
        Loaded += (_, _) => KeepTransparentLater();
        ApplicationThemeManager.Changed += (_, _) => KeepTransparentLater();
    }

    void KeepTransparentLater() => Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
    {
        WindowBackgroundManager.UpdateBackground(this, ApplicationThemeManager.GetAppTheme(), WindowBackdropType.None);
        Background = Brushes.Transparent;
        // 背景効果を外すと合成の背景色が不透明な白になり、余白が白い四角になるため、透明に戻す
        if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
    });

    public void Present(StatusView v)
    {
        _hideTimer.Stop();
        _escDismissible = v.EscDismissible;
        ShowIndicator(v.Kind);
        bool recording = v.Kind == StatusKind.Recording;
        InputLevelBar.Visibility = InputLevelText.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (!recording) InputLevelBar.Clear();
        CostText.Text = v.CostText;
        CostText.Visibility = v.CostText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = v.Title;
        DetailText.Text = v.Detail;
        DetailText.Visibility = v.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Live の途中経過。呼び出し側が末尾の文字数に絞り、ここでも 3 行に抑えて最新の行を見せる(長文でも拡大し続けない)
        bool live = v.LiveText.Length > 0;
        _liveRun.Text = v.LiveText;
        LiveScroll.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        Card.Width = live ? 560 : double.NaN; // 途中文字を出す間は幅を固定し、行の折り返しを安定させる
        SetAnimation(_caretBlink, _caret, live);
        CancelButton.Visibility = v.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        AbortButton.Visibility = v.CanAbort ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = DiscardButton.Visibility = v.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = v.CanClose ? Visibility.Visible : Visibility.Collapsed;
        Buttons.Visibility = v.CanCancel || v.CanAbort || v.CanRetry || v.CanClose ? Visibility.Visible : Visibility.Collapsed;

        if (!IsVisible) Show();
        Reposition();
        if (live) LiveScroll.ScrollToEnd();
        if (v.AutoHide is { } t)
        {
            _hideTimer.Interval = t;
            _hideTimer.Start();
        }
        RefreshEscDismissible();
    }

    void RefreshEscDismissible()
    {
        bool now = IsVisible && _escDismissible;
        if (now == _escActive) return;
        _escActive = now;
        EscDismissibleChanged?.Invoke(now);
    }

    /// <summary>Esc で閉じられる内容が今も表示中なら閉じる。フックの判断は古いことがあるので、UI スレッドで確かめ直す。</summary>
    public void DismissIfEscDismissible()
    {
        if (!_escActive) return;
        _hideTimer.Stop();
        Hide();
    }

    public void UpdateInputLevel(int? peak)
    {
        // 平方根で小さな入力も見やすくする。表示用の変換で、録音音量は変更しない。
        InputLevelBar.Push(Math.Sqrt(Math.Clamp(peak ?? 0, 0, 32768) / 32768.0));
        bool hasInput = peak >= SilenceDetector.DefaultThreshold;
        InputLevelText.Text = peak is null ? "マイクの入力待ち" :
            hasInput ? "マイク入力あり" : "入力が小さい／無音";
    }

    /// <summary>録音中は点滅する赤丸、認識中はスピナー、それ以外は色付きの丸に記号を出す。中立の通知(取消・破棄)は何も出さない。</summary>
    void ShowIndicator(StatusKind kind)
    {
        RecDot.Visibility = kind == StatusKind.Recording ? Visibility.Visible : Visibility.Collapsed;
        Spinner.Visibility = kind == StatusKind.Recognizing ? Visibility.Visible : Visibility.Collapsed;
        (string glyph, string brush)? badge = kind switch
        {
            StatusKind.Success => ("✓", "Success"),
            StatusKind.Warning => ("!", "Caution"),
            StatusKind.Failed => ("!", "Warning"),
            _ => null,
        };
        Badge.Visibility = badge != null ? Visibility.Visible : Visibility.Collapsed;
        if (badge is { } b)
        {
            BadgeGlyph.Text = b.glyph;
            BadgeCircle.Fill = (Brush)FindResource(b.brush);
        }
        Indicator.Visibility = kind == StatusKind.Idle ? Visibility.Collapsed : Visibility.Visible;
        SetAnimation(_pulse, RecDot, kind == StatusKind.Recording);
    }

    static Storyboard Repeat(DependencyObject target, AnimationTimeline opacity)
    {
        Storyboard.SetTarget(opacity, target);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(OpacityProperty));
        return new Storyboard { Children = { opacity }, RepeatBehavior = RepeatBehavior.Forever };
    }

    /// <summary>「Windows のアニメーション効果」がオフなら点滅させない。</summary>
    void SetAnimation(Storyboard storyboard, FrameworkElement target, bool on)
    {
        if (on && SystemParameters.ClientAreaAnimation)
        {
            if (!_running.Contains(storyboard))
            {
                storyboard.Begin(target, isControllable: true);
                _running.Add(storyboard);
            }
            return;
        }
        if (_running.Remove(storyboard)) storyboard.Stop(target);
        target.Opacity = 1;
    }

    void Reposition()
    {
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        var shadow = ShadowArea.Margin; // 影の余白を除いたカードを、作業領域の下端から 24px に置く
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight + shadow.Bottom - 24;
    }

    void OnCancel(object s, RoutedEventArgs e) => CancelClicked?.Invoke();
    void OnAbort(object s, RoutedEventArgs e) => AbortClicked?.Invoke();
    void OnRetry(object s, RoutedEventArgs e) => RetryClicked?.Invoke();
    void OnDiscard(object s, RoutedEventArgs e) => DiscardClicked?.Invoke();
    void OnClose(object s, RoutedEventArgs e) { _hideTimer.Stop(); Hide(); }
}
