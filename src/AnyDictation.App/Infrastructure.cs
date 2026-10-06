using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AnyDictation.App;

internal static class AppPaths
{
    public static string DataDir => E2eMode.DataDir ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnyDictation");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string HistoryFile => Path.Combine(DataDir, "history.json");
    public static string LogFile => Path.Combine(DataDir, "log.txt");
}

/// <summary>診断ログ。状態遷移とエラー種別だけを書き、認識結果の本文・APIキー・音声は書かない。</summary>
internal static class Log
{
    const long MaxBytes = 256 * 1024;
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(AppPaths.LogFile, AppPaths.LogFile + ".old", overwrite: true);
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // ログ失敗でアプリの動作は止めない
        }
    }
}

/// <summary>Windows サインイン時の自動起動。HKCU の Run に AnyDictation の値だけを追加/削除し、他のエントリには触れない。</summary>
internal static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "AnyDictation";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled()
    {
        if (E2eMode.Enabled) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        if (E2eMode.Enabled) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>アンインストール時に呼ぶ。この exe を指す値だけを削除し、portable 版など別の exe を指す値は残す。</summary>
    public static void RemoveIfOwned()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string value && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase))
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

internal static class ClipboardHelper
{
    /// <summary>他アプリがクリップボードを開いていると失敗するため、短く数回だけ再試行する。</summary>
    public static async Task<bool> SetTextAsync(string text)
    {
        if (E2eMode.Enabled) return false;
        for (int i = 0; i < 10; i++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                await Task.Delay(50);
            }
        }
        return false;
    }
}

/// <summary>開始要求からの経過時間。録音ごとの ID で UI と入力スレッドのログを結び付ける。</summary>
internal sealed class RecordingStartTrace
{
    static int _nextId;
    readonly int _id = System.Threading.Interlocked.Increment(ref _nextId);
    readonly Stopwatch _clock = Stopwatch.StartNew();

    public void Mark(string stage)
    {
        double elapsed = _clock.Elapsed.TotalMilliseconds;
        Log.Write(FormattableString.Invariant($"recording_start id={_id} stage={stage} elapsed_ms={elapsed:F1}"));
    }
}