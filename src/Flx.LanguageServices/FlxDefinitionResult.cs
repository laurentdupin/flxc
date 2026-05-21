namespace Flx.LanguageServices;

public sealed class FlxDefinitionResult
{
    public FlxDefinitionResult(string path, FlxRange range)
    {
        Path = path;
        Range = range;
    }

    public string Path { get; }
    public FlxRange Range { get; }
}
