using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Splunk.Extensions.Logging.Tests;

/// <summary>Stress and performance tests for the pooled buffer pipeline. Explicit: run with <c>dotnet run -- -explicit only</c>.</summary>
public class PooledBufferStressTests(ITestOutputHelper output)
{
    /// <summary>Pool that tracks every rented array so leaks and double/foreign returns are detectable.</summary>
    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        private readonly ConcurrentDictionary<byte[], byte> _outstanding = new(ReferenceEqualityComparer.Instance);
        private long _rented;
        private long _badReturns;

        public int Outstanding => _outstanding.Count;
        public long Rented => Interlocked.Read(ref _rented);
        public long BadReturns => Interlocked.Read(ref _badReturns);

        public override byte[] Rent(int minimumLength)
        {
            var array = Shared.Rent(minimumLength);
            _outstanding[array] = 0;
            Interlocked.Increment(ref _rented);
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            if (!_outstanding.TryRemove(array, out _))
            {
                Interlocked.Increment(ref _badReturns);
                return;
            }
            Shared.Return(array, clearArray);
        }
    }

    private sealed class SinkHandler(Func<string, Task>? onBody = null, TimeSpan? delay = null, bool fail = false) : HttpMessageHandler
    {
        public int Requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            if (delay is { } d) await Task.Delay(d, ct);
            if (fail) throw new HttpRequestException("boom");
            if (onBody is null) await request.Content!.CopyToAsync(Stream.Null, ct);
            else await onBody(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    private static SplunkLoggerProvider Build(SplunkLoggerOptions options, HttpMessageHandler handler)
    {
        options.Endpoint = new Uri("https://splunk:8088");
        options.Token = "tok";
        options.HttpClient = new HttpClient(handler);
        options.ShutdownTimeout = TimeSpan.FromSeconds(60);
        new SplunkLoggerOptionsPostConfigure().PostConfigure(null, options);
        return new(Options.Create(options));
    }

    // Deterministic per-id payload: sizes from 0 to ~6 KB so events span several 2 KB pool segments.
    private static string MessageFor(int id) => $"{id}:" + new string((char)('a' + id % 26), id * 37 % 6000);

    private static List<JsonElement> ParseEvents(string body)
    {
        var events = new List<JsonElement>();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(body), new JsonReaderOptions { AllowMultipleValues = true });
        while (reader.Read() && JsonDocument.TryParseValue(ref reader, out var doc))
            events.Add(doc.RootElement.Clone());
        return events;
    }

    [Fact(Explicit = true)]
    public void Concurrent_producers_deliver_every_event_intact_and_return_all_buffers()
    {
        const int threads = 8, perThread = 5_000, total = threads * perThread;
        var pool = new TrackingArrayPool();
        var seen = new int[total];
        var corrupt = 0;
        var received = 0;

        var handler = new SinkHandler(body =>
        {
            foreach (var e in ParseEvents(body))
            {
                var id = e.GetProperty("fields").GetProperty("Id").GetInt32();
                if (e.GetProperty("event").GetString() != MessageFor(id) + id) Interlocked.Increment(ref corrupt);
                Interlocked.Increment(ref seen[id]);
                Interlocked.Increment(ref received);
            }
            return Task.CompletedTask;
        });

        var errors = new ConcurrentBag<Exception>();
        var provider = Build(new SplunkLoggerOptions
        {
            ArrayPool = pool,
            QueueCapacity = total * 2,
            BatchSize = 250,
            FlushInterval = TimeSpan.FromMilliseconds(20),
            OnError = errors.Add,
        }, handler);

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            var logger = provider.CreateLogger($"Cat{t}");
            for (var i = 0; i < perThread; i++)
            {
                var id = t * perThread + i;
                logger.Log(LogLevel.Information, new EventId(id % 100), "{Message}{Id}", MessageFor(id), id);
            }
        });
        provider.Dispose();

        Assert.Empty(errors);
        Assert.Equal(0, corrupt);
        Assert.Equal(total, received);
        Assert.All(seen, count => Assert.Equal(1, count));
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
        output.WriteLine($"events={total} requests={handler.Requests} rented arrays={pool.Rented}");
    }

    [Fact(Explicit = true)]
    public void Dropped_events_release_their_buffers()
    {
        const int total = 20_000;
        var pool = new TrackingArrayPool();
        var dropped = 0;
        var received = 0;

        var handler = new SinkHandler(body =>
        {
            Interlocked.Add(ref received, ParseEvents(body).Count);
            return Task.CompletedTask;
        }, delay: TimeSpan.FromMilliseconds(2));

        var provider = Build(new SplunkLoggerOptions
        {
            ArrayPool = pool,
            QueueCapacity = 64,
            BatchSize = 16,
            OnDropped = () => Interlocked.Increment(ref dropped),
        }, handler);

        var logger = provider.CreateLogger("Cat");
        for (var i = 0; i < total; i++) logger.LogInformation("{Message}{Id}", MessageFor(i), i);
        provider.Dispose();

        Assert.True(dropped > 0, "the slow sink should have caused drops");
        Assert.Equal(total, received + dropped);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
        output.WriteLine($"sent={total} received={received} dropped={dropped}");
    }

    [Fact(Explicit = true)]
    public void Failed_sends_still_release_their_buffers()
    {
        var pool = new TrackingArrayPool();
        var errors = 0;
        var provider = Build(new SplunkLoggerOptions
        {
            ArrayPool = pool,
            BatchSize = 10,
            OnError = _ => Interlocked.Increment(ref errors),
        }, new SinkHandler(fail: true));

        var logger = provider.CreateLogger("Cat");
        for (var i = 0; i < 1_000; i++) logger.LogError(new InvalidOperationException("x"), "{Message}", MessageFor(i));
        provider.Dispose();

        Assert.True(errors > 0);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
    }

    [Fact(Explicit = true)]
    public void Serialization_errors_return_their_buffers()
    {
        var pool = new TrackingArrayPool();
        var errors = 0;
        var provider = Build(new SplunkLoggerOptions
        {
            ArrayPool = pool,
            SerializeComplexValues = true,
            OnError = _ => Interlocked.Increment(ref errors),
        }, new SinkHandler());

        var logger = provider.CreateLogger("Cat");
        for (var i = 0; i < 100; i++) logger.LogInformation("{Big} {Bad}", new string('x', 5_000), new Throwing());
        provider.Dispose();

        Assert.Equal(100, errors);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
    }

    private sealed class Throwing
    {
        public int Value => throw new InvalidOperationException("boom");
    }
    [Fact(Explicit = true)]
    public async Task Dispose_while_producers_are_running_does_not_leak_or_throw()
    {
        var pool = new TrackingArrayPool();
        var provider = Build(new SplunkLoggerOptions { ArrayPool = pool, BatchSize = 50 }, new SinkHandler());
        using var stop = new CancellationTokenSource();

        var producers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            var logger = provider.CreateLogger($"Cat{t}");
            for (var i = 0; !stop.IsCancellationRequested; i++) logger.LogInformation("{Message}", MessageFor(i));
        })).ToArray();

        await Task.Delay(200, TestContext.Current.CancellationToken);
        provider.Dispose();
        stop.Cancel();
        await Task.WhenAll(producers);

        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
    }

    [Fact(Explicit = true)]
    public void Throughput_and_allocations_stay_within_budget()
    {
        const int warmup = 20_000, total = 200_000;
        var pool = new TrackingArrayPool();
        var provider = Build(new SplunkLoggerOptions
        {
            ArrayPool = pool,
            QueueCapacity = total + warmup,
            BatchSize = 1_000,
        }, new SinkHandler());
        var logger = provider.CreateLogger("Perf");

        for (var i = 0; i < warmup; i++) logger.LogInformation("Hello {Name} {Count}", "world", i);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < total; i++) logger.LogInformation("Hello {Name} {Count}", "world", i);
        sw.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        provider.Dispose();

        var perSecond = total / sw.Elapsed.TotalSeconds;
        var bytesPerEvent = allocated / (double)total;
        output.WriteLine($"{perSecond:N0} events/s on the logging thread, {bytesPerEvent:N0} B allocated/event");

        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
        // Loose budgets: catch regressions such as per-event buffer allocation or lost pooling, not machine noise.
        Assert.True(perSecond > 50_000, $"throughput too low: {perSecond:N0}/s");
        Assert.True(bytesPerEvent < 2_048, $"too many allocations: {bytesPerEvent:N0} B/event");
    }

    [Fact(Explicit = true)]
    public void Large_multi_segment_events_release_all_buffers()
    {
        const int total = 20_000;
        var pool = new TrackingArrayPool();
        var provider = Build(new SplunkLoggerOptions { ArrayPool = pool, QueueCapacity = total, BatchSize = 100 }, new SinkHandler());
        var logger = provider.CreateLogger("Big");
        var message = new string('x', 16 * 1024);

        for (var i = 0; i < total; i++) logger.LogInformation("{Message}", message);
        provider.Dispose();

        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.BadReturns);
        Assert.True(pool.Rented >= total, "each event should rent at least one array");
    }
}
