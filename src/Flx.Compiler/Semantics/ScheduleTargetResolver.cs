using Flx.Compiler.Frontend;

namespace Flx.Compiler.Semantics;

internal sealed class ScheduleTargetResolution
{
    public ScheduleTargetResolution(
        IReadOnlyList<FunctionSymbol> functions,
        bool isWildcard,
        bool isAmbiguous,
        IReadOnlyList<string>? candidateFullNames = null)
    {
        Functions = functions;
        IsWildcard = isWildcard;
        IsAmbiguous = isAmbiguous;
        CandidateFullNames = candidateFullNames ?? [];
    }

    public IReadOnlyList<FunctionSymbol> Functions { get; }
    public bool IsWildcard { get; }
    public bool IsAmbiguous { get; }
    public IReadOnlyList<string> CandidateFullNames { get; }
    public IReadOnlyList<string> FunctionGroupFullNames => Functions
        .Select(function => function.FullName)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();
}

internal static class ScheduleTargetResolver
{
    public static ScheduleTargetResolution Resolve(
        CompilationModel model,
        RunStepSyntax step,
        ModuleSymbol? currentModule)
    {
        return Resolve(model, step.Target, currentModule);
    }

    public static ScheduleTargetResolution Resolve(
        CompilationModel model,
        ScheduleTargetSyntax target,
        ModuleSymbol? currentModule)
    {
        return target.HasWildcard
            ? ResolveWildcard(model, target)
            : ResolveSingle(model, target.Text, currentModule);
    }

    private static ScheduleTargetResolution ResolveSingle(
        CompilationModel model,
        string targetName,
        ModuleSymbol? currentModule)
    {
        var functions = model.FunctionRegistry.ResolveFunctionGroup(targetName, currentModule, out var ambiguous);
        var candidates = ambiguous
            ? CandidateFullNames(model, targetName)
            : [];

        return new ScheduleTargetResolution(
            functions,
            isWildcard: false,
            isAmbiguous: ambiguous,
            candidateFullNames: candidates);
    }

    private static ScheduleTargetResolution ResolveWildcard(
        CompilationModel model,
        ScheduleTargetSyntax target)
    {
        var functions = model.FunctionRegistry.AllFunctions
            .GroupBy(function => function.FullName, StringComparer.Ordinal)
            .Where(group => Matches(target, group.First()))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .SelectMany(group => group.OrderBy(function => function.MangledName, StringComparer.Ordinal))
            .ToArray();

        return new ScheduleTargetResolution(
            functions,
            isWildcard: true,
            isAmbiguous: false);
    }

    private static bool Matches(ScheduleTargetSyntax target, FunctionSymbol function)
    {
        if (target.Segments.Count == 2 &&
            target.Segments[0].IsWildcard &&
            !target.Segments[1].IsWildcard)
        {
            return string.Equals(function.SourceName, target.Segments[1].Text, StringComparison.Ordinal);
        }

        var functionSegments = function.FullName.Split('.');
        if (functionSegments.Length != target.Segments.Count)
            return false;

        for (var index = 0; index < target.Segments.Count; index++)
        {
            var segment = target.Segments[index];
            if (segment.IsWildcard)
                continue;

            if (!string.Equals(segment.Text, functionSegments[index], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static IReadOnlyList<string> CandidateFullNames(CompilationModel model, string targetName)
    {
        if (targetName.Contains('.', StringComparison.Ordinal))
            return [];

        return model.FunctionRegistry
            .LookupSourceName(targetName)
            .Select(function => function.FullName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
