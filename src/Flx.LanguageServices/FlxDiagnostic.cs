namespace Flx.LanguageServices;

public sealed class FlxDiagnostic
{
    public FlxDiagnostic(string id, string message, string? path, int line, int character, int position)
    {
        Id = id;
        Message = message;
        Path = path;
        Line = line;
        Character = character;
        Position = position;
    }

    public string Id { get; }
    public string Message { get; }
    public string? Path { get; }

    /// <summary>Zero-based line number.</summary>
    public int Line { get; }

    /// <summary>Zero-based character offset within the line.</summary>
    public int Character { get; }

    public int Position { get; }
}
