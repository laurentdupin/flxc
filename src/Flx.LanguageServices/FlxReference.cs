namespace Flx.LanguageServices;

internal enum FlxReferenceKind
{
    Declaration,
    ScheduleRunTarget,
    TypeName,
    FlattenComponent,
    CreatePrefab,
    MethodCall
}

internal sealed class FlxReference
{
    public required string Path { get; init; }
    public required FlxRange Range { get; init; }
    public required FlxReferenceKind Kind { get; init; }
    public string TargetKey { get; init; } = "";
    public string TargetFullName { get; init; } = "";
    public required FlxSymbolKind TargetKind { get; init; }
    public IReadOnlyList<string> TargetKeys { get; init; } = [];
    public IReadOnlyList<string> TargetFullNames { get; init; } = [];

    public IReadOnlyList<string> EffectiveTargetKeys =>
        TargetKeys.Count > 0 ? TargetKeys : string.IsNullOrWhiteSpace(TargetKey) ? [] : [TargetKey];

    public IReadOnlyList<string> EffectiveTargetFullNames =>
        TargetFullNames.Count > 0 ? TargetFullNames : string.IsNullOrWhiteSpace(TargetFullName) ? [] : [TargetFullName];
}

internal sealed class FlxSymbolDefinition
{
    public required string Key { get; init; }
    public required string FullName { get; init; }
    public required FlxSymbolKind Kind { get; init; }
    public required string Path { get; init; }
    public required FlxRange Range { get; init; }
}

public sealed class FlxSymbolInfo
{
    public required string Key { get; init; }
    public required string FullName { get; init; }
    public required FlxSymbolKind Kind { get; init; }
    public required string Display { get; init; }
    public string? Detail { get; init; }
    public string? PackageName { get; init; }
    public string? ModuleName { get; init; }
    public string? SourcePath { get; init; }
}
