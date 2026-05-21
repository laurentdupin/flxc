using Flx.LanguageServices;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: Flx.LanguageServices.Smoke <file.flx | flx.package.json>");
    return 2;
}

var input = Path.GetFullPath(args[0]);
var workspace = Path.GetFileName(input).Equals("flx.package.json", StringComparison.OrdinalIgnoreCase)
    ? FlxWorkspace.LoadFromPackage(input, new FlxWorkspaceOptions { ValidateBinaryArtifacts = false })
    : FlxWorkspace.LoadForFile(input, new FlxWorkspaceOptions { ValidateBinaryArtifacts = false });

var snapshot = workspace.Analyze();

Console.WriteLine("Diagnostics:");
if (snapshot.Diagnostics.Count == 0)
{
    Console.WriteLine("  none");
}
else
{
    foreach (var diagnostic in snapshot.Diagnostics)
    {
        var path = diagnostic.Path ?? input;
        Console.WriteLine($"  {path}({diagnostic.Line + 1},{diagnostic.Character + 1}): error {diagnostic.Id}: {diagnostic.Message}");
    }
}

Console.WriteLine();
Console.WriteLine("Document symbols:");
if (snapshot.DocumentSymbols.Count == 0)
{
    Console.WriteLine("  none");
}
else
{
    foreach (var symbol in snapshot.DocumentSymbols.OrderBy(symbol => symbol.Path, StringComparer.OrdinalIgnoreCase).ThenBy(symbol => symbol.Range.Start.Line).ThenBy(symbol => symbol.Range.Start.Character))
        Console.WriteLine($"  {symbol.Path}({symbol.Range.Start.Line + 1},{symbol.Range.Start.Character + 1}): {symbol.Kind} {symbol.Name}{FormatDetail(symbol.Detail)}");
}

return snapshot.Diagnostics.Count == 0 ? 0 : 1;

static string FormatDetail(string? detail)
{
    return string.IsNullOrWhiteSpace(detail) ? "" : $" - {detail}";
}
