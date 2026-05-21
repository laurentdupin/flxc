using System.Text;
using System.Text.Json;
using Flx.LanguageServer.Protocol;

var arguments = args.ToList();
if (arguments.Count == 0)
{
    return await RunStdioAsync(logPath: null);
}

if (arguments.Contains("--smoke", StringComparer.Ordinal))
{
    var index = arguments.IndexOf("--smoke");
    if (index + 1 >= arguments.Count)
    {
        Console.Error.WriteLine("usage: flx-lsp --smoke <file.flx>");
        return 2;
    }

    return await RunSmokeAsync(arguments[index + 1], null);
}

if (arguments.Contains("--smoke-definition", StringComparer.Ordinal))
{
    var index = arguments.IndexOf("--smoke-definition");
    if (index + 3 >= arguments.Count ||
        !int.TryParse(arguments[index + 2], out var line) ||
        !int.TryParse(arguments[index + 3], out var character))
    {
        Console.Error.WriteLine("usage: flx-lsp --smoke-definition <file.flx> <zero-based-line> <zero-based-character>");
        return 2;
    }

    return await RunSmokeAsync(arguments[index + 1], new SmokePosition("textDocument/definition", line, character));
}

if (arguments.Contains("--smoke-hover", StringComparer.Ordinal))
{
    var index = arguments.IndexOf("--smoke-hover");
    if (index + 3 >= arguments.Count ||
        !int.TryParse(arguments[index + 2], out var line) ||
        !int.TryParse(arguments[index + 3], out var character))
    {
        Console.Error.WriteLine("usage: flx-lsp --smoke-hover <file.flx> <zero-based-line> <zero-based-character>");
        return 2;
    }

    return await RunSmokeAsync(arguments[index + 1], new SmokePosition("textDocument/hover", line, character));
}

if (!arguments.Contains("--stdio", StringComparer.Ordinal))
{
    Console.Error.WriteLine("usage: flx-lsp --stdio [--log <path>]");
    Console.Error.WriteLine("       flx-lsp --smoke <file.flx>");
    Console.Error.WriteLine("       flx-lsp --smoke-definition <file.flx> <zero-based-line> <zero-based-character>");
    Console.Error.WriteLine("       flx-lsp --smoke-hover <file.flx> <zero-based-line> <zero-based-character>");
    return 2;
}

return await RunStdioAsync(ReadOption(arguments, "--log"));

static async Task<int> RunStdioAsync(string? logPath)
{
    using var log = OpenLog(logPath);
    var server = new LspServer(log);
    await server.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
    return 0;
}

static async Task<int> RunSmokeAsync(string filePath, SmokePosition? positionRequest)
{
    var fullPath = Path.GetFullPath(filePath);
    var uri = new Uri(fullPath).AbsoluteUri;
    var text = await File.ReadAllTextAsync(fullPath);

    await using var input = new MemoryStream();
    await WriteFrameAsync(input, new
    {
        jsonrpc = "2.0",
        id = 1,
        method = "initialize",
        @params = new { }
    });
    await WriteFrameAsync(input, new
    {
        jsonrpc = "2.0",
        method = "textDocument/didOpen",
        @params = new
        {
            textDocument = new
            {
                uri,
                languageId = "flx",
                version = 1,
                text
            }
        }
    });
    await WriteFrameAsync(input, new
    {
        jsonrpc = "2.0",
        id = 2,
        method = "textDocument/documentSymbol",
        @params = new
        {
            textDocument = new
            {
                uri
            }
        }
    });

    var shutdownId = 3;
    if (positionRequest is not null)
    {
        await WriteFrameAsync(input, new
        {
            jsonrpc = "2.0",
            id = 3,
            method = positionRequest.Method,
            @params = new
            {
                textDocument = new
                {
                    uri
                },
                position = new
                {
                    line = positionRequest.Line,
                    character = positionRequest.Character
                }
            }
        });
        shutdownId = 4;
    }

    await WriteFrameAsync(input, new
    {
        jsonrpc = "2.0",
        id = shutdownId,
        method = "shutdown",
        @params = new { }
    });
    await WriteFrameAsync(input, new
    {
        jsonrpc = "2.0",
        method = "exit"
    });

    input.Position = 0;
    await using var output = new MemoryStream();
    var server = new LspServer();
    await server.RunAsync(input, output);

    Console.Write(Encoding.UTF8.GetString(output.ToArray()));
    return 0;
}

static string? ReadOption(IReadOnlyList<string> arguments, string option)
{
    for (var i = 0; i < arguments.Count; i++)
    {
        if (!arguments[i].Equals(option, StringComparison.Ordinal))
            continue;

        return i + 1 < arguments.Count ? arguments[i + 1] : null;
    }

    return null;
}

static TextWriter OpenLog(string? logPath)
{
    if (string.IsNullOrWhiteSpace(logPath))
        return TextWriter.Null;

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
    return new StreamWriter(File.Open(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
    {
        AutoFlush = true
    };
}

static async Task WriteFrameAsync(Stream stream, object message)
{
    var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
    var body = Encoding.UTF8.GetBytes(json);
    var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
    await stream.WriteAsync(header);
    await stream.WriteAsync(body);
}

internal sealed record SmokePosition(string Method, int Line, int Character);
