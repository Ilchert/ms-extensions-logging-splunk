using System.Net;
using System.Net.Http.Headers;

namespace Splunk.Extensions.Logging;

/// <summary>HTTP content that streams a list of buffers without concatenating them.</summary>
internal sealed class BufferListContent : HttpContent
{
    private readonly List<byte[]> _buffers;
    private readonly long _length;

    public BufferListContent(List<byte[]> buffers)
    {
        _buffers = buffers;
        foreach (var b in buffers) _length += b.Length;
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        foreach (var b in _buffers)
            await stream.WriteAsync(b, cancellationToken).ConfigureAwait(false);
    }

    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        foreach (var b in _buffers)
            stream.Write(b, 0, b.Length);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}
