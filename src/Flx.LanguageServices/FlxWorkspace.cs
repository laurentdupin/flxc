using Flx.Compiler.Diagnostics;
using Flx.Compiler.Frontend;
using Flx.Compiler.Packages;
using Flx.Compiler.Semantics;

namespace Flx.LanguageServices;

public sealed class FlxWorkspace
{
    private readonly string? _packageManifestPath;
    private readonly IReadOnlyList<string> _looseSourceFiles;
    private readonly FlxWorkspaceOptions _options;
    private readonly Dictionary<string, FlxDocument> _openDocuments = new(StringComparer.OrdinalIgnoreCase);

    private FlxWorkspace(string? packageManifestPath, IReadOnlyList<string> looseSourceFiles, FlxWorkspaceOptions options)
    {
        _packageManifestPath = packageManifestPath is null ? null : Path.GetFullPath(packageManifestPath);
        _looseSourceFiles = looseSourceFiles.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _options = options;
    }

    public static FlxWorkspace LoadFromPackage(string packageManifestPath, FlxWorkspaceOptions? options = null)
    {
        return new FlxWorkspace(packageManifestPath, [], options ?? new FlxWorkspaceOptions());
    }

    public static FlxWorkspace LoadLooseFiles(IReadOnlyList<string> sourceFiles, FlxWorkspaceOptions? options = null)
    {
        return new FlxWorkspace(null, sourceFiles, options ?? new FlxWorkspaceOptions());
    }

    public static FlxWorkspace LoadForFile(string sourceFilePath, FlxWorkspaceOptions? options = null)
    {
        var packageManifest = FindNearestPackageManifest(sourceFilePath);
        if (packageManifest is not null)
            return LoadFromPackage(packageManifest, options);

        return LoadLooseFiles([sourceFilePath], options);
    }

    public static string? FindNearestPackageManifest(string sourceFilePath)
    {
        var fullPath = Path.GetFullPath(sourceFilePath);
        var directory = Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath);

        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(directory, "flx.package.json");
            if (File.Exists(candidate))
                return candidate;

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    public FlxDocument OpenDocument(string path, string text)
    {
        var document = new FlxDocument(path, text);
        _openDocuments[document.Path] = document;
        return document;
    }

    public void UpdateDocument(string path, string text)
    {
        OpenDocument(path, text);
    }

    public void CloseDocument(string path)
    {
        _openDocuments.Remove(Path.GetFullPath(path));
    }

    public FlxAnalysisSnapshot Analyze()
    {
        var diagnostics = new DiagnosticBag();
        var packageGraph = LoadPackageGraph(diagnostics);
        var sources = LoadSources(packageGraph, diagnostics);
        var units = ParseSources(sources, diagnostics);
        ValidatePackageSchedules(packageGraph, units, diagnostics);

        var externalPackages = packageGraph?.BinaryPackages.Select(package => package.Metadata).ToArray() ?? [];
        var model = new SemanticAnalyzer(diagnostics).Analyze(
            units,
            _options.RequireSchedule,
            _options.ValidateScheduleTargets,
            externalPackages);

        ExportValidator.ValidateLibraryExports(packageGraph, model, diagnostics);

        return new FlxAnalysisSnapshot(
            diagnostics.Diagnostics.Select(ConvertDiagnostic).ToArray(),
            CollectDocumentSymbols(model).ToArray());
    }

    private PackageGraph? LoadPackageGraph(DiagnosticBag diagnostics)
    {
        if (string.IsNullOrWhiteSpace(_packageManifestPath))
            return null;

        return new PackageLoader(_options.ValidateBinaryArtifacts).Load(_packageManifestPath, diagnostics);
    }

    private IReadOnlyList<SourceFile> LoadSources(PackageGraph? packageGraph, DiagnosticBag diagnostics)
    {
        if (packageGraph is not null)
            return LoadPackageSources(packageGraph, diagnostics);

        return LoadLooseSources(diagnostics);
    }

    private IReadOnlyList<SourceFile> LoadPackageSources(PackageGraph packageGraph, DiagnosticBag diagnostics)
    {
        var sources = new List<SourceFile>();
        foreach (var package in packageGraph.SourceOrder)
        {
            foreach (var sourcePath in package.SourcePaths)
            {
                var text = ReadSourceText(sourcePath, diagnostics);
                if (text is null)
                    continue;

                sources.Add(new SourceFile(
                    sourcePath,
                    text,
                    packageName: package.Name,
                    packageRoot: package.RootDirectory));
            }
        }

        return sources;
    }

