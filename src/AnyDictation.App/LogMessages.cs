using AnyDictation;
using Microsoft.Extensions.Logging;

namespace AnyDictation.App;

/// <summary>録音開始の段階名。メンバー名がそのままログの stage= の値になるため、ログの書式に合わせて snake_case にする。</summary>
internal enum RecordingStartStage
{
    requested, preflight_failed, preflight_completed, live_setup_failed, microphone_start_failed, status_ui_updated,
    device_resolution_started, device_resolution_completed, wavein_start_call_started, wavein_start_call_returned,
    first_audio_buffer_received, first_non_silent_buffer_received,
    sound_play_requested, sound_play_call_returned, first_input_meter_updated,
}

/// <summary>
/// アプリが書く診断ログの一覧。ログに出す内容はここで定義したものだけにする(任意の文字列を書く入口は設けない)。
/// 引数は状態・種別・件数・長さ・時間・例外の型名に限り、認識結果の本文・APIキー・音声は受け取らない。
/// 例外は型名だけを渡し、Exception 自体は渡さない(メッセージやスタックに本文が入りうるため)。
/// </summary>
internal static partial class LogMessages
{
    // 1xxx: 起動・終了・設定
    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "startup settingsCorrupt={SettingsCorrupt} historyCorrupt={HistoryCorrupt}")]
    public static partial void Startup(this ILogger logger, bool settingsCorrupt, bool historyCorrupt);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "hook install failed")]
    public static partial void HookInstallFailed(this ILogger logger);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "hook action={Action}")]
    public static partial void HookActionReceived(this ILogger logger, HookAction action);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information, Message = "toggle requested source=hotkey ui_delay_ms={UiDelayMs:F0}")]
    public static partial void ToggleRequestedFromHotkey(this ILogger logger, double uiDelayMs);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information, Message = "toggle requested source=tray")]
    public static partial void ToggleRequestedFromTray(this ILogger logger);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Error, Message = "unhandled {ExceptionType}")]
    public static partial void Unhandled(this ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Information, Message = "exit")]
    public static partial void Exiting(this ILogger logger);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Warning, Message = "settings save failed")]
    public static partial void SettingsSaveFailed(this ILogger logger);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Warning, Message = "settings saved; obsolete credentials left")]
    public static partial void SettingsSavedWithLeftovers(this ILogger logger);

    // 2xxx: 録音
    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "recording_start id={Id} stage={Stage} elapsed_ms={ElapsedMs:F1}")]
    public static partial void RecordingStartStageReached(this ILogger logger, int id, RecordingStartStage stage, double elapsedMs);

    /// <summary>errorMessage はアプリと NAudio が作るデバイスのエラー文(画面にも出す)。録音内容は含まない。</summary>
    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "recorder start failed: {ExceptionType} {ErrorMessage}")]
    public static partial void RecorderStartFailed(this ILogger logger, string exceptionType, string errorMessage);

    /// <summary>profile は利用者が付けたプロファイル名。</summary>
    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "recording started profile={Profile}")]
    public static partial void RecordingStarted(this ILogger logger, string profile);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Warning, Message = "stop failed: {ExceptionType}")]
    public static partial void StopFailed(this ILogger logger, string exceptionType);

    /// <summary>音声の代わりにバイト数だけを書く。</summary>
    [LoggerMessage(EventId = 2005, Level = LogLevel.Information, Message = "recording stopped auto={Auto} silent={Silent} warning={Warning} bytes={Bytes}")]
    public static partial void RecordingStopped(this ILogger logger, bool auto, bool silent, bool warning, int bytes);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Warning, Message = "recorder interrupted silent={Silent} bytes={Bytes} live={Live}")]
    public static partial void RecorderInterrupted(this ILogger logger, bool silent, int bytes, bool live);

    [LoggerMessage(EventId = 2007, Level = LogLevel.Information, Message = "microphone ready startup_ms={StartupMs:F0} noticed={Noticed}")]
    public static partial void MicrophoneReady(this ILogger logger, double startupMs, bool noticed);

    [LoggerMessage(EventId = 2008, Level = LogLevel.Warning, Message = "live failed while recording; stopping the recording")]
    public static partial void LiveFailedWhileRecording(this ILogger logger);

    [LoggerMessage(EventId = 2009, Level = LogLevel.Information, Message = "recording cancelled live={Live}")]
    public static partial void RecordingCancelled(this ILogger logger, bool live);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Warning, Message = "recording sound failed: {ExceptionType}")]
    public static partial void RecordingSoundFailed(this ILogger logger, string exceptionType);

    // 3xxx: 認識と結果の配送
    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "recognition started provider={Provider} model={Model}")]
    public static partial void RecognitionStarted(this ILogger logger, ProviderKind provider, string model);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "recognition failed kind={Kind}")]
    public static partial void RecognitionFailed(this ILogger logger, TranscriptionErrorKind kind);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Error, Message = "recognition failed unexpected={ExceptionType}")]
    public static partial void RecognitionFailedUnexpected(this ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Information, Message = "recognition succeeded")]
    public static partial void RecognitionSucceeded(this ILogger logger);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Information, Message = "retry requested")]
    public static partial void RetryRequested(this ILogger logger);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Information, Message = "pending audio discarded")]
    public static partial void PendingAudioDiscarded(this ILogger logger);

    [LoggerMessage(EventId = 3007, Level = LogLevel.Warning, Message = "history save failed: {ExceptionType}")]
    public static partial void HistorySaveFailed(this ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3008, Level = LogLevel.Information, Message = "delivery outcome={Outcome} reason={Reason} enter=-")]
    public static partial void Delivered(this ILogger logger, DeliveryOutcome outcome, CopyReason reason);

    [LoggerMessage(EventId = 3009, Level = LogLevel.Information, Message = "delivery outcome={Outcome} reason={Reason} enter={EnterSent}")]
    public static partial void DeliveredWithEnter(this ILogger logger, DeliveryOutcome outcome, CopyReason reason, bool enterSent);

    [LoggerMessage(EventId = 3010, Level = LogLevel.Information, Message = "repaste outcome={Outcome} reason={Reason}")]
    public static partial void Repasted(this ILogger logger, DeliveryOutcome outcome, CopyReason reason);

    // 4xxx: キーボードフックと前面ウィンドウの監視
    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "keyboard hook started")]
    public static partial void KeyboardHookStarted(this ILogger logger);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information, Message = "keyboard hook stopped")]
    public static partial void KeyboardHookStopped(this ILogger logger);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Warning, Message = "repaste hotkey not registered win32={Win32Error}")]
    public static partial void RepasteHotkeyNotRegistered(this ILogger logger, int win32Error);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Warning, Message = "keyboard hook reset not delivered win32={Win32Error}")]
    public static partial void KeyboardHookResetNotDelivered(this ILogger logger, int win32Error);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Warning, Message = "keyboard hook stop not delivered win32={Win32Error}")]
    public static partial void KeyboardHookStopNotDelivered(this ILogger logger, int win32Error);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Warning, Message = "keyboard hook did not stop within 2s")]
    public static partial void KeyboardHookDidNotStop(this ILogger logger);

    [LoggerMessage(EventId = 4007, Level = LogLevel.Warning, Message = "foreground tracker hook failed")]
    public static partial void ForegroundTrackerHookFailed(this ILogger logger);

    // 6xxx: 更新
    [LoggerMessage(EventId = 6002, Level = LogLevel.Warning, Message = "update apply failed {ExceptionType}")]
    public static partial void UpdateApplyFailed(this ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Information, Message = "update downloaded version={Version}")]
    public static partial void UpdateDownloaded(this ILogger logger, string version);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Warning, Message = "update check failed {ExceptionType}")]
    public static partial void UpdateCheckFailed(this ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 6005, Level = LogLevel.Information, Message = "update apply and restart version={Version}")]
    public static partial void UpdateApplyAndRestart(this ILogger logger, string version);
}
