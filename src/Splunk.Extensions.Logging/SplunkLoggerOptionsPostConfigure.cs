using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Splunk.Extensions.Logging;

/// <summary>Expands the base HEC URL to the event collector endpoint and configures the HTTP client.</summary>
internal sealed class SplunkLoggerOptionsPostConfigure : IPostConfigureOptions<SplunkLoggerOptions>
{
    public void PostConfigure(string? name, SplunkLoggerOptions options)
    {
        var endpoint = options.Endpoint;
        if (endpoint is not null && !endpoint.AbsolutePath.Contains("/services/collector", StringComparison.OrdinalIgnoreCase))
            options.Endpoint = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/services/collector/event");

        options.HttpClient ??= new HttpClient();
        if (options.HttpClient.DefaultRequestHeaders.Authorization is null && !string.IsNullOrWhiteSpace(options.Token))
            options.HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Splunk", options.Token);
    }
}
