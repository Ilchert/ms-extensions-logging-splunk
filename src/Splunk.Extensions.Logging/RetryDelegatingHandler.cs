namespace Splunk.Extensions.Logging;

/// <summary>Retries failed requests with exponential backoff.</summary>
internal sealed class RetryDelegatingHandler(int maxRetries, TimeSpan retryDelay) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var delay = retryDelay;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode || attempt >= maxRetries) return response;
                response.Dispose();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && attempt < maxRetries)
            {
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay += delay;
        }
    }
}
