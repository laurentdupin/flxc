namespace Flx.LanguageServices;

public sealed class FlxDocument
{
    internal FlxDocument(string path, string text)
    {
        Path = System.IO.Path.GetFullPath(path);
        Text = text;
    }

    public string Path { get; }
    public string Text { get; }
}
