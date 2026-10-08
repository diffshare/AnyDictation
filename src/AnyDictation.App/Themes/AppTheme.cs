using System;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;

namespace AnyDictation.App;

/// <summary>WPF UI のテーマと、この画面の設計トークン(Themes/*.xaml)を、設定のテーマ(Windows に合わせる / ライト / ダーク)に合わせる。</summary>
internal static class AppTheme
{
    static readonly Color LightAccent = Color.FromRgb(0x2D, 0x6F, 0xCD), DarkAccent = Color.FromRgb(0x76, 0xAC, 0xFC);
    static ResourceDictionary? _tokens;
    static Window? _watched; // Windows のテーマ変更を受け取るウィンドウ
    static bool _watching;
    static ThemePreference _preference;

    public static void Initialize(Application app, ThemePreference preference)
    {
        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        app.Resources.MergedDictionaries.Add(Load("Common"));
        ApplicationThemeManager.Changed += (theme, _) => ApplyTokens(app, theme);
        Apply(preference);
        ApplyTokens(app, ApplicationThemeManager.GetAppTheme()); // 既にライトなら Changed が来ないため、ここでも当てる
    }

    /// <summary>Windows のテーマ変更を受け取るウィンドウ。設定が「Windows に合わせる」の間だけ追随する。</summary>
    public static void Watch(Window window)
    {
        _watched = window;
        UpdateWatch();
    }

    public static void Apply(ThemePreference preference)
    {
        _preference = preference;
        if (preference == ThemePreference.System) ApplicationThemeManager.ApplySystemTheme(updateAccent: false);
        else ApplicationThemeManager.Apply(preference == ThemePreference.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, updateAccent: false);
        UpdateWatch();
    }

    // 固定のテーマを選んでいる間に Windows 側が切り替わっても、上書きされないようにする
    static void UpdateWatch()
    {
        if (_watched == null) return;
        bool want = _preference == ThemePreference.System;
        if (want == _watching) return;
        if (want) SystemThemeWatcher.Watch(_watched, WindowBackdropType.Mica, updateAccents: false);
        else SystemThemeWatcher.UnWatch(_watched);
        _watching = want;
    }

    static void ApplyTokens(Application app, ApplicationTheme theme)
    {
        bool dark = theme == ApplicationTheme.Dark;
        var next = Load(dark ? "Dark" : "Light");
        if (_tokens != null) app.Resources.MergedDictionaries.Remove(_tokens);
        app.Resources.MergedDictionaries.Add(next);
        _tokens = next;
        // アクセントは Windows の色ではなく、設計の固定の青を使う
        var accent = dark ? DarkAccent : LightAccent;
        ApplicationAccentColorManager.Apply(accent, accent, accent, accent);
    }

    static ResourceDictionary Load(string name) =>
        new() { Source = new Uri($"pack://application:,,,/AnyDictation;component/Themes/{name}.xaml", UriKind.Absolute) };
}
