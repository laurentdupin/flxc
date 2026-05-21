using System.Text.Json.Serialization;
using Flx.LanguageServices;

namespace Flx.LanguageServer.Protocol;

internal static class LspErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InternalError = -32603;
}

internal static class TextDocumentSyncKind
{
    public const int Full = 1;
}

internal static class DiagnosticSeverity
{
    public const int Error = 1;
}

internal static class SymbolKind
{
    public const int Module = 2;
    public const int Namespace = 3;
    public const int Class = 5;
    public const int Method = 6;
    public const int Function = 12;
    public const int Variable = 13;
    public const int Event = 24;
    public const int Struct = 23;
}

internal sealed record LspPosition(int Line, int Character);

internal sealed record LspRange(LspPosition Start, LspPosition End);

internal sealed class LspLocation
{
    public required string Uri { get; init; }
    public required LspRange Range { get; init; }
}

internal sealed class LspDiagnostic
{
    public required LspRange Range { get; init; }
    public int Severity { get; init; } = DiagnosticSeverity.Error;
    public required string Code { get; init; }
    public string Source { get; init; } = "flx";
    public required string Message { get; init; }
}

internal sealed class LspDocumentSymbol
{
    public required string Name { get; init; }
    public string Detail { get; init; } = "";
    public required int Kind { get; init; }
    public required LspRange Range { get; init; }
    public required LspRange SelectionRange { get; init; }
    public IReadOnlyList<LspDocumentSymbol> Children { get; init; } = [];
}

internal sealed class TextDocumentIdentifier
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";
}

internal sealed class TextDocumentItem
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

internal sealed class VersionedTextDocumentIdentifier
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";
}

internal sealed class TextDocumentContentChangeEvent
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

internal sealed class DidOpenTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public TextDocumentItem TextDocument { get; set; } = new();
}

internal sealed class DidChangeTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public VersionedTextDocumentIdentifier TextDocument { get; set; } = new();

    [JsonPropertyName("contentChanges")]
    public List<TextDocumentContentChangeEvent> ContentChanges { get; set; } = [];
}

internal sealed class DidCloseTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = new();
}

internal sealed class DocumentSymbolParams
{
    [JsonPropertyName("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = new();
}

internal sealed class TextDocumentPositionParams
{
    [JsonPropertyName("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = new();

    [JsonPropertyName("position")]
    public LspPosition Position { get; set; } = new(0, 0);
}

internal sealed record OpenDocumentState(string Uri, string Path, string Text);

internal static class LspTypeConversions
{
    public static LspDiagnostic ToLspDiagnostic(FlxDiagnostic diagnostic)
    {
        var start = new LspPosition(diagnostic.Line, diagnostic.Character);
        var end = new LspPosition(diagnostic.Line, diagnostic.Character + 1);
        return new LspDiagnostic
        {
            Range = new LspRange(start, end),
            Code = diagnostic.Id,
            Message = diagnostic.Message
        };
    }

    public static LspDocumentSymbol ToLspDocumentSymbol(FlxDocumentSymbol symbol)
    {
        var range = ToLspRange(symbol.Range);
        return new LspDocumentSymbol
        {
            Name = symbol.Name,
            Detail = symbol.Detail ?? "",
            Kind = ToLspSymbolKind(symbol.Kind),
            Range = range,
            SelectionRange = range,
            Children = []
        };
    }

    public static LspLocation ToLspLocation(FlxDefinitionResult definition)
    {
        return new LspLocation
        {
            Uri = new Uri(Path.GetFullPath(definition.Path)).AbsoluteUri,
            Range = ToLspRange(definition.Range)
        };
    }

    private static LspRange ToLspRange(FlxRange range)
    {
        return new LspRange(
            new LspPosition(range.Start.Line, range.Start.Character),
            new LspPosition(range.End.Line, range.End.Character));
    }

    private static int ToLspSymbolKind(FlxSymbolKind kind)
    {
        return kind switch
        {
            FlxSymbolKind.Module => SymbolKind.Module,
            FlxSymbolKind.Component => SymbolKind.Struct,
            FlxSymbolKind.Prefab => SymbolKind.Class,
            FlxSymbolKind.Function => SymbolKind.Function,
            FlxSymbolKind.Method => SymbolKind.Method,
            FlxSymbolKind.Global => SymbolKind.Variable,
            FlxSymbolKind.Schedule => SymbolKind.Event,
            _ => SymbolKind.Namespace
        };
    }
}
