using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Splunk.Extensions.Logging;

internal sealed class SplunkLogger(string category, SplunkLoggerProvider provider) : ILogger, IBufferedLogger
{
    private static readonly JavaScriptEncoder s_encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    private static readonly JsonWriterOptions s_writerOptions = new() { Encoder = s_encoder };
    private static readonly JsonEncodedText s_time = JsonEncodedText.Encode("time"u8, s_encoder);
    private static readonly JsonEncodedText s_host = JsonEncodedText.Encode("host"u8, s_encoder);
    private static readonly JsonEncodedText s_source = JsonEncodedText.Encode("source"u8, s_encoder);
    private static readonly JsonEncodedText s_sourceType = JsonEncodedText.Encode("sourcetype"u8, s_encoder);
    private static readonly JsonEncodedText s_index = JsonEncodedText.Encode("index"u8, s_encoder);
    private static readonly JsonEncodedText s_event = JsonEncodedText.Encode("event"u8, s_encoder);
    private static readonly JsonEncodedText s_fields = JsonEncodedText.Encode("fields"u8, s_encoder);
    private static readonly JsonEncodedText s_severityText = JsonEncodedText.Encode("otel.log.severity.text"u8, s_encoder);
    private static readonly JsonEncodedText s_severityNumber = JsonEncodedText.Encode("otel.log.severity.number"u8, s_encoder);
    private static readonly JsonEncodedText s_serviceName = JsonEncodedText.Encode("service.name"u8, s_encoder);
    private static readonly JsonEncodedText s_sdkName = JsonEncodedText.Encode("telemetry.sdk.name"u8, s_encoder);
    private static readonly JsonEncodedText s_sdkLanguage = JsonEncodedText.Encode("telemetry.sdk.language"u8, s_encoder);
    private static readonly JsonEncodedText s_sdkVersion = JsonEncodedText.Encode("telemetry.sdk.version"u8, s_encoder);
    private static readonly string s_version = typeof(SplunkLogger).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private static readonly JsonEncodedText s_scopeName = JsonEncodedText.Encode("scope.name"u8, s_encoder);
    private static readonly JsonEncodedText s_eventId = JsonEncodedText.Encode("logrecord.event.id"u8, s_encoder);
    private static readonly JsonEncodedText s_logName = JsonEncodedText.Encode("otel.log.name"u8, s_encoder);
    private static readonly JsonEncodedText s_exceptionType = JsonEncodedText.Encode("exception.type"u8, s_encoder);
    private static readonly JsonEncodedText s_exceptionMessage = JsonEncodedText.Encode("exception.message"u8, s_encoder);
    private static readonly JsonEncodedText s_exceptionStackTrace = JsonEncodedText.Encode("exception.stacktrace"u8, s_encoder);
    private static readonly JsonEncodedText s_traceId = JsonEncodedText.Encode("trace_id"u8, s_encoder);
    private static readonly JsonEncodedText s_spanId = JsonEncodedText.Encode("span_id"u8, s_encoder);
    private static readonly JsonEncodedText s_sevTrace = JsonEncodedText.Encode("Trace"u8, s_encoder);
    private static readonly JsonEncodedText s_sevDebug = JsonEncodedText.Encode("Debug"u8, s_encoder);
    private static readonly JsonEncodedText s_sevInformation = JsonEncodedText.Encode("Information"u8, s_encoder);
    private static readonly JsonEncodedText s_sevWarning = JsonEncodedText.Encode("Warning"u8, s_encoder);
    private static readonly JsonEncodedText s_sevError = JsonEncodedText.Encode("Error"u8, s_encoder);
    private static readonly JsonEncodedText s_sevCritical = JsonEncodedText.Encode("Critical"u8, s_encoder);

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
        provider.Enqueue(Serialize(DateTimeOffset.UtcNow, logLevel, eventId, exception?.GetType().Name, exception?.Message, exception?.ToString(), formatter(state, exception),
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
        using (var w = new Utf8JsonWriter(stream, s_writerOptions))
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
            w.WriteString(s_serviceName, o.ServiceName);
            w.WriteString(s_sdkName, "opentelemetry"u8);
            w.WriteString(s_sdkLanguage, "dotnet"u8);
            w.WriteString(s_sdkVersion, s_version);
            w.WriteString(s_scopeName, category);
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
                    WriteValue(w, key, value);
                }
            }

            if (includeScopes) provider.ScopeProvider?.ForEachScope((scope, writer) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> kvs)
                    foreach (var (key, value) in kvs)
                    {
                        if (string.IsNullOrEmpty(key) || key == "{OriginalFormat}") continue;
                        WriteValue(writer, key, value);
                    }
            }, w);
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static JsonEncodedText SeverityText(LogLevel l) => l switch
    {
        LogLevel.Trace => s_sevTrace,
        LogLevel.Debug => s_sevDebug,
        LogLevel.Information => s_sevInformation,
        LogLevel.Warning => s_sevWarning,
        LogLevel.Error => s_sevError,
        _ => s_sevCritical,
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



