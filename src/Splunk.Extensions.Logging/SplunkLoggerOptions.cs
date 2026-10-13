using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Splunk.Extensions.Logging;

/// <summary>Options for the Splunk HTTP Event Collector (HEC) logger provider.</summary>
public class SplunkLoggerOptions
{
    /// <summary>Base HEC URL, e.g. https://splunk:8088. The /services/collector/event path is appended unless already present.</summary>
    public Uri Endpoint { get; set; } = null!;

    /// <summary>HEC token.</summary>
    public string Token { get; set; } = null!;

    /// <summary>HTTP client used to send events. Created if not set (with retries per <see cref="MaxRetries"/> and <see cref="RetryDelay"/>) and never disposed by the provider. The Authorization header is set from <see cref="Token"/> unless already present.</summary>
    public HttpClient HttpClient { get; set; } = null!;

    public string? Index { get; set; }
    public string? Source { get; set; }
    /// <summary>Optional sourcetype. Avoid <c>_json</c>: Splunk silently drops events whose text is not JSON.</summary>
    public string? SourceType { get; set; }

    /// <summary>Host field. Defaults to the machine name.</summary>
    public string? Host { get; set; } = Environment.MachineName;

    /// <summary>Value of the <c>service.name</c> field. Defaults to <c>unknown_service:&lt;process name&gt;</c>, like the OpenTelemetry SDK.</summary>
    public string ServiceName { get; set; } = "unknown_service:" + System.Diagnostics.Process.GetCurrentProcess().ProcessName;

    /// <summary>
    /// Serializer options used when <see cref="SerializeComplexValues"/> is enabled. Defaults to named floating point literals
    /// (<c>"NaN"</c>, <c>"Infinity"</c>) being allowed. The <see cref="JsonSerializerOptions.Encoder"/> (default <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>) and the writer settings (indentation, new line, max depth) are also used for the event itself.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.General)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Serialize all property and scope values (including primitives, enums, collections and custom types)
    /// as JSON using <see cref="JsonSerializerOptions"/>. When false only strings, booleans and finite numbers keep their JSON type; everything else is written as its <c>ToString()</c> text. A value that fails to serialize drops the event and calls <see cref="OnError"/>.
    /// </summary>
    public bool SerializeComplexValues { get; set; }

    /// <summary>Include logger scopes in events.</summary>
    public bool IncludeScopes { get; set; } = true;

    /// <summary>Maximum number of events per HTTP request.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Maximum time to wait for a batch to fill.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum number of queued events; when full, the oldest events are dropped.</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>Number of retries for failed requests.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Delay before first retry; doubles on each retry.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Time allowed to flush queued events on dispose.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Called when a batch could not be delivered and was dropped, or the provider failed to shut down cleanly.</summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>Called for each event dropped because the queue was full. Runs on the logging thread, so keep it cheap.</summary>
    public Action? OnDropped { get; set; }
}
