using Flx.Compiler.Codegen.C;
using Flx.Compiler.Diagnostics;
using Flx.Compiler.Frontend;
using Flx.Compiler.Metadata;
using System.Text.RegularExpressions;

namespace Flx.Compiler.Semantics;

internal sealed class SemanticAnalyzer
{
    private static readonly HashSet<string> ReservedProgramArgumentSymbols = new(StringComparer.Ordinal)
    {
        "argc",
        "argv"
    };

    private readonly DiagnosticBag _diagnostics;

    public SemanticAnalyzer(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public CompilationModel Analyze(
        IReadOnlyList<CompilationUnitSyntax> units,
        bool requireSchedule,
        bool validateScheduleTargets,
        IReadOnlyList<PackageMetadata>? externalPackages = null)
    {
        var model = new CompilationModel();

        foreach (var unit in units)
        {
            var module = new ModuleSymbol(unit.Source, unit);
            model.Modules.Add(module);
            BindImports(unit, module);
            BindParallelExternalCalls(unit, module);
            model.Schedules.AddRange(unit.Schedules);
        }

        BindHiddenExternalSymbols(model, externalPackages ?? []);
        BindExternalComponents(model, externalPackages ?? []);

        foreach (var module in model.Modules)
            BindComponents(module, model);

        BindExternalPrefabs(model, externalPackages ?? []);

        foreach (var module in model.Modules)
            BindPrefabs(module, model);

        foreach (var module in model.Modules)
            BindGlobals(module.Syntax, module, model);

        BindExternalFunctions(model, externalPackages ?? [], model.FunctionRegistry);

        foreach (var module in model.Modules)
            BindFunctions(module.Syntax, module, model, model.FunctionRegistry);

        RegisterMethods(model);
        AnalyzeFunctionParallelism(model);
        CheckSchedules(model, requireSchedule, validateScheduleTargets);
        CheckFunctionBodies(model);

        return model;
    }

    private void BindComponents(ModuleSymbol module, CompilationModel model)
    {
        foreach (var component in module.Syntax.Components)
        {
            if (CheckReservedProgramArgumentSymbol(component.Name, component.NameLocation))
                continue;

            var fullName = CompilationModel.Qualify(module.Name, component.Name);
            if (model.ComponentsByFullName.ContainsKey(fullName))
            {
                _diagnostics.Report("FLX0302", $"duplicate component '{fullName}'.", component.NameLocation);
                continue;
            }

            var fields = ParseComponentFields(component, module.SourceFile);
            var symbol = new ComponentSymbol(module.SourceFile, component, component.Name, fullName, fields, component.IsExported);
            module.Components.Add(symbol);
            model.ComponentsByFullName.Add(symbol.FullName, symbol);
            AddByShortName(model.ComponentsByShortName, symbol.Name, symbol);
        }
    }

    private void BindExternalComponents(CompilationModel model, IReadOnlyList<PackageMetadata> externalPackages)
    {
        foreach (var package in externalPackages)
        {
            foreach (var header in package.Headers)
            {
                if (!string.IsNullOrWhiteSpace(header) && !model.ExternalHeaders.Contains(header, StringComparer.Ordinal))
                    model.ExternalHeaders.Add(header);
            }

            foreach (var component in package.Symbols.Components)
            {
                if (model.ComponentsByFullName.ContainsKey(component.FullName))
                {
                    _diagnostics.Report("FLX0302", $"duplicate component '{component.FullName}'.", ExternalLocation(package));
                    continue;
                }

                var sourceFile = ExternalSourceFile(package);
                var syntax = new ComponentDeclSyntax(component.Name, "{}", 0, ExternalLocation(package), ExternalLocation(package), isExported: true);
                var fields = component.Fields.Select(field => new ComponentFieldSymbol(
                    field.Type,
                    field.Name,
                    field.DefaultValue,
                    ExternalLocation(package))).ToArray();
                var symbol = new ComponentSymbol(sourceFile, syntax, component.Name, component.FullName, fields, isExported: true);

                model.ComponentsByFullName.Add(symbol.FullName, symbol);
                AddByShortName(model.ComponentsByShortName, symbol.Name, symbol);
            }
        }
    }

    private IReadOnlyList<ComponentFieldSymbol> ParseComponentFields(ComponentDeclSyntax component, SourceFile sourceFile)
    {
        var fields = new List<ComponentFieldSymbol>();
        var content = StripOuterBlock(component.BodyText);
        var pattern = new Regex(
            @"(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:=\s*(?<default>""(?:\\.|[^""\\])*""))?\s*;",
            RegexOptions.Multiline);

        foreach (Match match in pattern.Matches(content))
        {
            var type = match.Groups["type"].Value;
            var name = match.Groups["name"].Value;
            var defaultValue = match.Groups["default"].Success ? match.Groups["default"].Value : null;
            var location = sourceFile.GetLocation(component.BodyStart + 1 + match.Groups["name"].Index);

            if (type != "string")
                _diagnostics.Report("FLX0303", $"component field type '{type}' is not implemented yet.", location);

            fields.Add(new ComponentFieldSymbol(type, name, defaultValue, location));
        }

        if (fields.Count == 0 && !string.IsNullOrWhiteSpace(content))
            _diagnostics.Report("FLX0304", $"component '{component.Name}' contains unsupported field declarations.", component.Location);

        return fields;
    }

    private void BindPrefabs(ModuleSymbol module, CompilationModel model)
    {
        foreach (var prefab in module.Syntax.Prefabs)
        {
            if (CheckReservedProgramArgumentSymbol(prefab.Name, prefab.NameLocation))
                continue;

            var fullName = CompilationModel.Qualify(module.Name, prefab.Name);
            if (model.PrefabsByFullName.ContainsKey(fullName))
            {
                _diagnostics.Report("FLX0305", $"duplicate prefab '{fullName}'.", prefab.NameLocation);
                continue;
            }

            var flattened = new List<ComponentSymbol>();
            var content = StripOuterBlock(prefab.BodyText);
            var pattern = new Regex(@"\bflatten\s+(?<name>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*;", RegexOptions.Multiline);

            foreach (Match match in pattern.Matches(content))
            {
                var componentName = match.Groups["name"].Value;
                var componentLocation = module.SourceFile.GetLocation(prefab.BodyStart + 1 + match.Groups["name"].Index);
                var component = model.ResolveComponent(componentName, module);
                if (component is null)
                {
                    _diagnostics.Report(
                        model.IsAmbiguousComponentName(componentName, module) ? "FLX0404" : "FLX0306",
                        model.IsAmbiguousComponentName(componentName, module)
                            ? $"component name '{componentName}' is ambiguous."
                            : $"flatten target component '{componentName}' does not exist.",
                        componentLocation);
                    continue;
                }

                flattened.Add(component);
            }

            if (flattened.Count == 0)
                _diagnostics.Report("FLX0307", $"prefab '{prefab.Name}' must flatten at least one component.", prefab.Location);

            var duplicateFields = flattened
                .SelectMany(component => component.Fields)
                .GroupBy(field => field.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var duplicate in duplicateFields)
                _diagnostics.Report("FLX0308", $"prefab '{prefab.Name}' has duplicate flattened field '{duplicate.Key}'.", prefab.Location);

            var symbol = new PrefabSymbol(module.SourceFile, prefab, prefab.Name, fullName, flattened, prefab.IsExported);
            module.Prefabs.Add(symbol);
            model.PrefabsByFullName.Add(symbol.FullName, symbol);
            AddByShortName(model.PrefabsByShortName, symbol.Name, symbol);
        }
    }

    private void BindExternalPrefabs(CompilationModel model, IReadOnlyList<PackageMetadata> externalPackages)
    {
        foreach (var package in externalPackages)
        {
            foreach (var prefab in package.Symbols.Prefabs)
            {
                if (model.PrefabsByFullName.ContainsKey(prefab.FullName))
                {
                    _diagnostics.Report("FLX0305", $"duplicate prefab '{prefab.FullName}'.", ExternalLocation(package));
                    continue;
                }

                var flattened = new List<ComponentSymbol>();
                foreach (var componentName in prefab.FlattenedComponents)
                {
                    if (model.ComponentsByFullName.TryGetValue(componentName, out var component))
                    {
                        flattened.Add(component);
                    }
                    else
                    {
                        _diagnostics.Report(
                            "FLX0306",
                            $"binary package prefab '{prefab.FullName}' references missing component '{componentName}'.",
                            ExternalLocation(package));
                    }
                }

                var sourceFile = ExternalSourceFile(package);
                var syntax = new PrefabDeclSyntax(prefab.Name, "{}", 0, ExternalLocation(package), ExternalLocation(package), isExported: true);
                var symbol = new PrefabSymbol(sourceFile, syntax, prefab.Name, prefab.FullName, flattened, isExported: true);

                model.PrefabsByFullName.Add(symbol.FullName, symbol);
                AddByShortName(model.PrefabsByShortName, symbol.Name, symbol);
            }
        }
    }

    private void BindImports(CompilationUnitSyntax unit, ModuleSymbol module)
    {
        foreach (var import in unit.CImports)
        {
            if (CheckReservedProgramArgumentSymbol(import.Alias, import.Location))
                continue;

            if (module.CImportsByAlias.ContainsKey(import.Alias))
            {
                _diagnostics.Report("FLX0201", $"duplicate C import alias '{import.Alias}'.", import.Location);
                continue;
            }

            var symbol = new CImportSymbol(import.Header, import.Alias, import.Location);
            module.CImports.Add(symbol);
            module.CImportsByAlias.Add(symbol.Alias, symbol);
        }
    }

    private void BindParallelExternalCalls(CompilationUnitSyntax unit, ModuleSymbol module)
    {
        foreach (var parallel in unit.ParallelExternalCalls)
        {
            if (string.IsNullOrWhiteSpace(parallel.Alias) ||
                string.IsNullOrWhiteSpace(parallel.Name))
            {
                continue;
            }

            if (!module.CImportsByAlias.ContainsKey(parallel.Alias))
            {
                _diagnostics.Report("FLX0702", $"unknown C import alias '{parallel.Alias}'.", parallel.TargetLocation);
                continue;
            }

            if (module.ParallelExternalCallsByName.ContainsKey(parallel.FullName))
                continue;

            var symbol = new ParallelExternalSymbol(parallel.Alias, parallel.Name, parallel.TargetLocation);
            module.ParallelExternalCalls.Add(symbol);
            module.ParallelExternalCallsByName.Add(symbol.FullName, symbol);
        }
    }

    private void BindFunctions(CompilationUnitSyntax unit, ModuleSymbol module, CompilationModel model, FunctionRegistry registry)
    {
        foreach (var function in unit.Functions)
        {
            if (CheckReservedProgramArgumentSymbol(function.Name, function.NameLocation))
                continue;

            if (function.Name == "main")
                _diagnostics.Report("FLX0102", "function name 'main' is reserved when using schedule-generated main.", function.NameLocation);

            var returnType = ResolveTypeName(function.ReturnType, module, model, function.DeclarationLocation);

            var parameters = function.Parameters
                .Select(parameter => new ParameterSymbol(ResolveTypeName(parameter.Type, module, model, parameter.Location), parameter.Name, parameter.Location))
                .ToArray();

            foreach (var parameter in parameters)
                CheckReservedProgramArgumentSymbol(parameter.Name, parameter.Location);

            var fullName = CompilationModel.Qualify(module.Name, function.Name);
            if (registry.ContainsExactSignature(fullName, parameters))
                _diagnostics.Report("FLX0103", $"duplicate function signature '{FormatSignature(fullName, parameters)}'.", function.NameLocation);

            var mangledName = CNameMangler.Mangle(
                unit.Source.DisplayPath,
                function.Name,
                parameters.Select(parameter => CTypeNames.MapType(parameter.Type, model, module)));
            var symbol = new FunctionSymbol(
                module,
                unit.Source,
                function,
                function.Name,
                fullName,
                mangledName,
                returnType,
                parameters,
                function.NameLocation,
                isExported: function.IsExported);

            module.Functions.Add(symbol);
            registry.TryAdd(symbol);
        }
    }

    private void BindExternalFunctions(
        CompilationModel model,
        IReadOnlyList<PackageMetadata> externalPackages,
        FunctionRegistry registry)
    {
        foreach (var package in externalPackages)
        {
            foreach (var function in package.Symbols.Functions)
            {
                var moduleName = ModuleNameFromFullName(function.FullName, function.SourceName);
                var sourceFile = ExternalSourceFile(package);
                var unit = new CompilationUnitSyntax(sourceFile)
                {
                    Module = string.IsNullOrWhiteSpace(moduleName)
                        ? null
                        : new ModuleDeclSyntax(moduleName, ExternalLocation(package))
                };
                var module = new ModuleSymbol(sourceFile, unit);
                var syntax = new FunctionDeclSyntax(
                    function.ReturnType,
                    function.SourceName,
                    function.Parameters.Select(parameter => new ParameterSyntax(parameter.Type, parameter.Name, ExternalLocation(package))).ToArray(),
                    "{}",
                    0,
                    ExternalLocation(package),
                    ExternalLocation(package),
                    isExported: true);
                var parameters = function.Parameters.Select(parameter =>
                    new ParameterSymbol(parameter.Type, parameter.Name, ExternalLocation(package))).ToArray();

                if (registry.ContainsExactSignature(function.FullName, parameters))
                    _diagnostics.Report("FLX0103", $"duplicate function signature '{FormatSignature(function.FullName, parameters)}'.", ExternalLocation(package));

                var symbol = new FunctionSymbol(
                    module,
                    sourceFile,
                    syntax,
                    function.SourceName,
                    function.FullName,
                    function.MangledName,
                    function.ReturnType,
                    parameters,
                    ExternalLocation(package),
                    isExternal: true,
                    isExported: true)
                {
                    ReceiverType = function.ReceiverType,
                    ParallelInfo = function.Parallelizable
                        ? FunctionParallelInfo.Parallel()
                        : FunctionParallelInfo.Serial(function.ParallelReason ?? "external binary package function")
                };

                registry.TryAdd(symbol);
            }
        }
    }

    private void BindGlobals(CompilationUnitSyntax unit, ModuleSymbol module, CompilationModel model)
    {
        foreach (var global in unit.Globals)
        {
            if (CheckReservedProgramArgumentSymbol(global.Name, global.NameLocation))
                continue;

            var type = ResolveTypeName(global.Type, module, model, global.DeclarationLocation);

            var fullName = CompilationModel.Qualify(module.Name, global.Name);
            if (model.GlobalsByFullName.ContainsKey(fullName))
            {
                _diagnostics.Report("FLX0110", $"duplicate global variable '{fullName}'.", global.NameLocation);
                continue;
            }

            if (model.GlobalsByShortName.ContainsKey(global.Name))
            {
                _diagnostics.Report("FLX0110", $"duplicate global variable '{global.Name}'.", global.NameLocation);
                continue;
            }

            var symbol = new GlobalVariableSymbol(module, unit.Source, global, type, global.Name, fullName, global.Initializer, global.NameLocation, global.IsExported);
            module.Globals.Add(symbol);
            model.GlobalsByFullName.Add(symbol.FullName, symbol);
            AddByShortName(model.GlobalsByShortName, symbol.Name, symbol);
        }
    }

    private void RegisterMethods(CompilationModel model)
    {
        foreach (var function in model.FunctionRegistry.AllFunctions)
        {
            if (function.Parameters.Count == 0)
                continue;

            var receiverType = function.Parameters[0].Type;
            if (!model.PrefabsByFullName.TryGetValue(receiverType, out var receiver))
                continue;

            function.ReceiverType = receiver.FullName;
            if (!model.MethodRegistry.TryAdd(function, receiver, out var existing))
            {
                _diagnostics.Report(
                    "FLX0407",
                    $"duplicate method '{function.SourceName}' for receiver '{receiver.FullName}'.",
                    function.Location);

                if (existing is not null)
                {
                    _diagnostics.Report(
                        "FLX0407",
                        $"previous method '{existing.SourceName}' for receiver '{receiver.FullName}' is here.",
                        existing.Location);
                }
            }
        }
    }

    private void AnalyzeFunctionParallelism(CompilationModel model)
    {
        foreach (var function in model.FunctionRegistry.AllFunctions)
        {
            if (function.IsExternal)
                continue;

            function.ParallelInfo = AnalyzeFunctionParallelism(function, model);
        }
    }

    private static FunctionParallelInfo AnalyzeFunctionParallelism(FunctionSymbol function, CompilationModel model)
    {
        var body = function.Syntax.BodyText;
        if (Regex.IsMatch(body, @"\bcreate\s+[A-Za-z_]", RegexOptions.Multiline))
            return FunctionParallelInfo.Serial("function creates objects");

        if (Regex.IsMatch(body, @"\bdestroy\s+[A-Za-z_]", RegexOptions.Multiline))
            return FunctionParallelInfo.Serial("function destroys objects");

        if (Regex.IsMatch(body, @"\breparent\s+[A-Za-z_]", RegexOptions.Multiline))
            return FunctionParallelInfo.Serial("function reparents objects");

        if (function.Parameters.Count != 1)
            return FunctionParallelInfo.Serial("function does not have exactly one prefab parameter");

        var receiverType = function.Parameters[0].Type;
        if (!model.PrefabsByFullName.ContainsKey(receiverType))
            return FunctionParallelInfo.Serial("function parameter is not a prefab");

        var receiverName = Regex.Escape(function.Parameters[0].Name);
        if (Regex.IsMatch(body, $@"\b{receiverName}\.[A-Za-z_][A-Za-z0-9_]*\s*=(?!=)", RegexOptions.Multiline))
            return FunctionParallelInfo.Serial("function assigns prefab fields");

        foreach (var global in model.GlobalsByFullName.Values)
        {
            if (Regex.IsMatch(body, $@"\b{Regex.Escape(global.Name)}\s*=(?!=)", RegexOptions.Multiline))
                return FunctionParallelInfo.Serial($"function writes global '{global.FullName}'");
        }

        foreach (Match match in Regex.Matches(
                     body,
                     @"\b(?<alias>[A-Za-z_][A-Za-z0-9_]*)\.(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
                     RegexOptions.Multiline))
        {
            var alias = match.Groups["alias"].Value;
            if (!function.Module.CImportsByAlias.ContainsKey(alias))
                continue;

            var externalName = alias + "." + match.Groups["name"].Value;
            if (!function.Module.ParallelExternalCallsByName.ContainsKey(externalName))
                return FunctionParallelInfo.Serial($"calls external function '{externalName}' that is not marked parallel");
        }

        return FunctionParallelInfo.Parallel();
    }

    private string ResolveTypeName(string typeName, ModuleSymbol module, CompilationModel model, SourceLocation location)
    {
        if (typeName is "void" or "i32" or "usize" or "string" or "Array<string>")
            return typeName;

        var dotIndex = typeName.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex > 0)
        {
            var alias = typeName[..dotIndex];
            if (module.CImportsByAlias.ContainsKey(alias))
                return typeName;
        }

        if (model.ResolvePrefab(typeName, module) is { } prefab)
            return prefab.FullName;

        if (model.IsAmbiguousPrefabName(typeName, module))
            _diagnostics.Report("FLX0404", $"type name '{typeName}' is ambiguous.", location);
        else if (model.ResolveComponent(typeName, module) is { } component)
            return component.FullName;
        else if (model.IsAmbiguousComponentName(typeName, module))
            _diagnostics.Report("FLX0404", $"type name '{typeName}' is ambiguous.", location);
        else if (dotIndex > 0)
        {
            if (model.HiddenExternalSymbols.Contains(typeName))
                _diagnostics.Report("FLX0603", $"symbol '{typeName}' is not visible from this package.", location);
            else
                _diagnostics.Report("FLX0405", $"type name '{typeName}' does not exist.", location);
        }

        return typeName;
    }

