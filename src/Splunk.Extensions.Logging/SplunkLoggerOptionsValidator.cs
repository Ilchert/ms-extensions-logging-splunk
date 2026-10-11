using Microsoft.Extensions.Options;

namespace Splunk.Extensions.Logging;

internal sealed class SplunkLoggerOptionsValidator : IValidateOptions<SplunkLoggerOptions>
{
    public ValidateOptionsResult Validate(string? name, SplunkLoggerOptions options)
    {
        List<string>? failures = null;

        if (options.Endpoint is null)
            (failures ??= []).Add("SplunkLoggerOptions.Endpoint is required.");
        if (string.IsNullOrWhiteSpace(options.Token))
            (failures ??= []).Add("SplunkLoggerOptions.Token is required.");
        if (options.BatchSize < 1)
            (failures ??= []).Add("SplunkLoggerOptions.BatchSize must be at least 1.");
        if (options.QueueCapacity < 1)
            (failures ??= []).Add("SplunkLoggerOptions.QueueCapacity must be at least 1.");
        if (options.FlushInterval <= TimeSpan.Zero)
            (failures ??= []).Add("SplunkLoggerOptions.FlushInterval must be greater than zero.");

        return failures is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
