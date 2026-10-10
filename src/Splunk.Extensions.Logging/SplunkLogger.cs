using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Splunk.Extensions.Logging;

internal sealed class SplunkLogger(string category, SplunkLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => provider.ScopeProvider?.Push(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        ArgumentNullException.ThrowIfNull(formatter);
        provider.Enqueue(Serialize(logLevel, eventId, state, exception, formatter(state, exception)));
    }

    private byte[] Serialize<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, string message)
    {
        var o = provider.Options;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
            if (o.Host is not null) w.WriteString("host", o.Host);
            if (o.Source is not null) w.WriteString("source", o.Source);
            if (o.SourceType is not null) w.WriteString("sourcetype", o.SourceType);
            if (o.Index is not null) w.WriteString("index", o.Index);

            w.WriteString("event", message);

            w.WriteStartObject("fields");
            w.WriteString("otel.log.severity.text", SeverityText(level));
            w.WriteNumber("otel.log.severity.number", SeverityNumber(level));
            w.WriteString("otel.library.name", category);
            if (eventId.Id != 0) w.WriteNumber("logrecord.event.id", eventId.Id);
            if (eventId.Name is not null) w.WriteString("otel.log.name", eventId.Name);
            if (exception is not null)
            {
                w.WriteString("exception.type", exception.GetType().FullName);
                w.WriteString("exception.message", exception.Message);
                w.WriteString("exception.stacktrace", exception.ToString());
            }
            if (Activity.Current is { } activity)
            {
                w.WriteString("trace_id", activity.TraceId.ToHexString());
                w.WriteString("span_id", activity.SpanId.ToHexString());
            }

            if (state is IEnumerable<KeyValuePair<string, object?>> props)
            {
                foreach (var (key, value) in props)
                {
                    if (key == "{OriginalFormat}") continue;
                    WriteValue(w, key, value);
                }
            }

            provider.ScopeProvider?.ForEachScope((scope, writer) =>
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

