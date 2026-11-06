using Microsoft.Extensions.Logging;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// 書かれたログのメッセージを記録するテスト用ロガー。
/// </summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly List<string> _messages = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_lock)
            {
                return [.. _messages];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lock)
        {
            _messages.Add(formatter(state, exception));
        }
    }
}
