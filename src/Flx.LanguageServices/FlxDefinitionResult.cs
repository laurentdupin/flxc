namespace Flx.LanguageServices;

public sealed class FlxDefinitionResult
{
    public FlxDefinitionResult(string path, FlxRange range)
        : this([new FlxDefinitionLocation(path, range)])
    {
    }

    public FlxDefinitionResult(IReadOnlyList<FlxDefinitionLocation> locations)
    {
        Locations = locations;
    }

    public IReadOnlyList<FlxDefinitionLocation> Locations { get; }
    public string Path => Locations[0].Path;
    public FlxRange Range => Locations[0].Range;
}

public sealed record FlxDefinitionLocation(string Path, FlxRange Range);
