namespace Flx.LanguageServices;

public sealed class FlxCompletionItem
{
    public FlxCompletionItem(string label, string? detail = null)
    {
        Label = label;
        Detail = detail;
    }

    public string Label { get; }
    public string? Detail { get; }
}
