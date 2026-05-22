using Flx.Compiler.Diagnostics;
using Flx.Compiler.Frontend;
using Flx.Compiler.Metadata;
using Flx.Compiler.Packages;
using Flx.Compiler.Semantics;
using System.Text.RegularExpressions;

namespace Flx.LanguageServices;

public sealed class FlxWorkspace
{
    private const string QualifiedNamePattern = @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*";

    private static readonly Regex FlattenReferenceRegex = new(
        @"\bflatten\s+(?<name>" + QualifiedNamePattern + @")\s*;",
        RegexOptions.Multiline);

    private static readonly Regex CreateReferenceRegex = new(
        @"\bcreate\s+(?<name>" + QualifiedNamePattern + @")\b",
        RegexOptions.Multiline);

    private static readonly Regex LocalDeclarationRegex = new(
        @"\b(?<type>" + QualifiedNamePattern + @"(?:<" + QualifiedNamePattern + @">)?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.Multiline);

    private static readonly Regex MethodCallRegex = new(
        @"\b(?<target>[A-Za-z_][A-Za-z0-9_]*)\.(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*\(\s*(?<args>[^()]*)\s*\)",
        RegexOptions.Multiline);

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
            CollectDocumentSymbols(model).ToArray(),
            CollectReferences(model).ToArray(),
            CollectDefinitions(model, packageGraph),
            CollectSymbolInfos(model, packageGraph),
            sources.ToDictionary(source => source.FullPath, source => source.Text, StringComparer.OrdinalIgnoreCase),
            CollectFunctionScopes(model).ToArray(),
            CollectMemberCompletions(model));
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

    private static IReadOnlyDictionary<string, FlxSymbolDefinition> CollectDefinitions(
        CompilationModel model,
        PackageGraph? packageGraph)
    {
        var definitions = new Dictionary<string, FlxSymbolDefinition>(StringComparer.Ordinal);
        foreach (var module in model.Modules)
        {
            if (module.Syntax.Module is { } moduleDecl)
            {
                var range = FindNameRange(module.SourceFile, moduleDecl.Location.Position, module.Name) ??
                            RangeFromLocation(moduleDecl.Location, "module".Length);
                definitions[ModuleKey(module.Name)] = new FlxSymbolDefinition
                {
                    Key = ModuleKey(module.Name),
                    FullName = module.Name,
                    Kind = FlxSymbolKind.Module,
                    Path = module.SourceFile.FullPath,
                    Range = range
                };
            }

            foreach (var component in module.Components)
                definitions[ComponentKey(component.FullName)] = CreateDefinition(
                    ComponentKey(component.FullName),
                    component.FullName,
                    FlxSymbolKind.Component,
                    component.SourceFile.FullPath,
                    component.Syntax.NameLocation,
                    component.Name.Length);

            foreach (var prefab in module.Prefabs)
                definitions[PrefabKey(prefab.FullName)] = CreateDefinition(
                    PrefabKey(prefab.FullName),
                    prefab.FullName,
                    FlxSymbolKind.Prefab,
                    prefab.SourceFile.FullPath,
                    prefab.Syntax.NameLocation,
                    prefab.Name.Length);

            foreach (var global in module.Globals)
                definitions[GlobalKey(global.FullName)] = CreateDefinition(
                    GlobalKey(global.FullName),
                    global.FullName,
                    FlxSymbolKind.Global,
                    global.SourceFile.FullPath,
                    global.Syntax.NameLocation,
                    global.Name.Length);

            foreach (var function in module.Functions)
            {
                var kind = function.ReceiverType is null ? FlxSymbolKind.Function : FlxSymbolKind.Method;
                definitions[FunctionKey(function)] = CreateDefinition(
                    FunctionKey(function),
                    function.FullName,
                    kind,
                    function.SourceFile.FullPath,
                    function.Syntax.NameLocation,
                function.SourceName.Length);
            }
        }

        AddBinaryPackageDefinitions(packageGraph, definitions);

        foreach (var component in model.ComponentsByFullName.Values)
        {
            if (!File.Exists(component.SourceFile.FullPath))
                continue;

            definitions.TryAdd(ComponentKey(component.FullName), CreateDefinition(
                ComponentKey(component.FullName),
                component.FullName,
                FlxSymbolKind.Component,
                component.SourceFile.FullPath,
                component.Syntax.NameLocation,
                Math.Max(1, component.Name.Length)));
        }

        foreach (var prefab in model.PrefabsByFullName.Values)
        {
            if (!File.Exists(prefab.SourceFile.FullPath))
                continue;

            definitions.TryAdd(PrefabKey(prefab.FullName), CreateDefinition(
                PrefabKey(prefab.FullName),
                prefab.FullName,
                FlxSymbolKind.Prefab,
                prefab.SourceFile.FullPath,
                prefab.Syntax.NameLocation,
                Math.Max(1, prefab.Name.Length)));
        }

        foreach (var function in model.FunctionRegistry.AllFunctions.Where(function => function.IsExternal))
        {
            if (!File.Exists(function.SourceFile.FullPath))
                continue;

            var kind = function.ReceiverType is null ? FlxSymbolKind.Function : FlxSymbolKind.Method;
            definitions.TryAdd(FunctionKey(function), CreateDefinition(
                FunctionKey(function),
                function.FullName,
                kind,
                function.SourceFile.FullPath,
                function.Location,
                Math.Max(1, function.SourceName.Length)));
        }

        return definitions;
    }

