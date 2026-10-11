using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Splunk.Extensions.Logging.Benchmarks;

[MemoryDiagnoser]
public class LoggingBenchmarks
{
    private const int EventsPerInvocation = 10_000;

    private sealed class NullHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await request.Content!.CopyToAsync(Stream.Null, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    private ServiceProvider? _services;
    private ILogger _logger = null!;
    private string _message = null!;

    /// <summary>Size of the {Payload} argument in characters.</summary>
    [Params(16, 4096)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _message = new string('x', PayloadSize);
        _services = Build(queueCapacity: 100_000);
        _logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("Bench");
    }

    [GlobalCleanup]
    public void Cleanup() => _services?.Dispose();

    private static ServiceProvider Build(int queueCapacity) => new ServiceCollection()
        .AddLogging(b => b.AddSplunk(o =>
        {
            o.Endpoint = new Uri("https://splunk:8088");
            o.Token = "tok";
            o.HttpClient = new HttpClient(new NullHandler());
            o.QueueCapacity = queueCapacity;
            o.BatchSize = 100;
        }))
        .BuildServiceProvider();

    /// <summary>Cost of the logging call itself (serialize and enqueue); the worker drains concurrently.</summary>
    [Benchmark(OperationsPerInvoke = EventsPerInvocation)]
    public void LogCall()
    {
        for (var i = 0; i < EventsPerInvocation; i++)
            _logger.LogInformation("Hello {Payload} {Count}", _message, i);
    }

    /// <summary>End to end: log events, then dispose to flush everything through the HTTP pipeline.</summary>
    [Benchmark(OperationsPerInvoke = EventsPerInvocation)]
    public void LogAndFlush()
    {
        using var services = Build(queueCapacity: EventsPerInvocation * 2);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Bench");
        for (var i = 0; i < EventsPerInvocation; i++)
            logger.LogInformation("Hello {Payload} {Count}", _message, i);
    }
}