    private IReadOnlyList<SourceFile> LoadLooseSources(DiagnosticBag diagnostics)
    {
        var sourcePaths = _looseSourceFiles
            .Concat(_openDocuments.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sources = new List<SourceFile>();
        foreach (var sourcePath in sourcePaths)
        {
            var text = ReadSourceText(sourcePath, diagnostics);
            if (text is null)
                continue;

            sources.Add(new SourceFile(sourcePath, text));
        }

        return sources;
    }

    private string? ReadSourceText(string sourcePath, DiagnosticBag diagnostics)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        if (_openDocuments.TryGetValue(fullPath, out var openDocument))
            return openDocument.Text;

        if (File.Exists(fullPath))
            return File.ReadAllText(fullPath);

        diagnostics.Report("FLX9005", $"input file '{sourcePath}' does not exist.");
        return null;
    }

    private static IReadOnlyList<CompilationUnitSyntax> ParseSources(IEnumerable<SourceFile> sourceFiles, DiagnosticBag diagnostics)
    {
        var units = new List<CompilationUnitSyntax>();
        foreach (var source in sourceFiles)
        {
            var lexer = new Lexer(source, diagnostics);
            var tokens = lexer.Lex();
            var parser = new Parser(source, tokens, diagnostics);
            units.Add(parser.ParseCompilationUnit());
        }

        return units;
    }

    private static void ValidatePackageSchedules(PackageGraph? packageGraph, IReadOnlyList<CompilationUnitSyntax> units, DiagnosticBag diagnostics)
    {
        if (packageGraph is null)
            return;

        var packagesByName = packageGraph.Packages.ToDictionary(package => package.Name, StringComparer.Ordinal);
        foreach (var unit in units)
        {
            if (unit.Schedules.Count == 0)
                continue;

            var packageName = unit.Source.PackageName;
            if (packageName is null || !packagesByName.TryGetValue(packageName, out var package))
                continue;

            if (!ReferenceEquals(package, packageGraph.RootPackage))
            {
                foreach (var schedule in unit.Schedules)
                    diagnostics.Report("FLX0505", $"schedule block found in dependency package '{package.Name}'.", schedule.Location);
                continue;
            }

            if (package.IsLibrary)
            {
                foreach (var schedule in unit.Schedules)
                    diagnostics.Report("FLX0503", $"library package '{package.Name}' cannot contain a schedule block.", schedule.Location);
            }
        }
    }

    private static IEnumerable<FlxDocumentSymbol> CollectDocumentSymbols(CompilationModel model)
    {
        foreach (var module in model.Modules)
        {
            if (module.Syntax.Module is { } moduleDecl)
                yield return CreateSymbol(module.Name, FlxSymbolKind.Module, module.SourceFile.FullPath, moduleDecl.Location, module.Name);

            foreach (var component in module.Components)
                yield return CreateSymbol(component.Name, FlxSymbolKind.Component, component.SourceFile.FullPath, component.Syntax.NameLocation, component.FullName);

            foreach (var prefab in module.Prefabs)
                yield return CreateSymbol(prefab.Name, FlxSymbolKind.Prefab, prefab.SourceFile.FullPath, prefab.Syntax.NameLocation, prefab.FullName);

            foreach (var global in module.Globals)
                yield return CreateSymbol(global.Name, FlxSymbolKind.Global, global.SourceFile.FullPath, global.Syntax.NameLocation, global.Type);

            foreach (var function in module.Functions)
            {
                var kind = function.ReceiverType is null ? FlxSymbolKind.Function : FlxSymbolKind.Method;
                yield return CreateSymbol(function.SourceName, kind, function.SourceFile.FullPath, function.Syntax.NameLocation, FormatFunctionDetail(function));
            }

            foreach (var schedule in module.Syntax.Schedules)
                yield return CreateSymbol("schedule", FlxSymbolKind.Schedule, module.SourceFile.FullPath, schedule.Location, null);
        }
    }

    private static FlxDocumentSymbol CreateSymbol(
        string name,
        FlxSymbolKind kind,
        string path,
        SourceLocation location,
        string? detail)
    {
        var start = ToPosition(location);
        var end = new FlxPosition(start.Line, start.Character + Math.Max(1, name.Length));
        return new FlxDocumentSymbol(name, kind, path, new FlxRange(start, end), detail);
    }

    private static string FormatFunctionDetail(FunctionSymbol function)
    {
        var parameters = string.Join(", ", function.Parameters.Select(parameter => $"{parameter.Type} {parameter.Name}"));
        var prefix = function.ReceiverType is null ? "function" : "method";
        return $"{prefix} {function.ReturnType} {function.FullName}({parameters})";
    }

    private static FlxDiagnostic ConvertDiagnostic(Diagnostic diagnostic)
    {
        if (diagnostic.Location is not { } location)
            return new FlxDiagnostic(diagnostic.Id, diagnostic.Message, null, 0, 0, 0);

        return new FlxDiagnostic(
            diagnostic.Id,
            diagnostic.Message,
            location.FilePath,
            Math.Max(0, location.Line - 1),
            Math.Max(0, location.Column - 1),
            location.Position);
    }

    private static FlxPosition ToPosition(SourceLocation location)
    {
        return new FlxPosition(Math.Max(0, location.Line - 1), Math.Max(0, location.Column - 1));
    }
}
