# Splunk.Extensions.Logging

A [Microsoft.Extensions.Logging](https://learn.microsoft.com/dotnet/core/extensions/logging) provider that sends log events to [Splunk](https://www.splunk.com/).

## Usage

```csharp
builder.Logging.AddSplunk(o =>
{
    o.Endpoint = new Uri("https://splunk.example.com:8088");
    o.Token = "<HEC token>";
    o.Index = "main";
    o.Source = "my-app";
});
```

Events are queued, batched and sent to the HTTP Event Collector (`/services/collector/event`) on a background task. Pending events are flushed when the provider is disposed.

### Retries

By default the provider creates its own `HttpClient` with a `RetryDelegatingHandler` that retries failed requests (exceptions and non-success status codes) up to `MaxRetries` times (default 3), starting with `RetryDelay` (default 1 second) and doubling it on each retry. Batches that still fail are dropped and reported through `OnError`.

### Custom `HttpClient`

Use the overload that receives the `IServiceProvider` to supply your own `HttpClient`, for example one created by `IHttpClientFactory`. The `Authorization: Splunk <token>` header is added from `Token` unless the client already has one. The provider never disposes the client, and `MaxRetries`/`RetryDelay` are not applied to it.

```csharp
builder.Services.AddHttpClient("splunk");

builder.Logging.AddSplunk((sp, o) =>
{
    o.Endpoint = new Uri("https://splunk.example.com:8088");
    o.Token = "<HEC token>";
    o.HttpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("splunk");
});
```

### Retries with Polly

To use [Polly](https://www.pollydocs.org/) instead of the built-in retries, add a resilience handler to the named client (package `Microsoft.Extensions.Http.Resilience`, or `Microsoft.Extensions.Http.Polly` for the legacy API):

```csharp
builder.Services.AddHttpClient("splunk")
    .AddResilienceHandler("splunk", pipeline => pipeline
        .AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 5,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
        })
        .AddTimeout(TimeSpan.FromSeconds(30)));
```

`AddStandardResilienceHandler()` is a ready-made alternative (retry, circuit breaker and timeouts). Then pass the client through `o.HttpClient` as shown above.

## Event format

Events follow the layout produced by the [splunk-otel-collector](https://github.com/signalfx/splunk-otel-collector) HEC exporter: the formatted message is the `event`, and all other data is flattened into `fields`.

```json
{
  "time": 1760135438.834,
  "host": "my-host",
  "source": "my-app",
  "index": "main",
  "event": "Hello world",
  "fields": {
    "otel.log.severity.text": "Warning",
    "otel.log.severity.number": 13,
    "scope.name": "MyApp.Category",
    "logrecord.event.id": 5,
    "otel.log.name": "Evt",
    "exception.type": "InvalidOperationException",
    "exception.message": "boom",
    "exception.stacktrace": "...",
    "trace_id": "...",
    "span_id": "...",
    "Name": "world"
  }
}
```

### Mapping

| Source (MEL) | HEC location |
|---|---|
| Formatted message | `event` |
| Time of logging (UTC) | `time` (epoch seconds) |
| `SplunkLoggerOptions.Host` / `Source` / `SourceType` / `Index` | `host` / `source` / `sourcetype` / `index` |
| `LogLevel` | `fields["otel.log.severity.text"]`: Trace=`Trace`, Debug=`Debug`, Information=`Information`, Warning=`Warning`, Error=`Error`, Critical=`Critical` |
| `LogLevel` | `fields["otel.log.severity.number"]`: 1, 5, 9, 13, 17, 21 respectively |
| Category name | `fields["scope.name"]` |
| `EventId.Id` (when non-zero) | `fields["logrecord.event.id"]` |
| `EventId.Name` | `fields["otel.log.name"]` |
| `Exception` | `fields["exception.type"]`, `["exception.message"]`, `["exception.stacktrace"]` |
| `Activity.Current` | `fields["trace_id"]`, `fields["span_id"]` |
| Structured message properties | `fields[<name>]` (including `{OriginalFormat}`) |
| Resource attributes | `fields["service.name"]` (`ServiceName` option, default `unknown_service:<process>`), `fields["telemetry.sdk.name"]`/`language`/`version` |
| Scopes (key/value pairs, when `IncludeScopes` is on) | `fields[<name>]`; non-structured scopes are ignored |

## Integration tests

`tests/Splunk.Extensions.Logging.IntegrationTests` sends the same log calls through this provider (directly to HEC) and through the OpenTelemetry .NET logger (OTLP -> splunk-otel-collector -> HEC), then queries Splunk and compares the indexed events. They are skipped when Splunk is not running.

```bash
docker compose -f tests/integration/docker-compose.yml up -d   # Splunk takes ~1-2 minutes to become healthy
dotnet test tests/Splunk.Extensions.Logging.IntegrationTests
docker compose -f tests/integration/docker-compose.yml down
```

Event text, severity, exception, properties, scopes and `otel.log.name` are identical. Intentional differences: this provider also emits `scope.name` and `logrecord.event.id`, sets `host` to the machine name, and reports its own `telemetry.sdk.version`.

## Solution Structure

| Project | Path | Purpose |
|---|---|---|
| `Splunk.Extensions.Logging` | `src/Splunk.Extensions.Logging/` | Main library |
| `Splunk.Extensions.Logging.Tests` | `tests/Splunk.Extensions.Logging.Tests/` | Unit tests |

## Build & Test

```bash
# Build the solution
dotnet build

# Run all tests
dotnet test
```

## License

MIT, see [LICENSE](./LICENSE).
