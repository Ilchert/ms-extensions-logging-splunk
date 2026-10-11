using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;

namespace Splunk.Extensions.Logging.IntegrationTests;

/// <summary>
/// Sends the same log calls through the Splunk logger (direct to HEC) and through the OpenTelemetry logger
/// (OTLP -> splunk-otel-collector -> HEC), then compares what Splunk indexed.
/// Requires: docker compose -f tests/integration/docker-compose.yml up -d
/// </summary>
public class SplunkVsOtelCollectorTests
{
    private const string HecUrl = "http://localhost:8088";
    private const string HecToken = "00000000-0000-0000-0000-000000000001";
    private const string ManagementUrl = "https://localhost:8089";
    private const string SplunkUser = "admin";
    private const string SplunkPassword = "Chang3d!Passw0rd";
    private const string MelSource = "mel-splunk";
    private const string CollectorSource = "otel-collector";

    // Splunk-assigned metadata that is not part of the event payload.
    private static readonly HashSet<string> s_metadataFields = new(StringComparer.Ordinal)
    {
        "index", "host", "source", "sourcetype", "splunk_server", "splunk_server_group", "linecount", "punct",
        "timestamp", "timeendpos", "timestartpos", "eventtype", "tag", "tag::eventtype", "date_hour", "date_mday",
        "date_minute", "date_month", "date_second", "date_wday", "date_year", "date_zone",
    };

    // Intentional differences: only the Splunk logger emits these ...
    private static readonly HashSet<string> s_melOnly = new(StringComparer.Ordinal) { "scope.name", "logrecord.event.id" };


    private static readonly HttpClient s_management = CreateManagementClient();

    private static HttpClient CreateManagementClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{SplunkUser}:{SplunkPassword}")));
        return client;
    }

    private static async Task<bool> IsSplunkAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync($"{HecUrl}/services/collector/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    private static void EmitScenarios(ILogger logger, string runId)
    {
        logger.LogInformation("Hello {Name} {Scenario} {RunId}", "world", "s1", runId);

        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex)
        {
            logger.LogWarning(new EventId(5, "Evt"), ex, "Failed {Id} {Scenario} {RunId}", 42, "s2", runId);
        }

        using (logger.BeginScope(new Dictionary<string, object> { ["ScopeKey"] = "scope-value" }))
            logger.LogError("In scope {Scenario} {RunId}", "s3", runId);

        logger.LogCritical("Plain {Scenario} {RunId}", "s4", runId);
        logger.LogInformation("Another {Scenario} {RunId}", "s5", runId);
    }

    private static async Task<List<Dictionary<string, string>>> SearchAsync(string runId, CancellationToken ct)
    {
        var search = $"search index=main earliest=-1h \"{runId}\" | fields *";
        using var response = await s_management.PostAsync($"{ManagementUrl}/services/search/jobs/export",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["search"] = search,
                ["output_mode"] = "json",
            }), ct);
        response.EnsureSuccessStatusCode();

        var results = new List<Dictionary<string, string>>();
        foreach (var line in (await response.Content.ReadAsStringAsync(ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("result", out var result)) continue;
            var row = new Dictionary<string, string>();
            foreach (var p in result.EnumerateObject())
                row[p.Name] = p.Value.ValueKind == JsonValueKind.Array
                    ? string.Join("|", p.Value.EnumerateArray().Select(v => v.GetString()))
                    : p.Value.ToString();
            results.Add(row);
        }
        return results;
    }

    [Fact(Explicit = true)]
    public async Task Splunk_logger_matches_otel_collector_output()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipUnless(await IsSplunkAvailableAsync(ct),
            "Splunk is not running. Start it with: docker compose -f tests/integration/docker-compose.yml up -d");

        var runId = Guid.NewGuid().ToString("N");
        var melErrors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        using (var melFactory = LoggerFactory.Create(b => b.AddSplunk(o =>
        {
            o.Endpoint = new Uri(HecUrl);
            o.Token = HecToken;
            o.Index = "main";
            o.Source = MelSource;
            o.FlushInterval = TimeSpan.FromMilliseconds(100);
            o.OnError = e => melErrors.Add(e);
        })))
            EmitScenarios(melFactory.CreateLogger("Integration.Category"), runId);

        using (var otelFactory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
            o.AddOtlpExporter(e =>
            {
                e.Endpoint = new Uri("http://localhost:4318/v1/logs");
                e.Protocol = OtlpExportProtocol.HttpProtobuf;
            });
        })))
            EmitScenarios(otelFactory.CreateLogger("Integration.Category"), runId);

        Assert.Empty(melErrors);

        List<Dictionary<string, string>> events = [];
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            events = await SearchAsync(runId, ct);
            if (events.Count(e => e["source"] == MelSource) >= 5 && events.Count(e => e["source"] == CollectorSource) >= 5) break;
            await Task.Delay(2000, ct);
        }

        var output = TestContext.Current.TestOutputHelper!;
        var mel = events.Where(e => e["source"] == MelSource).ToDictionary(e => e["Scenario"]);
        var otel = events.Where(e => e["source"] == CollectorSource).ToDictionary(e => e["Scenario"]);
        Assert.Equal(5, mel.Count);
        Assert.Equal(5, otel.Count);

        foreach (var scenario in mel.Keys.Order())
        {
            var a = Payload(mel[scenario]);
            var b = Payload(otel[scenario]);
            output.WriteLine($"=== {scenario} ===");
            var differences = new List<string>();
            foreach (var key in a.Keys.Union(b.Keys).Order())
            {
                a.TryGetValue(key, out var av);
                b.TryGetValue(key, out var bv);
                output.WriteLine($"{(av == bv ? "  " : "!!")} {key,-32} mel={av ?? "<missing>"} | otel={bv ?? "<missing>"}");
            }
        }
    }

    private static SortedDictionary<string, string> Payload(Dictionary<string, string> row)
    {
        var payload = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in row)
            if (!s_metadataFields.Contains(key) && !key.StartsWith('_'))
                payload[key] = value;
        payload["host"] = row.GetValueOrDefault("host") ?? "";
        payload["event(_raw)"] = row.GetValueOrDefault("_raw") ?? "";
        return payload;
    }
}
