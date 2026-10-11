using Microsoft.Extensions.Options;

namespace Splunk.Extensions.Logging;

/// <summary>Expands the base HEC URL to the full event collector endpoint.</summary>
internal sealed class SplunkLoggerOptionsPostConfigure : IPostConfigureOptions<SplunkLoggerOptions>
{
    public void PostConfigure(string? name, SplunkLoggerOptions options)
    {
        var endpoint = options.Endpoint;
        if (endpoint is null || endpoint.AbsolutePath.Contains("/services/collector", StringComparison.OrdinalIgnoreCase))
            return;
        options.Endpoint = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/services/collector/event");
    }
}