    private static void AddBinaryPackageDefinitions(
        PackageGraph? packageGraph,
        Dictionary<string, FlxSymbolDefinition> definitions)
    {
        if (packageGraph is null)
            return;

        foreach (var package in packageGraph.BinaryPackages)
        {
            foreach (var component in package.Metadata.Symbols.Components)
            {
                definitions[ComponentKey(component.FullName)] = CreateBinaryPackageDefinition(
                    package,
                    ComponentKey(component.FullName),
                    component.FullName,
                    FlxSymbolKind.Component,
                    component.Source,
                    component.Line,
                    component.Column,
                    component.Name,
                    component.FullName);
            }

            foreach (var prefab in package.Metadata.Symbols.Prefabs)
            {
                definitions[PrefabKey(prefab.FullName)] = CreateBinaryPackageDefinition(
                    package,
                    PrefabKey(prefab.FullName),
                    prefab.FullName,
                    FlxSymbolKind.Prefab,
                    prefab.Source,
                    prefab.Line,
                    prefab.Column,
                    prefab.Name,
                    prefab.FullName);
            }

            foreach (var function in package.Metadata.Symbols.Functions)
            {
                definitions["function:" + function.MangledName] = CreateBinaryPackageDefinition(
                    package,
                    "function:" + function.MangledName,
                    function.FullName,
                    function.ReceiverType is null ? FlxSymbolKind.Function : FlxSymbolKind.Method,
                    function.Source,
                    function.Line,
                    function.Column,
                    function.SourceName,
                    function.FullName);
            }
        }
    }

    private static IReadOnlyDictionary<string, FlxSymbolInfo> CollectSymbolInfos(
        CompilationModel model,
        PackageGraph? packageGraph)
    {
        var infos = new Dictionary<string, FlxSymbolInfo>(StringComparer.Ordinal);
        foreach (var module in model.Modules)
        {
            if (module.Syntax.Module is not null)
            {
                infos[ModuleKey(module.Name)] = new FlxSymbolInfo
                {
                    Key = ModuleKey(module.Name),
                    FullName = module.Name,
                    Kind = FlxSymbolKind.Module,
                    Display = $"module {module.Name}",
                    PackageName = module.SourceFile.PackageName,
                    ModuleName = module.Name,
                    SourcePath = module.SourceFile.FullPath
                };
            }

            foreach (var component in module.Components)
                infos[ComponentKey(component.FullName)] = new FlxSymbolInfo
                {
                    Key = ComponentKey(component.FullName),
                    FullName = component.FullName,
                    Kind = FlxSymbolKind.Component,
                    Display = $"component {component.FullName}",
                    Detail = FormatComponentFields(component.Fields),
                    PackageName = component.SourceFile.PackageName,
                    ModuleName = module.Name,
                    SourcePath = component.SourceFile.FullPath
                };

            foreach (var prefab in module.Prefabs)
                infos[PrefabKey(prefab.FullName)] = new FlxSymbolInfo
                {
                    Key = PrefabKey(prefab.FullName),
                    FullName = prefab.FullName,
                    Kind = FlxSymbolKind.Prefab,
                    Display = $"prefab {prefab.FullName}",
                    Detail = FormatFlattenedComponents(prefab.FlattenedComponents),
                    PackageName = prefab.SourceFile.PackageName,
                    ModuleName = module.Name,
                    SourcePath = prefab.SourceFile.FullPath
                };

            foreach (var global in module.Globals)
                infos[GlobalKey(global.FullName)] = new FlxSymbolInfo
                {
                    Key = GlobalKey(global.FullName),
                    FullName = global.FullName,
                    Kind = FlxSymbolKind.Global,
                    Display = $"global {global.Type} {global.FullName}",
                    PackageName = global.SourceFile.PackageName,
                    ModuleName = module.Name,
                    SourcePath = global.SourceFile.FullPath
                };

            foreach (var function in module.Functions)
                infos[FunctionKey(function)] = CreateFunctionInfo(function);

            foreach (var parallel in module.ParallelExternalCalls)
            {
                var key = ParallelExternalKey(module.SourceFile.FullPath, parallel.FullName);
                infos[key] = new FlxSymbolInfo
                {
                    Key = key,
                    FullName = parallel.FullName,
                    Kind = FlxSymbolKind.Function,
                    Display = $"{parallel.FullName} allowed in parallel scheduled jobs",
                    Detail = "Calls may execute concurrently and unordered. The programmer accepts responsibility for external side effects.",
                    PackageName = module.SourceFile.PackageName,
                    ModuleName = module.Name,
                    SourcePath = module.SourceFile.FullPath
                };
            }
        }

        AddBinaryPackageSymbolInfos(packageGraph, infos);

        foreach (var function in model.FunctionRegistry.AllFunctions.Where(function => function.IsExternal))
            infos.TryAdd(FunctionKey(function), CreateFunctionInfo(function));

        return infos;
    }

