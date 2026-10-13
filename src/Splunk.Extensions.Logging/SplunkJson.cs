using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Splunk.Extensions.Logging;

/// <summary>Field names, encoded constants and value writers for the Splunk HEC event JSON.</summary>
internal static class SplunkJson
{
    internal static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    internal static readonly JsonEncodedText Time = JsonEncodedText.Encode("time"u8, Encoder);
    internal static readonly JsonEncodedText Host = JsonEncodedText.Encode("host"u8, Encoder);
    internal static readonly JsonEncodedText Source = JsonEncodedText.Encode("source"u8, Encoder);
    internal static readonly JsonEncodedText SourceType = JsonEncodedText.Encode("sourcetype"u8, Encoder);
    internal static readonly JsonEncodedText Index = JsonEncodedText.Encode("index"u8, Encoder);
    internal static readonly JsonEncodedText Event = JsonEncodedText.Encode("event"u8, Encoder);
    internal static readonly JsonEncodedText Fields = JsonEncodedText.Encode("fields"u8, Encoder);
    internal static readonly JsonEncodedText SeverityTextField = JsonEncodedText.Encode("otel.log.severity.text"u8, Encoder);
    internal static readonly JsonEncodedText SeverityNumberField = JsonEncodedText.Encode("otel.log.severity.number"u8, Encoder);
    internal static readonly JsonEncodedText ServiceName = JsonEncodedText.Encode("service.name"u8, Encoder);
    internal static readonly JsonEncodedText SdkName = JsonEncodedText.Encode("telemetry.sdk.name"u8, Encoder);
    internal static readonly JsonEncodedText SdkLanguage = JsonEncodedText.Encode("telemetry.sdk.language"u8, Encoder);
    internal static readonly JsonEncodedText SdkNameValue = JsonEncodedText.Encode("opentelemetry"u8, Encoder);
    internal static readonly JsonEncodedText SdkLanguageValue = JsonEncodedText.Encode("dotnet"u8, Encoder);
    internal static readonly JsonEncodedText SdkVersion = JsonEncodedText.Encode("telemetry.sdk.version"u8, Encoder);
    internal static readonly JsonEncodedText Version = JsonEncodedText.Encode(typeof(SplunkLogger).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", Encoder);
    internal static readonly JsonEncodedText ScopeName = JsonEncodedText.Encode("scope.name"u8, Encoder);
    internal static readonly JsonEncodedText EventId = JsonEncodedText.Encode("logrecord.event.id"u8, Encoder);
    internal static readonly JsonEncodedText LogName = JsonEncodedText.Encode("otel.log.name"u8, Encoder);
    internal static readonly JsonEncodedText ExceptionType = JsonEncodedText.Encode("exception.type"u8, Encoder);
    internal static readonly JsonEncodedText ExceptionMessage = JsonEncodedText.Encode("exception.message"u8, Encoder);
    internal static readonly JsonEncodedText ExceptionStackTrace = JsonEncodedText.Encode("exception.stacktrace"u8, Encoder);
    internal static readonly JsonEncodedText TraceId = JsonEncodedText.Encode("trace_id"u8, Encoder);
    internal static readonly JsonEncodedText SpanId = JsonEncodedText.Encode("span_id"u8, Encoder);
    internal static readonly JsonEncodedText SevTrace = JsonEncodedText.Encode("Trace"u8, Encoder);
    internal static readonly JsonEncodedText SevDebug = JsonEncodedText.Encode("Debug"u8, Encoder);
    internal static readonly JsonEncodedText SevInformation = JsonEncodedText.Encode("Information"u8, Encoder);
    internal static readonly JsonEncodedText SevWarning = JsonEncodedText.Encode("Warning"u8, Encoder);
    internal static readonly JsonEncodedText SevError = JsonEncodedText.Encode("Error"u8, Encoder);
    internal static readonly JsonEncodedText SevCritical = JsonEncodedText.Encode("Critical"u8, Encoder);

    internal static JsonEncodedText SeverityText(LogLevel l) => l switch
    {
        LogLevel.Trace => SevTrace,
        LogLevel.Debug => SevDebug,
        LogLevel.Information => SevInformation,
        LogLevel.Warning => SevWarning,
        LogLevel.Error => SevError,
        _ => SevCritical,
    };

    internal static int SeverityNumber(LogLevel l) => l switch
    {
        LogLevel.Trace => 1,
        LogLevel.Debug => 5,
        LogLevel.Information => 9,
        LogLevel.Warning => 13,
        LogLevel.Error => 17,
        _ => 21,
    };
}
