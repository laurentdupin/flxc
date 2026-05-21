using System.Text.Json;

namespace Flx.LanguageServer.Protocol;

internal sealed class LspConnection
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly LspMessageReader _reader = new();
    private readonly LspMessageWriter _writer = new();

    public LspConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public Task<JsonDocument?> ReadAsync(CancellationToken cancellationToken = default)
    {
        return _reader.ReadAsync(_input, cancellationToken);
    }

    public Task WriteAsync(object message, CancellationToken cancellationToken = default)
    {
        return _writer.WriteAsync(_output, message, cancellationToken);
    }
}
