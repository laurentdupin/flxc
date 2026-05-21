using Flx.Compiler.Codegen.C;
using Flx.Compiler.Diagnostics;
using Flx.Compiler.Frontend;
using System.Text.RegularExpressions;

namespace Flx.Compiler.Semantics;

internal sealed class SemanticAnalyzer
{
    private readonly DiagnosticBag _diagnostics;

    public SemanticAnalyzer(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public CompilationModel Analyze(IReadOnlyList<CompilationUnitSyntax> units, bool requireSchedule, bool validateScheduleTargets)
    {
        var model = new CompilationModel();

        foreach (var unit in units)
        {
            var module = new ModuleSymbol(unit.Source, unit);
            model.Modules.Add(module);
            BindImports(unit, module);
            model.Schedules.AddRange(unit.Schedules);
        }

        foreach (var module in model.Modules)
            BindComponents(module, model);

        foreach (var module in model.Modules)
            BindPrefabs(module, model);

        foreach (var module in model.Modules)
            BindFunctions(module.Syntax, module, model, model.FunctionRegistry);

        CheckSchedules(model, requireSchedule, validateScheduleTargets);
        CheckFunctionBodies(model);

        return model;
    }

    private void BindComponents(ModuleSymbol module, CompilationModel model)
    {
        foreach (var component in module.Syntax.Components)
        {
            if (model.ComponentsByName.ContainsKey(component.Name))
            {
                _diagnostics.Report("FLX0302", $"duplicate component '{component.Name}'.", component.NameLocation);
                continue;
            }

            var fields = ParseComponentFields(component, module.SourceFile);
            var symbol = new ComponentSymbol(module.SourceFile, component, component.Name, fields);
            module.Components.Add(symbol);
            model.ComponentsByName.Add(symbol.Name, symbol);
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
            if (model.PrefabsByName.ContainsKey(prefab.Name))
            {
                _diagnostics.Report("FLX0305", $"duplicate prefab '{prefab.Name}'.", prefab.NameLocation);
                continue;
            }

            var flattened = new List<ComponentSymbol>();
            var content = StripOuterBlock(prefab.BodyText);
            var pattern = new Regex(@"\bflatten\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*;", RegexOptions.Multiline);

            foreach (Match match in pattern.Matches(content))
            {
                var componentName = match.Groups["name"].Value;
                if (!model.ComponentsByName.TryGetValue(componentName, out var component))
                {
                    _diagnostics.Report(
                        "FLX0306",
                        $"flatten target component '{componentName}' does not exist.",
                        module.SourceFile.GetLocation(prefab.BodyStart + 1 + match.Groups["name"].Index));
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

            var symbol = new PrefabSymbol(module.SourceFile, prefab, prefab.Name, flattened);
            module.Prefabs.Add(symbol);
            model.PrefabsByName.Add(symbol.Name, symbol);
        }
    }

    private void BindImports(CompilationUnitSyntax unit, ModuleSymbol module)
    {
        foreach (var import in unit.CImports)
        {
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

    private void BindFunctions(CompilationUnitSyntax unit, ModuleSymbol module, CompilationModel model, FunctionRegistry registry)
    {
        foreach (var function in unit.Functions)
        {
            if (function.Name == "main")
                _diagnostics.Report("FLX0102", "function name 'main' is reserved when using schedule-generated main.", function.NameLocation);

            CheckTypeAlias(function.ReturnType, module, function.DeclarationLocation);

            var parameters = function.Parameters
                .Select(parameter => new ParameterSymbol(parameter.Type, parameter.Name, parameter.Location))
                .ToArray();

            foreach (var parameter in parameters)
                CheckTypeAlias(parameter.Type, module, parameter.Location);

            if (registry.ContainsExactSignature(function.Name, parameters))
                _diagnostics.Report("FLX0103", $"duplicate function signature '{FormatSignature(function.Name, parameters)}'.", function.NameLocation);

            var mangledName = CNameMangler.Mangle(
                unit.Source.DisplayPath,
                function.Name,
                parameters.Select(parameter => CTypeNames.MapType(parameter.Type, model, module)));
            var symbol = new FunctionSymbol(module, unit.Source, function, function.Name, mangledName, function.ReturnType, parameters, function.NameLocation);

            module.Functions.Add(symbol);
            registry.TryAdd(symbol);
        }
    }

    private void CheckTypeAlias(string typeName, ModuleSymbol module, SourceLocation location)
    {
        var dotIndex = typeName.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex <= 0)
            return;

        var alias = typeName[..dotIndex];
        if (!module.CImportsByAlias.ContainsKey(alias))
            _diagnostics.Report("FLX0200", $"unknown C import alias '{alias}'.", location);
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
            var overloads = model.FunctionRegistry.LookupSourceName(step.Name);
            if (overloads.Count == 0)
            {
                _diagnostics.Report("FLX0101", $"run target '{step.Name}' does not exist.", step.Location);
                continue;
            }

            foreach (var overload in overloads.Where(function => !IsRunnableInMvp(function, model)))
            {
                _diagnostics.Report(
                    "FLX0104",
                    $"run {step.Name} includes overload {FormatSignature(overload.SourceName, overload.Parameters)}, but object-parameter systems are not implemented yet.",
                    step.Location);
            }
        }
    }

    private static bool IsRunnableInMvp(FunctionSymbol function, CompilationModel model)
    {
        if (function.Parameters.Count == 0)
            return true;

        return function.Parameters.Count == 1 && model.PrefabsByName.ContainsKey(function.Parameters[0].Type);
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
            }
        }
    }

    private static string FormatSignature(string name, IReadOnlyList<ParameterSymbol> parameters)
    {
        if (parameters.Count == 0)
            return $"{name}()";

        return $"{name}({string.Join(", ", parameters.Select(parameter => parameter.Type))})";
    }

    private static string StripOuterBlock(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '{' && trimmed[^1] == '}'
            ? trimmed[1..^1]
            : text;
    }
}
