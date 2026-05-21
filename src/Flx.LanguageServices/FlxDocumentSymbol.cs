namespace Flx.LanguageServices;

public sealed class FlxDocumentSymbol
{
    public FlxDocumentSymbol(
        string name,
        FlxSymbolKind kind,
        string path,
        FlxRange range,
        string? detail = null)
    {
        Name = name;
        Kind = kind;
        Path = path;
        Range = range;
        Detail = detail;
    }

    public string Name { get; }
    public FlxSymbolKind Kind { get; }
    public string Path { get; }
    public FlxRange Range { get; }
    public string? Detail { get; }
}

public enum FlxSymbolKind
{
    Module,
    Component,
    Prefab,
    Function,
    Method,
    Global,
    Schedule
}
