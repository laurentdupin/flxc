using Flx.Compiler.Codegen.C;
using Flx.Compiler.Diagnostics;
using Flx.Compiler.Frontend;

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
            BindFunctions(unit, module, model.FunctionRegistry);
            model.Schedules.AddRange(unit.Schedules);
        }

        CheckSchedules(model, requireSchedule, validateScheduleTargets);
        CheckFunctionBodies(model);

        return model;
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

    private void BindFunctions(CompilationUnitSyntax unit, ModuleSymbol module, FunctionRegistry registry)
    {
        foreach (var function in unit.Functions)
        {
            if (function.Name == "main")
                _diagnostics.Report("FLX0102", "function name 'main' is reserved when using schedule-generated main.", function.NameLocation);

            if (function.ReturnType != "void")
                _diagnostics.Report("FLX0106", $"MVP only supports void functions; found return type '{function.ReturnType}'.", function.DeclarationLocation);

            var parameters = function.Parameters
                .Select(parameter => new ParameterSymbol(parameter.Type, parameter.Name, parameter.Location))
                .ToArray();

            if (registry.ContainsExactSignature(function.Name, parameters))
                _diagnostics.Report("FLX0103", $"duplicate function signature '{FormatSignature(function.Name, parameters)}'.", function.NameLocation);

            var mangledName = CNameMangler.Mangle(unit.Source.DisplayPath, function.Name, parameters.Select(parameter => parameter.Type));
            var symbol = new FunctionSymbol(unit.Source, function, function.Name, mangledName, function.ReturnType, parameters, function.NameLocation);

            module.Functions.Add(symbol);
            registry.TryAdd(symbol);
        }
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

            foreach (var overload in overloads.Where(function => function.Parameters.Count > 0))
            {
                _diagnostics.Report(
                    "FLX0104",
                    $"run {step.Name} includes overload {FormatSignature(overload.SourceName, overload.Parameters)}, but object-parameter systems are not implemented yet.",
                    step.Location);
            }
        }
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
}
