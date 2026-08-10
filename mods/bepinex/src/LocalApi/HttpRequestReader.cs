using System.Text;

namespace MystiaStewardCompanion.LocalApi;

internal static class HttpRequestReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static HttpRequestData Read(Stream stream, int maxHeaderBytes, int maxBodyBytes)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (maxHeaderBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxHeaderBytes));
        if (maxBodyBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBodyBytes));

        var capacity = checked(maxHeaderBytes + maxBodyBytes + 1);
        var buffer = new byte[capacity];
        var total = 0;
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            if (total >= maxHeaderBytes)
            {
                throw new HttpRequestReadException(
                    431,
                    "Request Header Fields Too Large",
                    "request headers too large");
            }

            var count = stream.Read(buffer, total, Math.Min(buffer.Length - total, maxHeaderBytes - total));
            if (count <= 0)
            {
                throw new HttpRequestReadException(400, "Bad Request", "incomplete request headers");
            }

            total += count;
            headerEnd = FindHeaderEnd(buffer, total);
        }

        var header = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var contentLength = ReadContentLength(header);
        if (contentLength > maxBodyBytes)
        {
            throw new HttpRequestReadException(413, "Payload Too Large", "request body too large");
        }

        var requestLength = checked(headerEnd + contentLength);
        while (total < requestLength)
        {
            var count = stream.Read(buffer, total, requestLength - total);
            if (count <= 0)
            {
                throw new HttpRequestReadException(400, "Bad Request", "incomplete request body");
            }
            total += count;
        }

        if (total > requestLength)
        {
            throw new HttpRequestReadException(400, "Bad Request", "unexpected bytes after request body");
        }

        var body = contentLength == 0
            ? Array.Empty<byte>()
            : buffer.AsSpan(headerEnd, contentLength).ToArray();
        return new HttpRequestData(header, body);
    }

    public static string ReadRequiredJsonBody(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Body.Length == 0)
        {
            throw new HttpRequestReadException(400, "Bad Request", "JSON request body is required");
        }

        try
        {
            return StrictUtf8.GetString(request.Body);
        }
        catch (DecoderFallbackException)
        {
            throw new HttpRequestReadException(400, "Bad Request", "request body must be valid UTF-8 JSON");
        }
    }

    private static int ReadContentLength(string header)
    {
        string? contentLengthValue = null;
        foreach (var rawLine in header.Split(new[] { "\r\n" }, StringSplitOptions.None).Skip(1))
        {
            if (rawLine.Length == 0) break;
            var separator = rawLine.IndexOf(':');
            if (separator <= 0) continue;
            var name = rawLine[..separator].Trim();
            var value = rawLine[(separator + 1)..].Trim();
            if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestReadException(400, "Bad Request", "transfer encoding is not supported");
            }
            if (!string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (contentLengthValue != null)
            {
                throw new HttpRequestReadException(400, "Bad Request", "duplicate content length");
            }
            contentLengthValue = value;
        }

        if (contentLengthValue == null) return 0;
        if (contentLengthValue.Length == 0
            || contentLengthValue.Any(character => character is < '0' or > '9')
            || !int.TryParse(contentLengthValue, out var contentLength))
        {
            throw new HttpRequestReadException(400, "Bad Request", "invalid content length");
        }
        return contentLength;
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var index = 3; index < length; index++)
        {
            if (buffer[index - 3] == '\r'
                && buffer[index - 2] == '\n'
                && buffer[index - 1] == '\r'
                && buffer[index] == '\n')
            {
                return index + 1;
            }
        }

        return -1;
    }
}

internal sealed class HttpRequestData
{
    public HttpRequestData(string header, byte[] body)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Body = body ?? throw new ArgumentNullException(nameof(body));
    }

    public string Header { get; }
    public byte[] Body { get; }
}

internal sealed class HttpRequestReadException : Exception
{
    public HttpRequestReadException(int statusCode, string reason, string error)
        : base(error)
    {
        StatusCode = statusCode;
        Reason = reason;
        Error = error;
    }

    public int StatusCode { get; }
    public string Reason { get; }
    public string Error { get; }
}
