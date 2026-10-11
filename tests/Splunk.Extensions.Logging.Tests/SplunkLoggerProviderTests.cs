using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Splunk.Extensions.Logging.Tests;

public class SplunkLoggerProviderTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<(string Auth, string Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.Headers.Authorization!.ToString(), request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    private static SplunkLoggerProvider Create(CaptureHandler handler)
    {
        var options = new SplunkLoggerOptions
        {
            Endpoint = new Uri("https://splunk:8088"),
            Token = "tok",
            Index = "main",
            FlushInterval = TimeSpan.FromMilliseconds(50),
        };
        return Build(options, handler);
    }

    private static SplunkLoggerProvider Build(SplunkLoggerOptions options, HttpMessageHandler handler)
    {
        options.HttpClient = new HttpClient(handler);
        new SplunkLoggerOptionsPostConfigure().PostConfigure(null, options);
        return new(Options.Create(options));
    }

    [Fact]
    public void Requires_endpoint_and_token()
    {
        using var services = new ServiceCollection()
            .AddLogging(b => b.AddSplunk(_ => { }))
            .BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() => services.GetServices<ILoggerProvider>().ToList());
        Assert.Contains(ex.Failures, f => f.Contains("Endpoint"));
        Assert.Contains(ex.Failures, f => f.Contains("Token"));
    }

    [Fact]
    public void Sends_events_to_hec_on_dispose()
    {
        var handler = new CaptureHandler();
        var provider = Create(handler);
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        var logger = provider.CreateLogger("Cat");

        using (logger.BeginScope("scope {Id}", 7))
            logger.LogWarning(new EventId(5, "Evt"), new InvalidOperationException("boom"), "Hello {Name}", "world");
        logger.LogInformation("second");
        provider.Dispose();

        var req = Assert.Single(handler.Requests);
        Assert.Equal("Splunk tok", req.Auth);
        Assert.Equal("https://splunk:8088/services/collector/event", req.Uri);

        var events = new List<JsonElement>();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(req.Body), new JsonReaderOptions { AllowMultipleValues = true });
        while (reader.Read() && JsonDocument.TryParseValue(ref reader, out var doc)) events.Add(doc.RootElement.Clone());

        Assert.Equal(2, events.Count);
        Assert.Equal("main", events[0].GetProperty("index").GetString());
        Assert.Equal("Hello world", events[0].GetProperty("event").GetString());
        var f = events[0].GetProperty("fields");
        Assert.Equal("Warning", f.GetProperty("otel.log.severity.text").GetString());
        Assert.Equal(13, f.GetProperty("otel.log.severity.number").GetInt32());
        Assert.Equal("Cat", f.GetProperty("scope.name").GetString());
        Assert.Equal("world", f.GetProperty("Name").GetString());
        Assert.Equal(5, f.GetProperty("logrecord.event.id").GetInt32());
        Assert.Equal("Evt", f.GetProperty("otel.log.name").GetString());
        Assert.Equal("InvalidOperationException", f.GetProperty("exception.type").GetString());
        Assert.Equal("boom", f.GetProperty("exception.message").GetString());
        Assert.Equal(7, f.GetProperty("Id").GetInt32());    }

    [Fact]
    public void Buffered_records_are_sent_with_original_timestamp()
    {
        var handler = new CaptureHandler();
        var provider = Create(handler);
        var logger = (IBufferedLogger)provider.CreateLogger("Cat");
        var ts = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        logger.LogRecords([new TestRecord(ts)]);
        provider.Dispose();

        var req = Assert.Single(handler.Requests);
        using var doc = JsonDocument.Parse(req.Body);
        Assert.Equal(ts.ToUnixTimeMilliseconds() / 1000.0, doc.RootElement.GetProperty("time").GetDouble());
        Assert.Equal("buffered", doc.RootElement.GetProperty("event").GetString());
        var f = doc.RootElement.GetProperty("fields");
        Assert.Equal("Error", f.GetProperty("otel.log.severity.text").GetString());
        Assert.Equal(1, f.GetProperty("K").GetInt32());
    }

    private sealed class TestRecord(DateTimeOffset ts) : BufferedLogRecord
    {
        public override DateTimeOffset Timestamp => ts;
        public override LogLevel LogLevel => LogLevel.Error;
        public override EventId EventId => default;
        public override string? Exception => null;
        public override string? FormattedMessage => "buffered";
        public override IReadOnlyList<KeyValuePair<string, object?>> Attributes => [new("K", 1)];
    }

    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Bodies { get; } = [];

        public Task FirstRequestStarted => _firstRequestStarted.Task;
        public void Release() => _gate.SetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (Bodies) Bodies.Add(body);
            _firstRequestStarted.TrySetResult();
            await _gate.Task.WaitAsync(ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Drops_oldest_events_when_queue_is_full()
    {
        var handler = new GatedHandler();
        var provider = Build(new SplunkLoggerOptions
        {
            Endpoint = new Uri("https://splunk:8088"),
            Token = "tok",
            BatchSize = 1,
            QueueCapacity = 2,
            FlushInterval = TimeSpan.FromHours(1),
        }, handler);
        var logger = provider.CreateLogger("Cat");

        // The worker takes the first event and blocks sending it, leaving the queue empty.
        logger.LogInformation("m1");
        await handler.FirstRequestStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        for (var i = 2; i <= 6; i++) logger.LogInformation("m{I}", i);

        handler.Release();
        provider.Dispose();

        var messages = handler.Bodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("event").GetString()!).ToArray();
        Assert.Equal(["m1", "m5", "m6"], messages);
    }

    [Fact]
    public void Reports_error_when_dispose_times_out()
    {
        var handler = new GatedHandler();
        Exception? error = null;
        var provider = Build(new SplunkLoggerOptions
        {
            Endpoint = new Uri("https://splunk:8088"),
            Token = "tok",
            BatchSize = 1,
            ShutdownTimeout = TimeSpan.FromMilliseconds(100),
            OnError = e => error = e,
        }, handler);

        provider.CreateLogger("Cat").LogInformation("stuck");
        provider.Dispose();

        Assert.IsType<TimeoutException>(error);
    }

    [Fact]
    public void AddSplunk_registers_provider()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSplunk(o => { o.Endpoint = new Uri("https://splunk:8088"); o.Token = "t"; }));
        using var sp = services.BuildServiceProvider();
        var providers = sp.GetServices<ILoggerProvider>();
        Assert.Contains(providers, p => p is SplunkLoggerProvider);
    }
}





