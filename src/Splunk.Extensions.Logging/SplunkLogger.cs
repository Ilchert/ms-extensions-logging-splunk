using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Splunk.Extensions.Logging;

internal sealed class SplunkLogger(string category, SplunkLoggerProvider provider) : ILogger, IBufferedLogger
{
    private static readonly JsonEncodedText s_time = JsonEncodedText.Encode("time");
    private static readonly JsonEncodedText s_host = JsonEncodedText.Encode("host");
    private static readonly JsonEncodedText s_source = JsonEncodedText.Encode("source");
    private static readonly JsonEncodedText s_sourceType = JsonEncodedText.Encode("sourcetype");
    private static readonly JsonEncodedText s_index = JsonEncodedText.Encode("index");
    private static readonly JsonEncodedText s_event = JsonEncodedText.Encode("event");
    private static readonly JsonEncodedText s_fields = JsonEncodedText.Encode("fields");
    private static readonly JsonEncodedText s_severityText = JsonEncodedText.Encode("otel.log.severity.text");
    private static readonly JsonEncodedText s_severityNumber = JsonEncodedText.Encode("otel.log.severity.number");
    private static readonly JsonEncodedText s_libraryName = JsonEncodedText.Encode("otel.library.name");
    private static readonly JsonEncodedText s_eventId = JsonEncodedText.Encode("logrecord.event.id");
    private static readonly JsonEncodedText s_logName = JsonEncodedText.Encode("otel.log.name");
    private static readonly JsonEncodedText s_exceptionType = JsonEncodedText.Encode("exception.type");
    private static readonly JsonEncodedText s_exceptionMessage = JsonEncodedText.Encode("exception.message");
    private static readonly JsonEncodedText s_exceptionStackTrace = JsonEncodedText.Encode("exception.stacktrace");
    private static readonly JsonEncodedText s_traceId = JsonEncodedText.Encode("trace_id");
    private static readonly JsonEncodedText s_spanId = JsonEncodedText.Encode("span_id");

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
        provider.Enqueue(Serialize(DateTimeOffset.UtcNow, logLevel, eventId, exception?.GetType().FullName, exception?.Message, exception?.ToString(), formatter(state, exception),
            activity?.TraceId, activity?.SpanId, props, includeScopes: true));
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
            provider.Enqueue(Serialize(r.Timestamp, r.LogLevel, r.EventId, null, null, r.Exception, message,
                traceId, spanId, r.Attributes, includeScopes: false));
        }
    }

    private byte[] Serialize(DateTimeOffset timestamp, LogLevel level, EventId eventId, string? exceptionType,
        string? exceptionMessage, string? exceptionStackTrace, string message, ActivityTraceId? traceId, ActivitySpanId? spanId,
        IEnumerable<KeyValuePair<string, object?>>? props, bool includeScopes)
    {
        var o = provider.Options;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber(s_time, timestamp.ToUnixTimeMilliseconds() / 1000.0);
            if (o.Host is not null) w.WriteString(s_host, o.Host);
            if (o.Source is not null) w.WriteString(s_source, o.Source);
            if (o.SourceType is not null) w.WriteString(s_sourceType, o.SourceType);
            if (o.Index is not null) w.WriteString(s_index, o.Index);

            w.WriteString(s_event, message);

            w.WriteStartObject(s_fields);
            w.WriteString(s_severityText, SeverityText(level));
            w.WriteNumber(s_severityNumber, SeverityNumber(level));
            w.WriteString(s_libraryName, category);
            if (eventId.Id != 0) w.WriteNumber(s_eventId, eventId.Id);
            if (eventId.Name is not null) w.WriteString(s_logName, eventId.Name);
            if (exceptionType is not null) w.WriteString(s_exceptionType, exceptionType);
            if (exceptionMessage is not null) w.WriteString(s_exceptionMessage, exceptionMessage);
            if (exceptionStackTrace is not null) w.WriteString(s_exceptionStackTrace, exceptionStackTrace);
            if (traceId is { } tid) w.WriteString(s_traceId, tid.ToHexString());
            if (spanId is { } sid) w.WriteString(s_spanId, sid.ToHexString());

            if (props is not null)
            {
                foreach (var (key, value) in props)
                {
                    if (key == "{OriginalFormat}") continue;
                    WriteValue(w, key, value);
                }
            }

            if (includeScopes) provider.ScopeProvider?.ForEachScope((scope, writer) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> kvs)
                    foreach (var (key, value) in kvs)
                    {
                        if (key == "{OriginalFormat}") continue;
                        WriteValue(writer, key, value);
                    }
            }, w);
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string SeverityText(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => "FATAL",
    };

    private static int SeverityNumber(LogLevel l) => l switch
    {
        LogLevel.Trace => 1,
        LogLevel.Debug => 5,
        LogLevel.Information => 9,
        LogLevel.Warning => 13,
        LogLevel.Error => 17,
        _ => 21,
    };

    private static void WriteValue(Utf8JsonWriter w, string key, object? value)
    {
        switch (value)
        {
            case null: w.WriteNull(key); break;
            case string s: w.WriteString(key, s); break;
            case bool b: w.WriteBoolean(key, b); break;
            case int i: w.WriteNumber(key, i); break;
            case long l: w.WriteNumber(key, l); break;
            case double d when double.IsFinite(d): w.WriteNumber(key, d); break;
            case float f when float.IsFinite(f): w.WriteNumber(key, f); break;
            case decimal m: w.WriteNumber(key, m); break;
            default: w.WriteString(key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
        }
    }
}



