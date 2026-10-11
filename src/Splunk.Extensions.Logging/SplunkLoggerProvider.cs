using System.Buffers;
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

    internal SplunkLoggerOptions Options => _options;

    internal IExternalScopeProvider? ScopeProvider => _options.IncludeScopes ? _scopeProvider : null;

    /// <summary>Gets a pooled writer; disposing it queues the written event.</summary>
    internal PooledBufferWriter GetWriter() => new(this, _pool);

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

    private void Return(ReadOnlySequence<byte> sequence)
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
}
