using System.Net.Http.Headers;
using System.Text;
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
    private readonly bool _ownsClient;
    private readonly Uri _endpoint;
    private readonly Channel<byte[]> _channel;
    private readonly Task _worker;
    private readonly Task _timerTask;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _stop = new();
    private IExternalScopeProvider? _scopeProvider;

    public SplunkLoggerProvider(IOptions<SplunkLoggerOptions> options) : this(options, null) { }

    internal SplunkLoggerProvider(IOptions<SplunkLoggerOptions> options, HttpClient? client)
    {
        _options = options.Value;
        if (_options.Endpoint is null)
            throw new ArgumentException("SplunkLoggerOptions.Endpoint is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(_options.Token))
            throw new ArgumentException("SplunkLoggerOptions.Token is required.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.QueueCapacity, 1);

        _endpoint = BuildEndpoint(_options.Endpoint);
        _ownsClient = client is null;
        _client = client ?? new HttpClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Splunk", _options.Token);

        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _timer = new PeriodicTimer(_options.FlushInterval);
        _timerTask = Task.Run(RunTimerAsync);
        _worker = Task.Run(RunAsync);
    }

    public ILogger CreateLogger(string categoryName) => new SplunkLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    internal SplunkLoggerOptions Options => _options;

    internal IExternalScopeProvider? ScopeProvider => _options.IncludeScopes ? _scopeProvider : null;

    internal void Enqueue(byte[] payload) => _channel.Writer.TryWrite(payload);

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

        if (_ownsClient) _client.Dispose();
        _stop.Dispose();
    }

    private void ReportError(Exception ex)
    {
        try { _options.OnError?.Invoke(ex); } catch { }
    }

    private static Uri BuildEndpoint(Uri endpoint)
    {
        if (endpoint.AbsolutePath.Contains("/services/collector", StringComparison.OrdinalIgnoreCase))
            return endpoint;
        return new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/services/collector/event");
    }

    // Empty arrays are flush markers injected by the timer; real payloads are never empty.
    private async Task RunTimerAsync()
    {
        while (await _timer.WaitForNextTickAsync().ConfigureAwait(false))
            _channel.Writer.TryWrite([]);
    }

    private async Task RunAsync()
    {
        var batch = new List<byte[]>(_options.BatchSize);
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (item.Length > 0) batch.Add(item);
                if (batch.Count == 0 || (item.Length > 0 && batch.Count < _options.BatchSize)) continue;

                await SendAsync(batch).ConfigureAwait(false);
                batch.Clear();
            }

            if (batch.Count > 0) await SendAsync(batch).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }
    private async Task SendAsync(List<byte[]> batch)
    {
        var delay = _options.RetryDelay;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = new BufferListContent(batch),
                };
                using var response = await _client.SendAsync(request, _stop.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= _options.MaxRetries)
                {
                    ReportError(ex);
                    return;
                }
                try { await Task.Delay(delay, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                delay += delay;
            }
        }
    }
}
