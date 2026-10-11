using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Splunk.Extensions.Logging;

public static class SplunkLoggingBuilderExtensions
{
    /// <summary>Adds the Splunk HEC logger provider.</summary>
    public static ILoggingBuilder AddSplunk(this ILoggingBuilder builder, Action<SplunkLoggerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, SplunkLoggerProvider>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<SplunkLoggerOptions>, SplunkLoggerOptionsPostConfigure>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SplunkLoggerOptions>, SplunkLoggerOptionsValidator>());
        builder.Services.Configure(configure);
        return builder;
    }
}
