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
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
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
        _channel.Writer.TryComplete();
        try
        {
            if (!_worker.Wait(_options.ShutdownTimeout))
                _stop.Cancel();
            _worker.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException) { }

        if (_ownsClient) _client.Dispose();
        _stop.Dispose();
    }

    private static Uri BuildEndpoint(Uri endpoint)
    {
        if (endpoint.AbsolutePath.Contains("/services/collector", StringComparison.OrdinalIgnoreCase))
            return endpoint;
        return new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/services/collector/event");
    }

    private async Task RunAsync()
    {
        var reader = _channel.Reader;
        var batch = new List<byte[]>(_options.BatchSize);
        try
        {
            while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                batch.Clear();
                using var window = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                window.CancelAfter(_options.FlushInterval);
                try
                {
                    while (batch.Count < _options.BatchSize)
                    {
                        if (reader.TryRead(out var item)) batch.Add(item);
                        else if (!await reader.WaitToReadAsync(window.Token).ConfigureAwait(false)) break;
                    }
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { }

                if (batch.Count > 0) await SendAsync(batch).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendAsync(List<byte[]> batch)
    {
        var body = new byte[batch.Sum(b => b.Length)];
        var offset = 0;
        foreach (var b in batch)
        {
            b.CopyTo(body, offset);
            offset += b.Length;
        }

        var delay = _options.RetryDelay;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = new ByteArrayContent(body),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
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
                    try { _options.OnError?.Invoke(ex); } catch { }
                    return;
                }
                try { await Task.Delay(delay, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                delay += delay;
            }
        }
    }
}
