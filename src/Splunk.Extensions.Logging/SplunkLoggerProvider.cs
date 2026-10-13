using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.IO.Pipelines;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Splunk.Extensions.Logging;

/// <summary>Logger provider that ships events to Splunk via the HTTP Event Collector.</summary>
[ProviderAlias("Splunk")]
public sealed class SplunkLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly SplunkLoggerOptions _options;
    private readonly JsonWriterOptions _writerOptions;
    private readonly HttpClient _client;
    private readonly ArrayPool<byte> _pool;
    private readonly Channel<ReadOnlySequence<byte>> _channel;
    private readonly Task _worker;
    private readonly Task _timerTask;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _stop = new();
    private IExternalScopeProvider? _scopeProvider;

    public SplunkLoggerProvider(IOptions<SplunkLoggerOptions> options)
    {
        _options = options.Value;
        var json = _options.JsonSerializerOptions;
        _writerOptions = new JsonWriterOptions
        {
            Encoder = json.Encoder,
            Indented = json.WriteIndented,
            IndentCharacter = json.IndentCharacter,
            IndentSize = json.IndentSize,
            NewLine = json.NewLine,
            MaxDepth = json.MaxDepth,
        };
        _client = _options.HttpClient;
        _pool = _options.ArrayPool;

        _channel = Channel.CreateBounded<ReadOnlySequence<byte>>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        }, OnItemDropped);
        _timer = new PeriodicTimer(_options.FlushInterval);
        _timerTask = Task.Run(RunTimerAsync);
        _worker = Task.Run(RunAsync);
    }

    public ILogger CreateLogger(string categoryName) => new SplunkLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    internal IExternalScopeProvider? ScopeProvider => _options.IncludeScopes ? _scopeProvider : null;

    internal void Enqueue(ReadOnlySequence<byte> entry)
    {
        if (!_channel.Writer.TryWrite(entry)) Return(entry);
    }

    /// <summary>Flushes queued events (bounded by ShutdownTimeout) and releases resources.</summary>
    public void Dispose()
    {
        _timer.Dispose();
        _timerTask.Wait();
        _channel.Writer.TryComplete();
        try
        {
            if (!_worker.Wait(_options.ShutdownTimeout))
            {
                _stop.Cancel();
                ReportError(new TimeoutException(
                    $"Splunk logger did not flush within {_options.ShutdownTimeout}; pending events were dropped."));
            }
            _worker.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex)
        {
            ReportError(ex.InnerException ?? ex);
        }

        _stop.Dispose();
    }

    private void OnItemDropped(ReadOnlySequence<byte> item)
    {
        var isEvent = !item.IsEmpty;
        Return(item);
        if (isEvent)
            try { _options.OnDropped?.Invoke(); } catch { }
    }

    private void ReportError(Exception ex)
    {
        try { _options.OnError?.Invoke(ex); } catch { }
    }

    private async Task RunTimerAsync()
    {
        while (await _timer.WaitForNextTickAsync().ConfigureAwait(false))
            _channel.Writer.TryWrite(default);
    }

    private async Task RunAsync()
    {
        var batch = new List<ReadOnlySequence<byte>>(_options.BatchSize);
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (!item.IsEmpty) batch.Add(item);
                if (batch.Count == 0 || (!item.IsEmpty && batch.Count < _options.BatchSize)) continue;

                await SendAsync(batch).ConfigureAwait(false);
                ReleaseBatch(batch);
            }

            if (batch.Count > 0) await SendAsync(batch).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            ReleaseBatch(batch);
        }
    }

    internal void Return(ReadOnlySequence<byte> sequence)
    {
        foreach (var memory in sequence)
            if (MemoryMarshal.TryGetArray(memory, out var segment) && segment.Array is { } array)
                _pool.Return(array);
    }

    private void ReleaseBatch(List<ReadOnlySequence<byte>> batch)
    {
        foreach (var e in batch) Return(e);
        batch.Clear();
    }

    private async Task SendAsync(List<ReadOnlySequence<byte>> batch)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = new BufferListContent(batch),
            };
            using var response = await _client.SendAsync(request, _stop.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    internal void Write(string category, DateTimeOffset timestamp, LogLevel level, EventId eventId, string? exceptionType,
        string? exceptionMessage, string? exceptionStackTrace, string message, ActivityTraceId? traceId, ActivitySpanId? spanId,
        IEnumerable<KeyValuePair<string, object?>>? props, bool includeScopes)
    {
        var o = _options;
        using var buffer = new PooledBufferWriter(this, _pool);
        try
        {
            using var w = new Utf8JsonWriter(buffer, _writerOptions);
                w.WriteStartObject();
                w.WriteNumber(SplunkJson.Time, timestamp.ToUnixTimeMilliseconds() / 1000.0);
                if (o.Host is not null) w.WriteString(SplunkJson.Host, o.Host);
                if (o.Source is not null) w.WriteString(SplunkJson.Source, o.Source);
                if (o.SourceType is not null) w.WriteString(SplunkJson.SourceType, o.SourceType);
                if (o.Index is not null) w.WriteString(SplunkJson.Index, o.Index);

                w.WriteString(SplunkJson.Event, message);

                w.WriteStartObject(SplunkJson.Fields);
                w.WriteString(SplunkJson.SeverityTextField, SplunkJson.SeverityText(level));
                w.WriteNumber(SplunkJson.SeverityNumberField, SplunkJson.SeverityNumber(level));
                w.WriteString(SplunkJson.ServiceName, o.ServiceName);
                w.WriteString(SplunkJson.SdkName, SplunkJson.SdkNameValue);
                w.WriteString(SplunkJson.SdkLanguage, SplunkJson.SdkLanguageValue);
                w.WriteString(SplunkJson.SdkVersion, SplunkJson.Version);
                w.WriteString(SplunkJson.ScopeName, category);
                if (eventId.Id != 0) w.WriteNumber(SplunkJson.EventId, eventId.Id);
                if (eventId.Name is not null) w.WriteString(SplunkJson.LogName, eventId.Name);
                if (exceptionType is not null) w.WriteString(SplunkJson.ExceptionType, exceptionType);
                if (exceptionMessage is not null) w.WriteString(SplunkJson.ExceptionMessage, exceptionMessage);
                if (exceptionStackTrace is not null) w.WriteString(SplunkJson.ExceptionStackTrace, exceptionStackTrace);
                if (traceId is { } tid) w.WriteString(SplunkJson.TraceId, tid.ToHexString());
                if (spanId is { } sid) w.WriteString(SplunkJson.SpanId, sid.ToHexString());

                if (props is not null)
                {
                    foreach (var (key, value) in props)
                    {
                        WriteValue(w, key, value);
                    }
                }

                if (includeScopes) ScopeProvider?.ForEachScope((scope, state) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> kvs)
                        foreach (var (key, value) in kvs)
                        {
                            if (string.IsNullOrEmpty(key) || key == "{OriginalFormat}") continue;
                            state.Provider.WriteValue(state.Writer, key, value);
                        }
                }, (Writer: w, Provider: this));
                w.WriteEndObject();

                w.WriteEndObject();
        }
        catch (Exception ex)
        {
            buffer.Discard();
            ReportError(ex);
        }
    }

    private void WriteValue(Utf8JsonWriter w, string key, object? value)
    {
        if (_options.SerializeComplexValues)
        {
            w.WritePropertyName(key);
            if (value is null)
                w.WriteNullValue();
            else if (_options.JsonSerializerOptions.TryGetTypeInfo(value.GetType(), out var typeInfo))
                JsonSerializer.Serialize(w, value, typeInfo);
            else
                w.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
            return;
        }

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