    private static void BindHiddenExternalSymbols(CompilationModel model, IReadOnlyList<PackageMetadata> externalPackages)
    {
        foreach (var package in externalPackages)
        {
            foreach (var symbol in package.HiddenSymbols)
                model.HiddenExternalSymbols.Add(symbol);
        }
    }

    private bool CheckReservedProgramArgumentSymbol(string name, SourceLocation location)
    {
        if (!ReservedProgramArgumentSymbols.Contains(name))
            return false;

        _diagnostics.Report("FLX0301", $"'{name}' is a reserved program argument symbol.", location);
        return true;
    }

    private void CheckSchedules(CompilationModel model, bool requireSchedule, bool validateScheduleTargets)
    {
        if (model.Schedules.Count > 1)
        {
            foreach (var schedule in model.Schedules.Skip(1))
                _diagnostics.Report("FLX0100", "more than one schedule block found.", schedule.Location);
        }

        if (model.Schedules.Count == 0)
        {
            if (requireSchedule)
                _diagnostics.Report("FLX0105", "executable build requires exactly one schedule block.");
            return;
        }

        if (model.Schedules.Count != 1)
            return;

        if (!validateScheduleTargets)
            return;

        foreach (var step in model.Schedules[0].Steps)
        {
            if (step is LabelStepSyntax or LoopToStepSyntax)
                continue;

            var runStep = (RunStepSyntax)step;
            var scheduleModule = model.ScheduleModule;
            var resolution = ScheduleTargetResolver.Resolve(model, runStep, scheduleModule);
            if (resolution.IsAmbiguous)
            {
                var candidates = resolution.CandidateFullNames.Count == 0
                    ? ""
                    : " candidates: " + string.Join(", ", resolution.CandidateFullNames);
                _diagnostics.Report("FLX0406", $"schedule target '{runStep.Name}' is ambiguous.{candidates}", runStep.Location);
                continue;
            }

            if (resolution.Functions.Count == 0)
            {
                var message = resolution.IsWildcard
                    ? $"wildcard schedule target '{runStep.Name}' matched no function groups."
                    : $"run target '{runStep.Name}' does not exist.";
                _diagnostics.Report("FLX0101", message, runStep.Location);
                continue;
            }

            foreach (var overload in resolution.Functions.Where(function => !IsRunnableInMvp(function, model)))
            {
                _diagnostics.Report(
                    "FLX0104",
                    $"run {runStep.Name} includes overload {FormatSignature(overload.SourceName, overload.Parameters)}, but object-parameter systems are not implemented yet.",
                    runStep.Location);
            }
        }

        CheckDuplicateRunTargets(model);
        CheckScheduleLabels(model.Schedules[0]);
    }