    private static void AddBinaryPackageSymbolInfos(
        PackageGraph? packageGraph,
        Dictionary<string, FlxSymbolInfo> infos)
    {
        if (packageGraph is null)
            return;

        foreach (var package in packageGraph.BinaryPackages)
        {
            foreach (var component in package.Metadata.Symbols.Components)
            {
                infos[ComponentKey(component.FullName)] = new FlxSymbolInfo
                {
                    Key = ComponentKey(component.FullName),
                    FullName = component.FullName,
                    Kind = FlxSymbolKind.Component,
                    Display = $"component {component.FullName}",
                    Detail = FormatComponentFields(component.Fields.Select(field =>
                        new ComponentFieldSymbol(field.Type, field.Name, field.DefaultValue, default)).ToArray()),
                    PackageName = package.Name,
                    ModuleName = ModuleNameFromFullName(component.FullName, component.Name),
                    SourcePath = ResolveBinaryPackageSourcePath(package, component.Source) ?? package.MetadataPath
                };
            }

            foreach (var prefab in package.Metadata.Symbols.Prefabs)
            {
                infos[PrefabKey(prefab.FullName)] = new FlxSymbolInfo
                {
                    Key = PrefabKey(prefab.FullName),
                    FullName = prefab.FullName,
                    Kind = FlxSymbolKind.Prefab,
                    Display = $"prefab {prefab.FullName}",
                    Detail = prefab.FlattenedComponents.Count == 0
                        ? null
                        : "flattens " + string.Join(", ", prefab.FlattenedComponents),
                    PackageName = package.Name,
                    ModuleName = ModuleNameFromFullName(prefab.FullName, prefab.Name),
                    SourcePath = ResolveBinaryPackageSourcePath(package, prefab.Source) ?? package.MetadataPath
                };
            }

            foreach (var function in package.Metadata.Symbols.Functions)
            {
                var key = "function:" + function.MangledName;
                infos[key] = new FlxSymbolInfo
                {
                    Key = key,
                    FullName = function.FullName,
                    Kind = function.ReceiverType is null ? FlxSymbolKind.Function : FlxSymbolKind.Method,
                    Display = FormatFunctionMetadata(function),
                    Detail = function.ReceiverType is null ? null : $"receiver {function.ReceiverType}",
                    PackageName = package.Name,
                    ModuleName = ModuleNameFromFullName(function.FullName, function.SourceName),
                    SourcePath = ResolveBinaryPackageSourcePath(package, function.Source) ?? package.MetadataPath
                };
            }
        }
    }

    private static FlxSymbolInfo CreateFunctionInfo(FunctionSymbol function)
    {
        var kind = function.ReceiverType is null ? FlxSymbolKind.Function : FlxSymbolKind.Method;
        return new FlxSymbolInfo
        {
            Key = FunctionKey(function),
            FullName = function.FullName,
            Kind = kind,
            Display = FormatFunctionDetail(function),
            Detail = function.ReceiverType is null ? null : $"receiver {function.ReceiverType}",
            PackageName = function.SourceFile.PackageName,
            ModuleName = function.Module.Name,
            SourcePath = function.SourceFile.FullPath
        };
    }

    private static string? FormatComponentFields(IReadOnlyList<ComponentFieldSymbol> fields)
    {
        if (fields.Count == 0)
            return null;

        return string.Join("\n", fields.Select(field => $"field {field.Name} : {field.Type}"));
    }

