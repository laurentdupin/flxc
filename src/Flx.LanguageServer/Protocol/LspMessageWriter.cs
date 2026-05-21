using System.Text;
using System.Text.Json;

namespace Flx.LanguageServer.Protocol;

internal sealed class LspMessageWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task WriteAsync(Stream output, object message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await output.WriteAsync(header, cancellationToken);
            await output.WriteAsync(body, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
