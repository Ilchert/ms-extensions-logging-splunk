using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Splunk.Extensions.Logging.Tests;

public class ValueFormattingTests(ITestOutputHelper output)
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

    private enum Color { Red = 1, Green = 2 }

    private static JsonElement LogAndGetFields(Action<ILogger> log)
    {
        var handler = new CaptureHandler();
        var options = new SplunkLoggerOptions
        {
            Endpoint = new Uri("https://splunk:8088"),
            Token = "tok",
            HttpClient = new HttpClient(handler),
        };
        new SplunkLoggerOptionsPostConfigure().PostConfigure(null, options);
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
    public void Collections_are_written_as_a_single_field_and_the_message_lists_items()
    {
        var handler = new CaptureHandler();
        var options = new SplunkLoggerOptions
        {
            Endpoint = new Uri("https://splunk:8088"),
            Token = "tok",
            HttpClient = new HttpClient(handler),
        };
        new SplunkLoggerOptionsPostConfigure().PostConfigure(null, options);
        var provider = new SplunkLoggerProvider(Options.Create(options));
        provider.CreateLogger("Cat").LogInformation("{Ints} {Names} {Map}",
            new List<int> { 1, 2, 3 }, new[] { "a", "b" }, new Dictionary<string, int> { ["k"] = 1 });
        provider.Dispose();

        output.WriteLine(handler.Body);
        using var doc = JsonDocument.Parse(handler.Body);
        var root = doc.RootElement;
        Assert.Equal("1, 2, 3 a, b [k, 1]", root.GetProperty("event").GetString());

        var f = root.GetProperty("fields");
        Assert.Equal(JsonValueKind.String, f.GetProperty("Ints").ValueKind);
        Assert.Equal(JsonValueKind.String, f.GetProperty("Names").ValueKind);
        Assert.Equal(JsonValueKind.String, f.GetProperty("Map").ValueKind);
    }

    [Fact]
    public void Special_characters_are_not_over_escaped()
    {
        var f = LogAndGetFields(l => l.LogInformation("{Text}", "héllo <b>wörld</b> & 日本語 \n\t\u0001"));

        Assert.Equal("héllo <b>wörld</b> & 日本語 \n\t\u0001", f.GetProperty("Text").GetString());
    }
}
