using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AnyDictation.Tests;

/// <summary>書かれたログを、本番のファイルと同じく整形済みの 1 行としてキューへ積む。</summary>
sealed class QueueLogger(ConcurrentQueue<string> lines) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => lines.Enqueue(formatter(state, exception));
}