    private void CheckDuplicateRunTargets(CompilationModel model)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var runStep in model.Schedules[0].Steps.OfType<RunStepSyntax>())
        {
            var resolution = ScheduleTargetResolver.Resolve(model, runStep, model.ScheduleModule);
            if (resolution.Functions.Count == 0 || resolution.IsAmbiguous)
                continue;

            foreach (var fullName in resolution.FunctionGroupFullNames)
            {
                counts.TryGetValue(fullName, out var count);
                counts[fullName] = count + 1;
                if (count > 0)
                {
                    _diagnostics.ReportWarning(
                        "FLX0408",
                        $"schedule target '{fullName}' is included more than once.",
                        runStep.Location);
                }
            }
        }
    }

    private void CheckScheduleLabels(ScheduleDeclSyntax schedule)
    {
        var labels = new Dictionary<string, LabelStepSyntax>(StringComparer.Ordinal);
        foreach (var label in schedule.Steps.OfType<LabelStepSyntax>())
        {
            if (!labels.TryAdd(label.Name, label))
                _diagnostics.Report("FLX0111", $"duplicate schedule label '{label.Name}'.", label.Location);
        }

        foreach (var loopTo in schedule.Steps.OfType<LoopToStepSyntax>())
        {
            if (!labels.ContainsKey(loopTo.TargetLabel))
                _diagnostics.Report("FLX0112", $"loopto target label '{loopTo.TargetLabel}' does not exist.", loopTo.Location);
        }
    }

    private static bool IsRunnableInMvp(FunctionSymbol function, CompilationModel model)
    {
        if (function.Parameters.Count == 0)
            return true;

        return function.Parameters.Count == 1 && model.PrefabsByFullName.ContainsKey(function.Parameters[0].Type);
    }

    private void CheckFunctionBodies(CompilationModel model)
    {
        foreach (var module in model.Modules)
        {
            foreach (var function in module.Functions)
            {
                CBodyRewriter.ValidateAliases(
                    function.Syntax.BodyText,
                    module.CImportsByAlias,
                    function.SourceFile,
                    function.Syntax.BodyStart,
                    _diagnostics);

                CheckUnsupportedStringConcatenation(function);
            }
        }
    }

    private void CheckUnsupportedStringConcatenation(FunctionSymbol function)
    {
        foreach (Match match in Regex.Matches(
                     function.Syntax.BodyText,
                     @"(?<target>[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>""(?:\\.|[^""\\])*""\s*\+[^;]+)\s*;",
                     RegexOptions.Multiline))
        {
            var value = match.Groups["value"].Value;
            var supported = Regex.IsMatch(
                value,
                @"^""(?:\\.|[^""\\])*""\s*\+\s*[A-Za-z_][A-Za-z0-9_]*$");
            if (supported)
                continue;

            var location = function.SourceFile.GetLocation(function.Syntax.BodyStart + match.Groups["value"].Index);
            _diagnostics.Report(
                "FLX0703",
                "unsupported string concatenation form; only string-literal + integer-variable is implemented.",
                location);
        }
    }

    private static string FormatSignature(string name, IReadOnlyList<ParameterSymbol> parameters)
    {
        if (parameters.Count == 0)
            return $"{name}()";

        return $"{name}({string.Join(", ", parameters.Select(parameter => parameter.Type))})";
    }

    private static SourceFile ExternalSourceFile(PackageMetadata package)
    {
        var path = $"binary package {package.Name}";
        return new SourceFile(path, "", packageName: package.Name);
    }

    private static SourceLocation ExternalLocation(PackageMetadata package)
    {
        return new SourceLocation($"binary package {package.Name}", 1, 1, 0);
    }

    private static string ModuleNameFromFullName(string fullName, string sourceName)
    {
        var suffix = "." + sourceName;
        if (fullName.EndsWith(suffix, StringComparison.Ordinal))
            return fullName[..^suffix.Length];

        return "";
    }

    private static string StripOuterBlock(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '{' && trimmed[^1] == '}'
            ? trimmed[1..^1]
            : text;
    }

    private static void AddByShortName<T>(Dictionary<string, List<T>> dictionary, string shortName, T symbol)
    {
        if (!dictionary.TryGetValue(shortName, out var symbols))
        {
            symbols = [];
            dictionary.Add(shortName, symbols);
        }

        symbols.Add(symbol);
    }
}
