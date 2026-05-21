namespace Flx.LanguageServices;

public sealed class FlxHoverResult
{
    public FlxHoverResult(string text)
    {
        Text = text;
    }

    public string Text { get; }
}
