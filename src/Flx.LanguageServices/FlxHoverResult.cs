namespace Flx.LanguageServices;

public sealed class FlxHoverResult
{
    public FlxHoverResult(string text, FlxRange? range = null)
    {
        Text = text;
        Range = range;
    }

    public string Text { get; }
    public FlxRange? Range { get; }
}
