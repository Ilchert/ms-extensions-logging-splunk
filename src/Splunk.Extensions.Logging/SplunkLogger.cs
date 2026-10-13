using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Splunk.Extensions.Logging;

internal sealed class SplunkLogger(string category, SplunkLoggerProvider provider) : ILogger, IBufferedLogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => provider.ScopeProvider?.Push(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        ArgumentNullException.ThrowIfNull(formatter);
        var props = state as IEnumerable<KeyValuePair<string, object?>>;
        var activity = Activity.Current;
        provider.Write(category, DateTimeOffset.UtcNow, logLevel, eventId, exception?.GetType().Name, exception?.Message, exception?.ToString(), formatter(state, exception),
            activity?.TraceId, activity?.SpanId, props, includeScopes: true);
    }

    public void LogRecords(IEnumerable<BufferedLogRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var r in records)
        {
            if (!IsEnabled(r.LogLevel)) continue;
            var message = r.FormattedMessage
                ?? r.Exception
                ?? string.Empty;
            ActivityTraceId? traceId = r.ActivityTraceId is { } t && t != default ? t : null;
            ActivitySpanId? spanId = r.ActivitySpanId is { } s && s != default ? s : null;
            provider.Write(category, r.Timestamp, r.LogLevel, r.EventId, null, null, r.Exception, message,
                traceId, spanId, r.Attributes, includeScopes: false);
        }
    }
}
