namespace Splunk.Extensions.Logging.Tests;

public class SplunkLoggerProviderTests
{
    [Fact]
    public void Can_be_constructed()
    {
        var provider = new SplunkLoggerProvider();

        Assert.NotNull(provider);
    }
}
