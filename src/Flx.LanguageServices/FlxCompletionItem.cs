namespace Flx.LanguageServices;

public sealed class FlxCompletionItem
{
    public FlxCompletionItem(
        string label,
        FlxCompletionKind kind,
        string? detail = null,
        string? insertText = null,
        string? documentation = null)
    {
        Label = label;
        Kind = kind;
        Detail = detail;
        InsertText = insertText;
        Documentation = documentation;
    }

    public string Label { get; }
    public FlxCompletionKind Kind { get; }
    public string? Detail { get; }
    public string? InsertText { get; }
    public string? Documentation { get; }
}

public enum FlxCompletionKind
{
    Keyword,
    Type,
    Function,
    Method,
    Field,
    Variable,
    Module,
    Component,
    Prefab,
    Global,
    Snippet
}

internal sealed class FlxFunctionScope
{
    public required string Path { get; init; }
    public required FlxRange BodyRange { get; init; }
    public required IReadOnlyDictionary<string, string> VariableTypes { get; init; }
}

internal sealed class FlxMemberCompletionSet
{
    public required string TypeName { get; init; }
    public required IReadOnlyList<FlxCompletionItem> Items { get; init; }
    public required IReadOnlyDictionary<string, string> FieldTypes { get; init; }
}
