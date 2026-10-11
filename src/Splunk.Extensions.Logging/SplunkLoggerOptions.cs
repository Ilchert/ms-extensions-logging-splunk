namespace Splunk.Extensions.Logging;

/// <summary>Options for the Splunk HTTP Event Collector (HEC) logger provider.</summary>
public class SplunkLoggerOptions
{
    /// <summary>Base HEC URL, e.g. https://splunk:8088. The /services/collector/event path is appended unless already present.</summary>
    public Uri Endpoint { get; set; } = null!;

    /// <summary>HEC token.</summary>
    public string Token { get; set; } = null!;

    /// <summary>HTTP client used to send events. Created if not set and never disposed by the provider. The Authorization header is set from <see cref="Token"/> unless already present.</summary>
    public HttpClient HttpClient { get; set; } = null!;

    public string? Index { get; set; }
    public string? Source { get; set; }
    public string? SourceType { get; set; } = "_json";

    /// <summary>Host field. Defaults to the machine name.</summary>
    public string? Host { get; set; } = Environment.MachineName;

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

    /// <summary>Called when a batch could not be delivered and was dropped.</summary>
    public Action<Exception>? OnError { get; set; }
}