    private static string? FormatFlattenedComponents(IReadOnlyList<ComponentSymbol> components)
    {
        if (components.Count == 0)
            return null;

        return "flattens " + string.Join(", ", components.Select(component => component.FullName));
    }

    private static IEnumerable<FlxFunctionScope> CollectFunctionScopes(CompilationModel model)
    {
        foreach (var module in model.Modules)
        {
            foreach (var function in module.Functions)
            {
                var variableTypes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var parameter in function.Parameters)
                    variableTypes[parameter.Name] = NormalizeCompletionType(model, module, parameter.Type);

                foreach (Match match in LocalDeclarationRegex.Matches(function.Syntax.BodyText))
                {
                    var typeName = match.Groups["type"].Value;
                    var variableName = match.Groups["name"].Value;
                    variableTypes.TryAdd(variableName, NormalizeCompletionType(model, module, typeName));
                }

                yield return new FlxFunctionScope
                {
                    Path = function.SourceFile.FullPath,
                    BodyRange = RangeFromOffsets(
                        function.SourceFile,
                        function.Syntax.BodyStart,
                        function.Syntax.BodyStart + function.Syntax.BodyText.Length),
                    VariableTypes = variableTypes
                };
            }
        }
    }

    private static IReadOnlyDictionary<string, FlxMemberCompletionSet> CollectMemberCompletions(CompilationModel model)
    {
        var result = new Dictionary<string, FlxMemberCompletionSet>(StringComparer.Ordinal);
        foreach (var prefab in model.PrefabsByFullName.Values.OrderBy(prefab => prefab.FullName, StringComparer.Ordinal))
        {
            var items = new List<FlxCompletionItem>();
            var fieldTypes = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var field in prefab.Fields.OrderBy(field => field.Field.Name, StringComparer.Ordinal))
            {
                var fieldType = NormalizeCompletionType(model, null, field.Field.Type);
                fieldTypes[field.Field.Name] = fieldType;
                items.Add(new FlxCompletionItem(
                    field.Field.Name,
                    FlxCompletionKind.Field,
                    $"{field.Field.Type} field from {field.Component.FullName}",
                    field.Field.Name));
            }

            foreach (var method in model.MethodRegistry.AllMethods
                         .Where(method => method.ReceiverType == prefab.FullName && method.Parameters.Count == 1)
                         .OrderBy(method => method.SourceName, StringComparer.Ordinal))
            {
                items.Add(new FlxCompletionItem(
                    method.SourceName,
                    FlxCompletionKind.Method,
                    FormatFunctionDetail(method),
                    method.SourceName + "()"));
            }

            result[prefab.FullName] = new FlxMemberCompletionSet
            {
                TypeName = prefab.FullName,
                Items = items,
                FieldTypes = fieldTypes
            };
        }

        return result;
    }

    private static string NormalizeCompletionType(CompilationModel model, ModuleSymbol? module, string typeName)
    {
        if (typeName == "string" ||
            typeName == "i32" ||
            typeName == "usize" ||
            typeName == "f32" ||
            typeName == "f64" ||
            typeName.StartsWith("Array<", StringComparison.Ordinal))
        {
            return typeName;
        }

        if (module is not null && model.ResolvePrefab(typeName, module) is { } modulePrefab)
            return modulePrefab.FullName;

        return model.PrefabsByFullName.TryGetValue(typeName, out var prefab)
            ? prefab.FullName
            : typeName;
    }

    private static IEnumerable<FlxReference> CollectReferences(CompilationModel model)
    {
        var references = new List<FlxReference>();

        foreach (var module in model.Modules)
        {
            CollectDeclarationReferences(module, references);
            CollectParallelExternalReferences(module, references);
            CollectScheduleReferences(model, module, references);

            foreach (var prefab in module.Prefabs)
                CollectFlattenReferences(model, module, prefab, references);

            foreach (var global in module.Globals)
                CollectGlobalTypeReferences(model, module, global, references);

            foreach (var function in module.Functions)
                CollectFunctionReferences(model, module, function, references);
        }

        return references;
    }

    private static void CollectDeclarationReferences(ModuleSymbol module, List<FlxReference> references)
    {
        if (module.Syntax.Module is { } moduleDecl)
        {
            var range = FindNameRange(module.SourceFile, moduleDecl.Location.Position, module.Name) ??
                        RangeFromLocation(moduleDecl.Location, "module".Length);
            references.Add(new FlxReference
            {
                Path = module.SourceFile.FullPath,
                Range = range,
                Kind = FlxReferenceKind.Declaration,
                TargetKey = ModuleKey(module.Name),
                TargetFullName = module.Name,
                TargetKind = FlxSymbolKind.Module
            });
        }

        foreach (var component in module.Components)
            references.Add(CreateReference(
                component.SourceFile.FullPath,
                RangeFromLocation(component.Syntax.NameLocation, component.Name.Length),
                FlxReferenceKind.Declaration,
                ComponentKey(component.FullName),
                component.FullName,
                FlxSymbolKind.Component));

        foreach (var prefab in module.Prefabs)
            references.Add(CreateReference(
                prefab.SourceFile.FullPath,
                RangeFromLocation(prefab.Syntax.NameLocation, prefab.Name.Length),
                FlxReferenceKind.Declaration,
                PrefabKey(prefab.FullName),
                prefab.FullName,
                FlxSymbolKind.Prefab));

        foreach (var global in module.Globals)
            references.Add(CreateReference(
                global.SourceFile.FullPath,
                RangeFromLocation(global.Syntax.NameLocation, global.Name.Length),
                FlxReferenceKind.Declaration,
                GlobalKey(global.FullName),
                global.FullName,
                FlxSymbolKind.Global));

        foreach (var function in module.Functions)
        {
            var kind = function.ReceiverType is null ? FlxSymbolKind.Function : FlxSymbolKind.Method;
            references.Add(CreateReference(
                function.SourceFile.FullPath,
                RangeFromLocation(function.Syntax.NameLocation, function.SourceName.Length),
                FlxReferenceKind.Declaration,
                FunctionKey(function),
                function.FullName,
                kind));
        }
    }

    private static void CollectParallelExternalReferences(ModuleSymbol module, List<FlxReference> references)
    {
        foreach (var parallel in module.ParallelExternalCalls)
        {
            references.Add(CreateReference(
                module.SourceFile.FullPath,
                RangeFromLocation(parallel.Location, parallel.FullName.Length),
                FlxReferenceKind.ParallelExternal,
                ParallelExternalKey(module.SourceFile.FullPath, parallel.FullName),
                parallel.FullName,
                FlxSymbolKind.Function));
        }
    }

    private static void CollectScheduleReferences(
        CompilationModel model,
        ModuleSymbol module,
        List<FlxReference> references)
    {
        foreach (var schedule in module.Syntax.Schedules)
        {
            foreach (var runStep in schedule.Steps.OfType<RunStepSyntax>())
            {
                var resolution = ScheduleTargetResolver.Resolve(model, runStep, module);
                if (resolution.IsAmbiguous || resolution.Functions.Count == 0)
                    continue;

                var targetGroups = resolution.Functions
                    .GroupBy(function => function.FullName, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray();

                if (!runStep.Target.HasWildcard && targetGroups.Length == 1)
                {
                    var target = targetGroups[0];
                    references.Add(CreateReference(
                        module.SourceFile.FullPath,
                        RangeFromLocation(runStep.Location, runStep.Name.Length),
                        FlxReferenceKind.ScheduleRunTarget,
                        FunctionKey(target),
                        target.FullName,
                        target.ReceiverType is null ? FlxSymbolKind.Function : FlxSymbolKind.Method));
                    continue;
                }

                references.Add(CreateMultiReference(
                    module.SourceFile.FullPath,
                    RangeFromLocation(runStep.Location, runStep.Name.Length),
                    FlxReferenceKind.ScheduleRunTarget,
                    runStep.Name,
                    targetGroups.Select(FunctionKey).ToArray(),
                    targetGroups.Select(function => function.FullName).ToArray(),
                    FlxSymbolKind.Function));
            }
        }
    }

    private static void CollectFlattenReferences(
        CompilationModel model,
        ModuleSymbol module,
        PrefabSymbol prefab,
        List<FlxReference> references)
    {
        var content = StripOuterBlock(prefab.Syntax.BodyText);
        foreach (Match match in FlattenReferenceRegex.Matches(content))
        {
            var componentName = match.Groups["name"].Value;
            var component = model.ResolveComponent(componentName, module);
            if (component is null)
                continue;

            var location = module.SourceFile.GetLocation(prefab.Syntax.BodyStart + 1 + match.Groups["name"].Index);
            references.Add(CreateReference(
                module.SourceFile.FullPath,
                RangeFromLocation(location, componentName.Length),
                FlxReferenceKind.FlattenComponent,
                ComponentKey(component.FullName),
                component.FullName,
                FlxSymbolKind.Component));
        }
    }

    private static void CollectGlobalTypeReferences(
        CompilationModel model,
        ModuleSymbol module,
        GlobalVariableSymbol global,
        List<FlxReference> references)
    {
        var start = LineStartBefore(global.SourceFile.Text, global.Syntax.DeclarationLocation.Position);
        var end = global.Syntax.NameLocation.Position;
        AddTypeReferences(global.SourceFile, module, model, global.Syntax.Type, start, end, references);
    }

    private static void CollectFunctionReferences(
        CompilationModel model,
        ModuleSymbol module,
        FunctionSymbol function,
        List<FlxReference> references)
    {
        var signatureStart = LineStartBefore(function.SourceFile.Text, function.Syntax.NameLocation.Position);
        var signatureEnd = function.Syntax.BodyStart;
        AddTypeReferences(function.SourceFile, module, model, function.Syntax.ReturnType, signatureStart, signatureEnd, references);
        foreach (var parameter in function.Syntax.Parameters)
            AddTypeReferences(function.SourceFile, module, model, parameter.Type, signatureStart, signatureEnd, references);

        var variableTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in function.Parameters)
        {
            if (model.PrefabsByFullName.ContainsKey(parameter.Type))
                variableTypes[parameter.Name] = parameter.Type;
        }

        foreach (Match match in LocalDeclarationRegex.Matches(function.Syntax.BodyText))
        {
            var typeName = match.Groups["type"].Value;
            var variableName = match.Groups["name"].Value;
            var prefab = model.ResolvePrefab(typeName, module);
            if (prefab is null)
                continue;

            variableTypes[variableName] = prefab.FullName;
            var typeLocation = function.SourceFile.GetLocation(function.Syntax.BodyStart + match.Groups["type"].Index);
            references.Add(CreateReference(
                function.SourceFile.FullPath,
                RangeFromLocation(typeLocation, typeName.Length),
                FlxReferenceKind.TypeName,
                PrefabKey(prefab.FullName),
                prefab.FullName,
                FlxSymbolKind.Prefab));
        }

        foreach (Match match in CreateReferenceRegex.Matches(function.Syntax.BodyText))
        {
            var prefabName = match.Groups["name"].Value;
            var prefab = model.ResolvePrefab(prefabName, module);
            if (prefab is null)
                continue;

            var prefabLocation = function.SourceFile.GetLocation(function.Syntax.BodyStart + match.Groups["name"].Index);
            references.Add(CreateReference(
                function.SourceFile.FullPath,
                RangeFromLocation(prefabLocation, prefabName.Length),
                FlxReferenceKind.CreatePrefab,
                PrefabKey(prefab.FullName),
                prefab.FullName,
                FlxSymbolKind.Prefab));
        }

        foreach (Match match in MethodCallRegex.Matches(function.Syntax.BodyText))
        {
            var targetName = match.Groups["target"].Value;
            if (!variableTypes.TryGetValue(targetName, out var receiverType))
                continue;

            if (!model.PrefabsByFullName.ContainsKey(receiverType))
                continue;

            var args = match.Groups["args"].Value.Trim();
            if (args.Length != 0)
                continue;

            var methodName = match.Groups["method"].Value;
            var methods = model.MethodRegistry.Resolve(receiverType, methodName, argumentCount: 0);
            if (methods.Count != 1)
                continue;

            var method = methods[0];
            var methodLocation = function.SourceFile.GetLocation(function.Syntax.BodyStart + match.Groups["method"].Index);
            references.Add(CreateReference(
                function.SourceFile.FullPath,
                RangeFromLocation(methodLocation, methodName.Length),
                FlxReferenceKind.MethodCall,
                FunctionKey(method),
                method.FullName,
                FlxSymbolKind.Method));
        }
    }

    private static void AddTypeReferences(
        SourceFile source,
        ModuleSymbol module,
        CompilationModel model,
        string typeName,
        int start,
        int end,
        List<FlxReference> references)
    {
        if (!TryResolveTypeTarget(model, module, typeName, out var targetKey, out var targetFullName, out var targetKind))
            return;

        foreach (var position in FindNameOccurrences(source.Text, typeName, start, end))
        {
            references.Add(CreateReference(
                source.FullPath,
                RangeFromLocation(source.GetLocation(position), typeName.Length),
                FlxReferenceKind.TypeName,
                targetKey,
                targetFullName,
                targetKind));
        }
    }

    private static bool TryResolveTypeTarget(
        CompilationModel model,
        ModuleSymbol module,
        string typeName,
        out string targetKey,
        out string targetFullName,
        out FlxSymbolKind targetKind)
    {
        targetKey = "";
        targetFullName = "";
        targetKind = FlxSymbolKind.Module;

        if (typeName is "void" or "i32" or "usize" or "string" or "Array<string>")
            return false;

        if (typeName.IndexOf('.', StringComparison.Ordinal) is var dotIndex && dotIndex > 0)
        {
            var alias = typeName[..dotIndex];
            if (module.CImportsByAlias.ContainsKey(alias))
                return false;
        }

        if (model.ResolvePrefab(typeName, module) is { } prefab)
        {
            targetKey = PrefabKey(prefab.FullName);
            targetFullName = prefab.FullName;
            targetKind = FlxSymbolKind.Prefab;
            return true;
        }

        if (model.ResolveComponent(typeName, module) is { } component)
        {
            targetKey = ComponentKey(component.FullName);
            targetFullName = component.FullName;
            targetKind = FlxSymbolKind.Component;
            return true;
        }

        return false;
    }

    private static IEnumerable<int> FindNameOccurrences(string text, string name, int start, int end)
    {
        if (string.IsNullOrWhiteSpace(name))
            yield break;

        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);

        var position = start;
        while (position < end)
        {
            var found = text.IndexOf(name, position, end - position, StringComparison.Ordinal);
            if (found < 0)
                yield break;

            var after = found + name.Length;
            if (IsNameBoundary(text, found - 1) && IsNameBoundary(text, after))
                yield return found;

            position = after;
        }
    }

    private static bool IsNameBoundary(string text, int index)
    {
        return index < 0 ||
               index >= text.Length ||
               !(char.IsLetterOrDigit(text[index]) || text[index] == '_' || text[index] == '.');
    }

    private static int LineStartBefore(string text, int position)
    {
        position = Math.Clamp(position, 0, text.Length);
        var lineStart = text.LastIndexOf('\n', Math.Max(0, position - 1));
        return lineStart < 0 ? 0 : lineStart + 1;
    }

    private static string StripOuterBlock(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '{' && trimmed[^1] == '}'
            ? trimmed[1..^1]
            : text;
    }

    private static FlxRange? FindNameRange(SourceFile source, int start, string name)
    {
        var position = source.Text.IndexOf(name, Math.Clamp(start, 0, source.Text.Length), StringComparison.Ordinal);
        return position < 0
            ? null
            : RangeFromLocation(source.GetLocation(position), name.Length);
    }

    private static FlxSymbolDefinition CreateDefinition(
        string key,
        string fullName,
        FlxSymbolKind kind,
        string path,
        SourceLocation location,
        int length)
    {
        return new FlxSymbolDefinition
        {
            Key = key,
            FullName = fullName,
            Kind = kind,
            Path = path,
            Range = RangeFromLocation(location, length)
        };
    }

    private static FlxSymbolDefinition CreateMetadataDefinition(
        string key,
        string fullName,
        FlxSymbolKind kind,
        string metadataPath,
        string searchText)
    {
        var fullPath = Path.GetFullPath(metadataPath);
        var range = FindTextRangeInFile(fullPath, searchText) ??
                    new FlxRange(new FlxPosition(0, 0), new FlxPosition(0, 1));

        return new FlxSymbolDefinition
        {
            Key = key,
            FullName = fullName,
            Kind = kind,
            Path = fullPath,
            Range = range
        };
    }

    private static FlxSymbolDefinition CreateBinaryPackageDefinition(
        LoadedBinaryPackage package,
        string key,
        string fullName,
        FlxSymbolKind kind,
        string? source,
        int line,
        int column,
        string sourceName,
        string metadataSearchText)
    {
        var sourcePath = ResolveBinaryPackageSourcePath(package, source);
        if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
        {
            FlxRange? range = line > 0 && column > 0
                ? RangeFromLocation(new SourceLocation(sourcePath, line, column, 0), sourceName.Length)
                : FindTextRangeInFile(sourcePath, sourceName);

            if (range is not null)
            {
                return new FlxSymbolDefinition
                {
                    Key = key,
                    FullName = fullName,
                    Kind = kind,
                    Path = Path.GetFullPath(sourcePath),
                    Range = range.Value
                };
            }
        }

        return CreateMetadataDefinition(
            key,
            fullName,
            kind,
            package.MetadataPath,
            metadataSearchText);
    }

    private static string? ResolveBinaryPackageSourcePath(LoadedBinaryPackage package, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        if (Path.IsPathRooted(source))
            return Path.GetFullPath(source);

        var metadataDirectory = Path.GetDirectoryName(Path.GetFullPath(package.MetadataPath)) ??
                                Directory.GetCurrentDirectory();

        if (!string.IsNullOrWhiteSpace(package.Metadata.SourceRoot))
        {
            var sourceRoot = Path.IsPathRooted(package.Metadata.SourceRoot)
                ? package.Metadata.SourceRoot
                : Path.Combine(metadataDirectory, package.Metadata.SourceRoot);

            return Path.GetFullPath(Path.Combine(sourceRoot, source));
        }

        return Path.GetFullPath(Path.Combine(metadataDirectory, source));
    }

    private static FlxRange? FindTextRangeInFile(string path, string searchText)
    {
        if (!File.Exists(path))
            return null;

        var text = File.ReadAllText(path);
        var position = text.IndexOf(searchText, StringComparison.Ordinal);
        if (position < 0)
            return null;

        var source = new SourceFile(path, text);
        return RangeFromLocation(source.GetLocation(position), searchText.Length);
    }

    private static FlxReference CreateReference(
        string path,
        FlxRange range,
        FlxReferenceKind kind,
        string targetKey,
        string targetFullName,
        FlxSymbolKind targetKind)
    {
        return new FlxReference
        {
            Path = path,
            Range = range,
            Kind = kind,
            TargetKey = targetKey,
            TargetFullName = targetFullName,
            TargetKind = targetKind
        };
    }

    private static FlxReference CreateMultiReference(
        string path,
        FlxRange range,
        FlxReferenceKind kind,
        string displayName,
        IReadOnlyList<string> targetKeys,
        IReadOnlyList<string> targetFullNames,
        FlxSymbolKind targetKind)
    {
        return new FlxReference
        {
            Path = path,
            Range = range,
            Kind = kind,
            TargetFullName = displayName,
            TargetKind = targetKind,
            TargetKeys = targetKeys,
            TargetFullNames = targetFullNames
        };
    }

    private static FlxRange RangeFromLocation(SourceLocation location, int length)
    {
        var start = ToPosition(location);
        return new FlxRange(start, new FlxPosition(start.Line, start.Character + Math.Max(1, length)));
    }

    private static FlxRange RangeFromOffsets(SourceFile source, int start, int end)
    {
        return new FlxRange(
            ToPosition(source.GetLocation(start)),
            ToPosition(source.GetLocation(end)));
    }

    private static string ComponentKey(string fullName) => "component:" + fullName;
    private static string FunctionKey(FunctionSymbol function) => "function:" + function.MangledName;
    private static string GlobalKey(string fullName) => "global:" + fullName;
    private static string ModuleKey(string fullName) => "module:" + fullName;
    private static string ParallelExternalKey(string path, string fullName) => "parallel-external:" + Path.GetFullPath(path) + ":" + fullName;
    private static string PrefabKey(string fullName) => "prefab:" + fullName;

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

    private static string FormatFunctionMetadata(FunctionMetadata function)
    {
        var parameters = string.Join(", ", function.Parameters.Select(parameter => $"{parameter.Type} {parameter.Name}"));
        var prefix = function.ReceiverType is null ? "function" : "method";
        return $"{prefix} {function.ReturnType} {function.FullName}({parameters})";
    }

    private static string ModuleNameFromFullName(string fullName, string sourceName)
    {
        var suffix = "." + sourceName;
        if (fullName.EndsWith(suffix, StringComparison.Ordinal))
            return fullName[..^suffix.Length];

        return "";
    }

    private static FlxDiagnostic ConvertDiagnostic(Diagnostic diagnostic)
    {
        if (diagnostic.Location is not { } location)
            return new FlxDiagnostic(diagnostic.Id, diagnostic.Message, null, 0, 0, 0, ConvertSeverity(diagnostic.Severity));

        return new FlxDiagnostic(
            diagnostic.Id,
            diagnostic.Message,
            location.FilePath,
            Math.Max(0, location.Line - 1),
            Math.Max(0, location.Column - 1),
            location.Position,
            ConvertSeverity(diagnostic.Severity));
    }

    private static FlxDiagnosticSeverity ConvertSeverity(Flx.Compiler.Diagnostics.DiagnosticSeverity severity)
    {
        return severity == Flx.Compiler.Diagnostics.DiagnosticSeverity.Warning
            ? FlxDiagnosticSeverity.Warning
            : FlxDiagnosticSeverity.Error;
    }

    private static FlxPosition ToPosition(SourceLocation location)
    {
        return new FlxPosition(Math.Max(0, location.Line - 1), Math.Max(0, location.Column - 1));
    }
}
