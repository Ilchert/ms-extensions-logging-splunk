using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Splunk.Extensions.Logging.Tests;

public class ValueFormattingTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    private sealed class Customer(string name)
    {
        public override string ToString() => $"Customer({name})";
    }

    private sealed record Point(int X, int Y);

    private sealed class Throwing
    {
        public int Value => throw new InvalidOperationException("boom");
    }

    private enum Color { Red = 1, Green = 2 }

    private static SplunkLoggerOptions CreateOptions(CaptureHandler handler, Action<SplunkLoggerOptions>? configure)
    {
        var options = new SplunkLoggerOptions
        {
            Endpoint = new Uri("https://splunk:8088"),
            Token = "tok",
            HttpClient = new HttpClient(handler),
        };
        configure?.Invoke(options);
        new SplunkLoggerOptionsPostConfigure().PostConfigure(null, options);
        return options;
    }

    private static JsonElement LogAndGetFields(Action<ILogger> log, Action<SplunkLoggerOptions>? configure = null)
    {
        var handler = new CaptureHandler();
        var options = CreateOptions(handler, configure);
        var provider = new SplunkLoggerProvider(Options.Create(options));
        log(provider.CreateLogger("Cat"));
        provider.Dispose();

        using var doc = JsonDocument.Parse(handler.Body);
        return doc.RootElement.GetProperty("fields").Clone();
    }

    [Fact]
    public void Primitive_values_keep_their_json_types()
    {
        var f = LogAndGetFields(l => l.LogInformation(
            "{Text} {Int} {Long} {Double} {Decimal} {Bool} {Null}", "a\"b", 1, 2L, 1.5, 3.25m, true, null));

        Assert.Equal("a\"b", f.GetProperty("Text").GetString());
        Assert.Equal(1, f.GetProperty("Int").GetInt32());
        Assert.Equal(2L, f.GetProperty("Long").GetInt64());
        Assert.Equal(1.5, f.GetProperty("Double").GetDouble());
        Assert.Equal(3.25m, f.GetProperty("Decimal").GetDecimal());
        Assert.True(f.GetProperty("Bool").GetBoolean());
        Assert.Equal(JsonValueKind.Null, f.GetProperty("Null").ValueKind);
    }

    [Fact]
    public void Non_finite_numbers_are_written_as_strings()
    {
        var f = LogAndGetFields(l => l.LogInformation("{Nan} {Inf}", double.NaN, float.PositiveInfinity));

        Assert.Equal(JsonValueKind.String, f.GetProperty("Nan").ValueKind);
        Assert.Equal(JsonValueKind.String, f.GetProperty("Inf").ValueKind);
    }

    [Fact]
    public void Guid_dates_and_enums_are_formatted_as_invariant_strings()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var offset = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(1));

        var f = LogAndGetFields(l => l.LogInformation("{Id} {When} {Offset} {Span} {Color}",
            id, when, offset, TimeSpan.FromSeconds(90), Color.Green));

        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", f.GetProperty("Id").GetString());
        Assert.Equal(when.ToString(System.Globalization.CultureInfo.InvariantCulture), f.GetProperty("When").GetString());
        Assert.Equal(offset.ToString(System.Globalization.CultureInfo.InvariantCulture), f.GetProperty("Offset").GetString());
        Assert.Equal("00:01:30", f.GetProperty("Span").GetString());
        Assert.Equal("Green", f.GetProperty("Color").GetString());
    }

    [Fact]
    public void Custom_types_use_ToString()
    {
        var f = LogAndGetFields(l => l.LogInformation("{Customer} {Point}", new Customer("Ann"), new Point(1, 2)));

        Assert.Equal("Customer(Ann)", f.GetProperty("Customer").GetString());
        Assert.Equal(new Point(1, 2).ToString(), f.GetProperty("Point").GetString());
    }

    [Fact]
    public void Collections_and_custom_types_are_written_as_text_by_default()
    {
        var f = LogAndGetFields(l => l.LogInformation("{Ints} {Customer}", new List<int> { 1, 2 }, new Customer("Ann")));

        Assert.Equal(JsonValueKind.String, f.GetProperty("Ints").ValueKind);
        Assert.Equal("Customer(Ann)", f.GetProperty("Customer").GetString());
    }

    [Fact]
    public void Complex_values_are_serialized_as_json_when_enabled()
    {
        var f = LogAndGetFields(l => l.LogInformation("{Ints} {Names} {Map} {Point} {Id} {Nested}",
            new List<int> { 1, 2, 3 }, new[] { "a", "b" }, new Dictionary<string, int> { ["k"] = 1 },
            new Point(1, 2), Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), new { Items = new[] { 1.5, 2.5 } }),
            o => o.SerializeComplexValues = true);

        Assert.Equal([1, 2, 3], f.GetProperty("Ints").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(["a", "b"], f.GetProperty("Names").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, f.GetProperty("Map").GetProperty("k").GetInt32());
        Assert.Equal(1, f.GetProperty("Point").GetProperty("X").GetInt32());
        Assert.Equal(2, f.GetProperty("Point").GetProperty("Y").GetInt32());
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", f.GetProperty("Id").GetString());
        Assert.Equal([1.5, 2.5], f.GetProperty("Nested").GetProperty("Items").EnumerateArray().Select(e => e.GetDouble()));
    }

    [Fact]
    public void Non_finite_numbers_inside_complex_values_use_named_literals()
    {
        var f = LogAndGetFields(l => l.LogInformation("{Values}", new[] { double.NaN, double.PositiveInfinity, 1.0 }),
            o => o.SerializeComplexValues = true);

        Assert.Equal(["NaN", "Infinity", null], f.GetProperty("Values").EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null));
    }

    [Fact]
    public void Custom_json_serializer_options_are_used()
    {
        var f = LogAndGetFields(l => l.LogInformation("{Point}", new Point(1, 2)), o =>
        {
            o.SerializeComplexValues = true;
            o.JsonSerializerOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        });

        Assert.Equal(1, f.GetProperty("Point").GetProperty("x").GetInt32());
    }

    [Fact]
    public void Scope_values_are_serialized_when_enabled()
    {
        var handler = new CaptureHandler();
        var options = CreateOptions(handler, o => o.SerializeComplexValues = true);
        var provider = new SplunkLoggerProvider(Options.Create(options));
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        var logger = provider.CreateLogger("Cat");
        using (logger.BeginScope(new Dictionary<string, object?> { ["Tags"] = new[] { "x", "y" } }))
            logger.LogInformation("hi");
        provider.Dispose();

        using var doc = JsonDocument.Parse(handler.Body);
        Assert.Equal(["x", "y"], doc.RootElement.GetProperty("fields").GetProperty("Tags").EnumerateArray().Select(e => e.GetString()));
    }
    [Fact]
    public void All_values_use_the_serializer_when_enabled()
    {
        var f = LogAndGetFields(l => l.LogInformation("{Nan} {Day} {Nothing} {Text} {Number}",
            double.NaN, DayOfWeek.Tuesday, null, "x", 5),
            o => o.SerializeComplexValues = true);

        Assert.Equal("NaN", f.GetProperty("Nan").GetString());
        Assert.Equal(2, f.GetProperty("Day").GetInt32());
        Assert.Equal(JsonValueKind.Null, f.GetProperty("Nothing").ValueKind);
        Assert.Equal("x", f.GetProperty("Text").GetString());
        Assert.Equal(5, f.GetProperty("Number").GetInt32());
    }

    [Fact]
    public void Serialization_errors_are_reported_and_the_event_is_dropped()
    {
        var handler = new CaptureHandler();
        Exception? error = null;
        var options = CreateOptions(handler, o =>
        {
            o.SerializeComplexValues = true;
            o.OnError = ex => error = ex;
        });
        var provider = new SplunkLoggerProvider(Options.Create(options));
        var logger = provider.CreateLogger("Cat");

        logger.LogInformation("{Bad}", new Throwing());
        logger.LogInformation("good");
        provider.Dispose();

        Assert.NotNull(error);
        using var doc = JsonDocument.Parse(handler.Body);
        Assert.Equal("good", doc.RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public void Special_characters_are_not_over_escaped()
    {
        var f = LogAndGetFields(l => l.LogInformation("{Text}", "héllo <b>wörld</b> & 日本語 \n\t\u0001"));

        Assert.Equal("héllo <b>wörld</b> & 日本語 \n\t\u0001", f.GetProperty("Text").GetString());
    }
}
