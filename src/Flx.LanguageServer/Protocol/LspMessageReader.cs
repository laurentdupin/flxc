using System.Text;
using System.Text.Json;

namespace Flx.LanguageServer.Protocol;

internal sealed class LspMessageReader
{
    public async Task<JsonDocument?> ReadAsync(Stream input, CancellationToken cancellationToken = default)
    {
        var headerBytes = new List<byte>();
        var buffer = new byte[1];

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
                return headerBytes.Count == 0 ? null : throw new EndOfStreamException("Unexpected end of stream while reading LSP headers.");

            headerBytes.Add(buffer[0]);
            if (EndsWithHeaderTerminator(headerBytes))
                break;
        }

        var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
        var contentLength = ParseContentLength(headers);
        if (contentLength < 0)
            throw new InvalidDataException("LSP message is missing Content-Length header.");

        var body = new byte[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var read = await input.ReadAsync(body.AsMemory(offset, contentLength - offset), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of stream while reading LSP body.");

            offset += read;
        }

        return JsonDocument.Parse(body);
    }

    private static bool EndsWithHeaderTerminator(List<byte> bytes)
    {
        return bytes.Count >= 4 &&
               bytes[^4] == (byte)'\r' &&
               bytes[^3] == (byte)'\n' &&
               bytes[^2] == (byte)'\r' &&
               bytes[^1] == (byte)'\n';
    }

    private static int ParseContentLength(string headers)
    {
        foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex < 0)
                continue;

            var name = line[..colonIndex].Trim();
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line[(colonIndex + 1)..].Trim();
            return int.TryParse(value, out var length) ? length : -1;
        }

        return -1;
    }
}
