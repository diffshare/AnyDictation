using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

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
    string CostText = "");

/// <summary>
/// 小さな常時前面の状態表示。WS_EX_NOACTIVATE により、表示してもクリックしても前面ウィンドウ(入力フォーカス)を奪わない。
/// </summary>
internal partial class StatusWindow : Window
{
    readonly DispatcherTimer _hideTimer = new();

    public event Action? CancelClicked, AbortClicked, RetryClicked, DiscardClicked;

    public StatusWindow()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE,
                Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };
    }

    public void Present(StatusView v)
    {
        _hideTimer.Stop();
        Dot.Fill = new SolidColorBrush(v.Kind switch
        {
            StatusKind.Recording => Color.FromRgb(0xE5, 0x39, 0x35),
            StatusKind.Recognizing => Color.FromRgb(0xFB, 0x8C, 0x00),
            StatusKind.Success => Color.FromRgb(0x43, 0xA0, 0x47),
            StatusKind.Warning => Color.FromRgb(0xFD, 0xD8, 0x35),
            StatusKind.Failed => Color.FromRgb(0xD3, 0x2F, 0x2F),
            _ => Color.FromRgb(0x90, 0xA4, 0xAE),
        });
        InputMeter.Visibility = v.Kind == StatusKind.Recording ? Visibility.Visible : Visibility.Collapsed;
        CostText.Text = v.CostText;
        CostText.Visibility = v.CostText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = v.Title;
        DetailText.Text = v.Detail;
        DetailText.Visibility = v.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Live の途中経過。呼び出し側が末尾の文字数に絞り、ここでも高さを MaxHeight で抑える(長文でも拡大し続けない)
        LiveText.Text = v.LiveText;
        LiveText.Visibility = v.LiveText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = v.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        AbortButton.Visibility = v.CanAbort ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = DiscardButton.Visibility = v.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = v.CanClose ? Visibility.Visible : Visibility.Collapsed;
        Buttons.Visibility = v.CanCancel || v.CanAbort || v.CanRetry || v.CanClose ? Visibility.Visible : Visibility.Collapsed;

        if (!IsVisible) Show();
        Reposition();
        if (v.AutoHide is { } t)
        {
            _hideTimer.Interval = t;
            _hideTimer.Start();
        }
    }

    public void UpdateInputLevel(int? peak)
    {
        // 平方根で小さな入力も見やすくする。表示用の変換で、録音音量は変更しない。
        InputLevelBar.Value = Math.Sqrt(Math.Clamp(peak ?? 0, 0, 32768) / 32768.0) * 100;
        bool hasInput = peak >= SilenceDetector.DefaultThreshold;
        InputLevelBar.Foreground = hasInput ? Brushes.LightGreen : Brushes.Goldenrod;
        InputLevelText.Text = peak is null ? "マイクの入力待ち" :
            hasInput ? "マイク入力あり" : "入力が小さい／無音";
    }

    void Reposition()
    {
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 24;
    }

    void OnCancel(object s, RoutedEventArgs e) => CancelClicked?.Invoke();
    void OnAbort(object s, RoutedEventArgs e) => AbortClicked?.Invoke();
    void OnRetry(object s, RoutedEventArgs e) => RetryClicked?.Invoke();
    void OnDiscard(object s, RoutedEventArgs e) => DiscardClicked?.Invoke();
    void OnClose(object s, RoutedEventArgs e) { _hideTimer.Stop(); Hide(); }
}
