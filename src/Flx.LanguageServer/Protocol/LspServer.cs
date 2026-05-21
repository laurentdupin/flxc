using System.Text.Json;
using Flx.LanguageServices;

namespace Flx.LanguageServer.Protocol;

internal sealed class LspServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, OpenDocumentState> _openDocumentsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextWriter _log;

    public LspServer(TextWriter? log = null)
    {
        _log = log ?? TextWriter.Null;
    }

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        var connection = new LspConnection(input, output);
        await LogAsync("server start");

        while (!cancellationToken.IsCancellationRequested)
        {
            JsonDocument? document;
            try
            {
                document = await connection.ReadAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                await LogAsync("read error: " + ex);
                break;
            }

            if (document is null)
                break;

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("method", out var methodElement) ||
                    methodElement.ValueKind != JsonValueKind.String)
                {
                    await SendErrorAsync(connection, TryGetId(root), LspErrorCodes.InvalidRequest, "Invalid request.", cancellationToken);
                    continue;
                }

                var method = methodElement.GetString() ?? "";
                var id = TryGetId(root);

                try
                {
                    var shouldExit = await HandleMessageAsync(connection, root, method, id, cancellationToken);
                    if (shouldExit)
                        return;
                }
                catch (Exception ex)
                {
                    await LogAsync($"{method} error: {ex}");
                    if (id is not null)
                        await SendErrorAsync(connection, id.Value, LspErrorCodes.InternalError, ex.Message, cancellationToken);
                }
            }
        }
    }

    private async Task<bool> HandleMessageAsync(
        LspConnection connection,
        JsonElement root,
        string method,
        JsonElement? id,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                await LogAsync("initialize");
                if (id is not null)
                    await SendResultAsync(connection, id.Value, InitializeResult(), cancellationToken);
                return false;

            case "initialized":
                await LogAsync("initialized");
                return false;

            case "shutdown":
                await LogAsync("shutdown");
                if (id is not null)
                    await SendResultAsync(connection, id.Value, null, cancellationToken);
                return false;

            case "exit":
                await LogAsync("exit");
                return true;

            case "textDocument/didOpen":
                await HandleDidOpenAsync(connection, ReadParams<DidOpenTextDocumentParams>(root), cancellationToken);
                return false;

            case "textDocument/didChange":
                await HandleDidChangeAsync(connection, ReadParams<DidChangeTextDocumentParams>(root), cancellationToken);
                return false;

            case "textDocument/didClose":
                await HandleDidCloseAsync(connection, ReadParams<DidCloseTextDocumentParams>(root), cancellationToken);
                return false;

            case "textDocument/documentSymbol":
                if (id is not null)
                    await HandleDocumentSymbolAsync(connection, id.Value, ReadParams<DocumentSymbolParams>(root), cancellationToken);
                return false;

            default:
                if (id is not null)
                    await SendErrorAsync(connection, id.Value, LspErrorCodes.MethodNotFound, "Method not found", cancellationToken);
                return false;
        }
    }

    private async Task HandleDidOpenAsync(
        LspConnection connection,
        DidOpenTextDocumentParams parameters,
        CancellationToken cancellationToken)
    {
        var path = UriToPath(parameters.TextDocument.Uri);
        _openDocumentsByPath[path] = new OpenDocumentState(parameters.TextDocument.Uri, path, parameters.TextDocument.Text);
        await LogAsync("didOpen " + path);
        await AnalyzeAndPublishDiagnosticsAsync(connection, path, cancellationToken);
    }

    private async Task HandleDidChangeAsync(
        LspConnection connection,
        DidChangeTextDocumentParams parameters,
        CancellationToken cancellationToken)
    {
        var path = UriToPath(parameters.TextDocument.Uri);
        var text = parameters.ContentChanges.LastOrDefault()?.Text;
        if (text is null)
            return;

        var uri = _openDocumentsByPath.TryGetValue(path, out var existing) ? existing.Uri : parameters.TextDocument.Uri;
        _openDocumentsByPath[path] = new OpenDocumentState(uri, path, text);
        await LogAsync("didChange " + path);
        await AnalyzeAndPublishDiagnosticsAsync(connection, path, cancellationToken);
    }

    private async Task HandleDidCloseAsync(
        LspConnection connection,
        DidCloseTextDocumentParams parameters,
        CancellationToken cancellationToken)
    {
        var path = UriToPath(parameters.TextDocument.Uri);
        _openDocumentsByPath.Remove(path);
        await LogAsync("didClose " + path);
        await PublishDiagnosticsAsync(connection, parameters.TextDocument.Uri, [], cancellationToken);
    }

    private async Task HandleDocumentSymbolAsync(
        LspConnection connection,
        JsonElement id,
        DocumentSymbolParams parameters,
        CancellationToken cancellationToken)
    {
        var path = UriToPath(parameters.TextDocument.Uri);
        var snapshot = Analyze(path);
        var symbols = snapshot.DocumentSymbols
            .Where(symbol => PathsEqual(symbol.Path, path))
            .OrderBy(symbol => symbol.Range.Start.Line)
            .ThenBy(symbol => symbol.Range.Start.Character)
            .Select(LspTypeConversions.ToLspDocumentSymbol)
            .ToArray();

        await LogAsync($"documentSymbol {path}: {symbols.Length}");
        await SendResultAsync(connection, id, symbols, cancellationToken);
    }

    private async Task AnalyzeAndPublishDiagnosticsAsync(
        LspConnection connection,
        string contextPath,
        CancellationToken cancellationToken)
    {
        var snapshot = Analyze(contextPath);
        foreach (var document in _openDocumentsByPath.Values)
        {
            var diagnostics = snapshot.Diagnostics
                .Where(diagnostic => DiagnosticBelongsToDocument(diagnostic, document.Path, contextPath))
                .Select(LspTypeConversions.ToLspDiagnostic)
                .ToArray();

            await PublishDiagnosticsAsync(connection, document.Uri, diagnostics, cancellationToken);
        }

        await LogAsync($"analysis {contextPath}: {snapshot.Diagnostics.Count} diagnostics");
    }

    private FlxAnalysisSnapshot Analyze(string contextPath)
    {
        var workspace = FlxWorkspace.LoadForFile(contextPath, new FlxWorkspaceOptions
        {
            RequireSchedule = false,
            ValidateScheduleTargets = true,
            ValidateBinaryArtifacts = false
        });

        foreach (var document in _openDocumentsByPath.Values)
            workspace.OpenDocument(document.Path, document.Text);

        return workspace.Analyze();
    }

    private static bool DiagnosticBelongsToDocument(FlxDiagnostic diagnostic, string documentPath, string contextPath)
    {
        if (diagnostic.Path is null)
            return PathsEqual(documentPath, contextPath);

        return PathsEqual(diagnostic.Path, documentPath);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static object InitializeResult()
    {
        return new
        {
            capabilities = new
            {
                textDocumentSync = new
                {
                    openClose = true,
                    change = TextDocumentSyncKind.Full
                },
                documentSymbolProvider = true
            },
            serverInfo = new
            {
                name = "flx-lsp",
                version = "0.1.0"
            }
        };
    }

    private static T ReadParams<T>(JsonElement root) where T : new()
    {
        if (!root.TryGetProperty("params", out var parameters))
            return new T();

        return parameters.Deserialize<T>(JsonOptions) ?? new T();
    }

    private static JsonElement? TryGetId(JsonElement root)
    {
        return root.TryGetProperty("id", out var id) ? id.Clone() : null;
    }

    private static async Task SendResultAsync(
        LspConnection connection,
        JsonElement id,
        object? result,
        CancellationToken cancellationToken)
    {
        await connection.WriteAsync(new
        {
            jsonrpc = "2.0",
            id,
            result
        }, cancellationToken);
    }

    private static async Task SendErrorAsync(
        LspConnection connection,
        JsonElement? id,
        int code,
        string message,
        CancellationToken cancellationToken)
    {
        await connection.WriteAsync(new
        {
            jsonrpc = "2.0",
            id,
            error = new
            {
                code,
                message
            }
        }, cancellationToken);
    }

    private static async Task PublishDiagnosticsAsync(
        LspConnection connection,
        string uri,
        IReadOnlyList<LspDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        await connection.WriteAsync(new
        {
            jsonrpc = "2.0",
            method = "textDocument/publishDiagnostics",
            @params = new
            {
                uri,
                diagnostics
            }
        }, cancellationToken);
    }

    private static string UriToPath(string uri)
    {
        return Path.GetFullPath(new Uri(uri).LocalPath);
    }

    private Task LogAsync(string message)
    {
        return _log.WriteLineAsync($"[{DateTimeOffset.Now:O}] {message}");
    }
}
