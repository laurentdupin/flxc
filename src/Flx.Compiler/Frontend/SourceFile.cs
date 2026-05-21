using Flx.Compiler.Diagnostics;

namespace Flx.Compiler.Frontend;

internal sealed class SourceFile
{
    private readonly int[] _lineStarts;

    public SourceFile(
        string path,
        string text,
        string? originalText = null,
        string? packageName = null,
        string? packageRoot = null)
    {
        FullPath = Path.GetFullPath(path);
        DisplayPath = MakeDisplayPath(path);
        Text = text;
        OriginalText = originalText ?? text;
        PackageName = packageName;
        PackageRoot = packageRoot is null ? null : Path.GetFullPath(packageRoot);
        _lineStarts = ComputeLineStarts(text);
    }

    public string FullPath { get; }
    public string DisplayPath { get; }
    public string Text { get; }
    public string OriginalText { get; }
    public string? PackageName { get; }
    public string? PackageRoot { get; }

    public SourceLocation GetLocation(int position)
    {
        position = Math.Clamp(position, 0, Text.Length);
        var lineIndex = Array.BinarySearch(_lineStarts, position);
        if (lineIndex < 0)
            lineIndex = ~lineIndex - 1;

        lineIndex = Math.Clamp(lineIndex, 0, _lineStarts.Length - 1);
        var column = position - _lineStarts[lineIndex] + 1;
        return new SourceLocation(DisplayPath, lineIndex + 1, column, position);
    }

    private static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }

        return starts.ToArray();
    }

    private static string MakeDisplayPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());

        if (!cwd.EndsWith(Path.DirectorySeparatorChar))
            cwd += Path.DirectorySeparatorChar;

        if (fullPath.StartsWith(cwd, StringComparison.OrdinalIgnoreCase))
            return fullPath[cwd.Length..].Replace(Path.DirectorySeparatorChar, '/');

        return path.Replace(Path.DirectorySeparatorChar, '/');
    }
}
