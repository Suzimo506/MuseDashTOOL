using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace MdModManager.Helpers;

public class ProgressableStreamContent : HttpContent
{
    private readonly HttpContent _content;
    private readonly int _bufferSize;
    private readonly Action<long, long> _progressAction;

    public ProgressableStreamContent(HttpContent content, Action<long, long> progressAction)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _progressAction = progressAction;
        _bufferSize = 8192; // 8KB buffer

        foreach (var h in content.Headers)
        {
            this.Headers.Add(h.Key, h.Value);
        }
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var totalBytes = _content.Headers.ContentLength ?? -1L;
        var uploadedBytes = 0L;

        using var contentStream = await _content.ReadAsStreamAsync();
        var buffer = new byte[_bufferSize];
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
        {
            await stream.WriteAsync(buffer, 0, bytesRead);
            uploadedBytes += bytesRead;
            
            if (totalBytes > 0)
            {
                _progressAction?.Invoke(uploadedBytes, totalBytes);
            }
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _content.Headers.ContentLength ?? -1L;
        return length != -1L;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _content.Dispose();
        }
        base.Dispose(disposing);
    }
}
