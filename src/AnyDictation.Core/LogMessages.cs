using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace AnyDictation;

/// <summary>Live の段階名。メンバー名がそのままログの stage= の値になるため、ログの書式に合わせて snake_case にする。</summary>
public enum LiveStage
{
    connect_started, connected, session_created, session_updated, send_started, first_delta,
    audio_complete, committed, cancelled, server_error,
}

/// <summary>
/// Core が書くログの一覧。ログに出す内容はここで定義したものだけにする。
/// 引数は段階名・経過時間・長さ・種別などに限り、認識結果の本文・APIキー・音声・サービスが返す文字列は受け取らない。
/// </summary>
internal static partial class LiveLog
{
    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "live id={Id} stage={Stage} elapsed_ms={ElapsedMs:F1}")]
    public static partial void LiveStageReached(this ILogger logger, int id, LiveStage stage, double elapsedMs);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information, Message = "live id={Id} stage=commit_sent elapsed_ms={ElapsedMs:F1} audio_bytes={AudioBytes}")]
    public static partial void LiveCommitSent(this ILogger logger, int id, double elapsedMs, long audioBytes);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Warning, Message = "live id={Id} stage=closed_by_server elapsed_ms={ElapsedMs:F1} close_status={CloseStatus}")]
    public static partial void LiveClosedByServer(this ILogger logger, int id, double elapsedMs, WebSocketCloseStatus? closeStatus);

    /// <summary>本文の代わりに文字数だけを書く。</summary>
    [LoggerMessage(EventId = 5004, Level = LogLevel.Information, Message = "live id={Id} stage=final elapsed_ms={ElapsedMs:F1} chars={Chars}")]
    public static partial void LiveFinal(this ILogger logger, int id, double elapsedMs, int chars);

    [LoggerMessage(EventId = 5005, Level = LogLevel.Warning, Message = "live id={Id} stage=failed elapsed_ms={ElapsedMs:F1} kind={Kind}")]
    public static partial void LiveFailed(this ILogger logger, int id, double elapsedMs, TranscriptionErrorKind kind);
}
