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

Events are queued, batched and sent to the HTTP Event Collector (`/services/collector/event`) on a background task, with retries. Pending events are flushed when the provider is disposed.

## Event format

Events follow the layout produced by the [splunk-otel-collector](https://github.com/signalfx/splunk-otel-collector) HEC exporter: the formatted message is the `event`, and all other data is flattened into `fields`.

```json
{
  "time": 1760135438.834,
  "host": "my-host",
  "source": "my-app",
  "sourcetype": "_json",
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
| Structured message properties | `fields[<name>]` (`{OriginalFormat}` omitted) |
| Scopes (key/value pairs, when `IncludeScopes` is on) | `fields[<name>]`; non-structured scopes are ignored |

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
