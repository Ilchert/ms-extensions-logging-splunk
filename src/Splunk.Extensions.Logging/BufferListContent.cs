using System.Net;
using System.Net.Http.Headers;

namespace Splunk.Extensions.Logging;

/// <summary>HTTP content that streams a list of pooled buffers without concatenating them.</summary>
internal sealed class BufferListContent : HttpContent
{
    private readonly List<PooledBufferWriter> _entries;
    private readonly long _length;

    public BufferListContent(List<PooledBufferWriter> entries)
    {
        _entries = entries;
        foreach (var e in entries) _length += e.Length;
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        foreach (var e in _entries)
            await e.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        foreach (var e in _entries)
            e.CopyTo(stream);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}