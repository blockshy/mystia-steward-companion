using System.Text;

namespace MystiaStewardCompanion.LocalApi;

internal static class HttpRequestReader
{
    public static string ReadHeader(Stream stream, int maxBytes)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        var buffer = new byte[maxBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var count = stream.Read(buffer, total, buffer.Length - total);
            if (count <= 0)
            {
                throw new HttpRequestReadException(400, "Bad Request", "incomplete request headers");
            }

            total += count;
            var headerEnd = FindHeaderEnd(buffer, total);
            if (headerEnd >= 0)
            {
                return Encoding.ASCII.GetString(buffer, 0, headerEnd);
            }
        }

        throw new HttpRequestReadException(431, "Request Header Fields Too Large", "request headers too large");
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
